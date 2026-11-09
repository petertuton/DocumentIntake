#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.7.1' }
BeforeAll {
    . (Join-Path $PSScriptRoot 'register-analyzers.ps1')
    function az { }
    function azd { }
}

Describe 'Analyzer registration' -Tag 'Unit' {
    BeforeEach {
        $script:Principal = '11111111-1111-1111-1111-111111111111'
        $script:Scope = '/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/foundry'
        $script:Parameters = @{
            Endpoint = 'https://foundry.cognitiveservices.azure.com/'; SubscriptionId = 'sub'; TenantId = 'tenant'
            ResourceGroup = 'rg'; FoundryAccountName = 'foundry'; PrincipalId = $script:Principal; PrincipalType = 'User'
        }
        $script:Assignments = @(@{ principalId = $script:Principal; roleDefinitionId = '/roles/a97b65f3-24c7-4388-baec-2e87135dc908'; scope = $script:Scope })
        $script:StatusCodes = [Collections.Generic.Queue[int]]::new()
        $script:StatusCodes.Enqueue(200)
        $script:Calls = [Collections.Generic.List[object]]::new()
        Mock Write-Host { }
        Mock Start-Sleep { }
        Mock Resolve-DeploymentValue { $Value }
        Mock Invoke-DeploymentCommand {
            $global:LASTEXITCODE = 0
            $Output = & az @ArgumentList
            @{ ExitCode = $global:LASTEXITCODE; Output = $Output }
        }
        Mock az {
            $global:LASTEXITCODE = 0
            if ($args[0] -eq 'account' -and $args[1] -eq 'show') { return '{"id":"sub","tenantId":"tenant","user":{"type":"user","name":"user@example.test"}}' }
            if ($args[0] -eq 'ad') { return $script:Principal }
            if ($args[0] -eq 'cognitiveservices') { return (@{ id = $script:Scope; properties = @{ endpoint = 'https://foundry.cognitiveservices.azure.com/' } } | ConvertTo-Json) }
            if ($args[2] -eq 'list') { return ConvertTo-Json -InputObject $script:Assignments -Depth 5 }
            if ($args[1] -eq 'get-access-token') { return 'fake-secret-token' }
        }
        Mock Invoke-WebRequest {
            $script:Calls.Add(@{ Method = $Method; Uri = $Uri; Body = $Body })
            $Status = if ($script:StatusCodes.Count -gt 1) { $script:StatusCodes.Dequeue() } else { $script:StatusCodes.Peek() }
            return @{ StatusCode = $Status; Headers = @{}; Content = 'sensitive-response' }
        }
    }

    It 'Reuses a manual role and preserves defaults, model injection, and request order' {
        Register-IntakeAnalyzer @script:Parameters
        Should -Invoke az -Times 0 -ParameterFilter { $args[2] -eq 'create' }
        $script:Calls.Count | Should -Be 3
        $script:Calls[0].Method | Should -Be Patch
        $script:Calls[0].Uri | Should -BeLike '*defaults?api-version=2025-11-01'
        ($script:Calls[0].Body | ConvertFrom-Json).modelDeployments.'prebuilt-analyzer-completion' | Should -Be 'gpt-5-mini'
        $script:Calls[1].Uri | Should -BeLike '*analyzers/document_intake_classifier?*&allowReplace=true'
        $script:Calls[2].Uri | Should -BeLike '*analyzers/document_intake_form_analyzer?*&allowReplace=true'
        ($script:Calls[2].Body | ConvertFrom-Json).models.embedding | Should -Be 'text-embedding-3-large'
        Should -Invoke Invoke-WebRequest -Times 3 -Exactly -ParameterFilter { $MaximumRedirection -eq 0 -and $TimeoutSec -le 30 -and $Headers.Authorization -eq 'Bearer fake-secret-token' }
        Should -Invoke az -Times 1 -Exactly -ParameterFilter { $args[1] -eq 'get-access-token' -and $args -contains '--subscription' -and $args -contains 'sub' }
        Should -Invoke az -Times 0 -ParameterFilter { $args -notcontains '--subscription' }
    }

    It 'Preserves explicit analyzer IDs, API version, and model deployments' {
        Register-IntakeAnalyzer @script:Parameters -ClassifierId custom_class -AnalyzerId custom_form -ApiVersion custom_version -CompletionDeployment custom_completion -EmbeddingDeployment custom_embedding
        $script:Calls[1].Uri | Should -BeLike '*analyzers/custom_class?api-version=custom_version&allowReplace=true'
        $script:Calls[2].Uri | Should -BeLike '*analyzers/custom_form?*'
        ($script:Calls[2].Body | ConvertFrom-Json).models.completion | Should -Be 'custom_completion'
    }

    It 'Fails a role lookup without creating a role or calling the service' {
        Mock az { $global:LASTEXITCODE = 1 } -ParameterFilter { $args[2] -eq 'list' }
        { Register-IntakeAnalyzer @script:Parameters } | Should -Throw '*Foundry role lookup failed*'
        Should -Invoke az -Times 0 -ParameterFilter { $args[2] -eq 'create' }
        Should -Invoke Invoke-WebRequest -Times 0
    }

    It 'Fails token acquisition without sending HTTP requests' {
        Mock az { $global:LASTEXITCODE = 1; 'do-not-log-this-token' } -ParameterFilter { $args[1] -eq 'get-access-token' }
        { Register-IntakeAnalyzer @script:Parameters } | Should -Throw '*token acquisition failed*'
        Should -Invoke Invoke-WebRequest -Times 0
    }

    It 'Reuses an inherited assignment' {
        $script:Assignments[0].scope = '/subscriptions/sub'
        Register-IntakeAnalyzer @script:Parameters
        Should -Invoke az -Times 0 -ParameterFilter { $args[2] -eq 'create' }
    }

    It 'Reuses a management-group assignment returned by the inherited-scope query' {
        $script:Assignments[0].scope = '/providers/Microsoft.Management/managementGroups/parent'
        Register-IntakeAnalyzer @script:Parameters
        Should -Invoke az -Times 0 -ParameterFilter { $args[2] -eq 'create' }
    }

    It 'Creates a missing role only for the object ID and Foundry account scope' {
        $script:Assignments = @()
        Register-IntakeAnalyzer @script:Parameters
        Should -Invoke az -Times 1 -Exactly -ParameterFilter {
            $args[2] -eq 'create' -and $args -contains '--assignee-object-id' -and $args -contains $script:Principal -and
            $args -contains 'User' -and $args -contains $script:Scope -and $args -contains 'sub' -and
            $args -contains 'a97b65f3-24c7-4388-baec-2e87135dc908'
        }
    }

    It 'Resolves a service principal by client ID but assigns its object ID' {
        $script:Parameters.PrincipalType = 'ServicePrincipal'
        Mock az { $global:LASTEXITCODE = 0; '{"id":"sub","tenantId":"tenant","user":{"type":"servicePrincipal","name":"client-id"}}' } -ParameterFilter { $args[0] -eq 'account' -and $args[1] -eq 'show' }
        $script:Assignments = @()
        Register-IntakeAnalyzer @script:Parameters
        Should -Invoke az -Times 1 -Exactly -ParameterFilter { $args[0] -eq 'ad' -and $args[1] -eq 'sp' -and $args -contains 'client-id' }
        Should -Invoke az -Times 1 -Exactly -ParameterFilter { $args[2] -eq 'create' -and $args -contains 'ServicePrincipal' -and $args -contains $script:Principal }
    }

    It 'Rejects missing <Key> before Azure calls' -ForEach @(
        @{ Key = 'Endpoint' }, @{ Key = 'SubscriptionId' }, @{ Key = 'PrincipalId' }, @{ Key = 'FoundryAccountName' }
    ) {
        $script:Parameters.Remove($Key)
        { Register-IntakeAnalyzer @script:Parameters } | Should -Throw '*required*'
        Should -Invoke az -Times 0
        Should -Invoke Invoke-WebRequest -Times 0
    }

    It 'Rejects a different CLI principal before any role or HTTP calls' {
        Mock az { $global:LASTEXITCODE = 0; '22222222-2222-2222-2222-222222222222' } -ParameterFilter { $args[0] -eq 'ad' }
        { Register-IntakeAnalyzer @script:Parameters } | Should -Throw '*object ID does not match*'
        Should -Invoke az -Times 0 -ParameterFilter { $args[0] -eq 'role' }
        Should -Invoke Invoke-WebRequest -Times 0
    }

    It 'Rejects directory lookup failure without leaking output' {
        Mock az { $global:LASTEXITCODE = 1; 'sensitive-directory-output' } -ParameterFilter { $args[0] -eq 'ad' }
        { Register-IntakeAnalyzer @script:Parameters } | Should -Throw '*object ID lookup failed*'
        Should -Invoke Invoke-WebRequest -Times 0
    }

    It 'Rejects wrong tenant, type, or endpoint before sending a bearer token' -ForEach @(
        @{ Key = 'TenantId'; Value = 'wrong' }, @{ Key = 'PrincipalType'; Value = 'ServicePrincipal' },
        @{ Key = 'Endpoint'; Value = 'https://attacker.example/' }, @{ Key = 'Endpoint'; Value = 'http://foundry.cognitiveservices.azure.com/' }
    ) {
        $script:Parameters[$Key] = $Value
        { Register-IntakeAnalyzer @script:Parameters } | Should -Throw
        Should -Invoke Invoke-WebRequest -Times 0
        Should -Invoke az -Times 0 -ParameterFilter { $args[2] -eq 'create' }
    }

    It 'Fails a denied role assignment with administrator remediation' {
        $script:Assignments = @()
        Mock az { $global:LASTEXITCODE = 1 } -ParameterFilter { $args[2] -eq 'create' }
        { Register-IntakeAnalyzer @script:Parameters } | Should -Throw '*RBAC administrator*'
        Should -Invoke Invoke-WebRequest -Times 0
    }

    It 'Accepts a concurrent matching assignment only after requerying' {
        $script:Assignments = @()
        Mock az {
            $global:LASTEXITCODE = 1
            $script:Assignments = @(@{ principalId = $script:Principal; roleDefinitionId = '/roles/a97b65f3-24c7-4388-baec-2e87135dc908'; scope = $script:Scope })
        } -ParameterFilter { $args[2] -eq 'create' }
        Register-IntakeAnalyzer @script:Parameters
        Should -Invoke az -Times 2 -Exactly -ParameterFilter { $args[2] -eq 'list' }
    }

    It 'Retries HTTP <_> before completing' -ForEach @(403, 408, 429, 500, 502, 503, 504) {
        $script:StatusCodes.Clear()
        $script:StatusCodes.Enqueue($_)
        $script:StatusCodes.Enqueue(200)
        Register-IntakeAnalyzer @script:Parameters
        Should -Invoke Invoke-WebRequest -Times 4 -Exactly
        Should -Invoke Start-Sleep -Times 1 -Exactly
    }

    It 'Caps Retry-After before retrying throttled requests' {
        Mock Invoke-WebRequest { @{ StatusCode = 429; Headers = @{ 'Retry-After' = @('99999') } } }
        { Invoke-RegistrationRequest -Method Patch -Uri 'https://test.invalid' -Body '{}' -Headers @{} -Operation test -MaxAttempts 2 } | Should -Throw '*bounded retries*'
        Should -Invoke Start-Sleep -Times 1 -Exactly -ParameterFilter { $Seconds -eq 60 }
    }

    It 'Reacquires a token once for 401 and stops if it persists' {
        $script:StatusCodes.Clear()
        $script:StatusCodes.Enqueue(401)
        { Register-IntakeAnalyzer @script:Parameters } | Should -Throw '*HTTP 401 (nonretryable)*'
        Should -Invoke az -Times 2 -Exactly -ParameterFilter { $args[1] -eq 'get-access-token' }
        Should -Invoke Invoke-WebRequest -Times 2 -Exactly
    }

    It 'Fails immediately for permanent HTTP <_> without response or token disclosure' -ForEach @(301, 400, 404, 422) {
        $script:StatusCodes.Clear()
        $script:StatusCodes.Enqueue($_)
        try { Register-IntakeAnalyzer @script:Parameters; throw 'Expected failure' }
        catch {
            $_.Exception.Message | Should -Match 'nonretryable'
            $_.Exception.Message | Should -Not -Match 'fake-secret-token|sensitive-response'
        }
        Should -Invoke Invoke-WebRequest -Times 1 -Exactly
        Should -Invoke Start-Sleep -Times 0
    }

    It 'Exhausts ten requests and never begins analyzer PUTs after defaults fail' {
        $script:StatusCodes.Clear()
        $script:StatusCodes.Enqueue(403)
        { Register-IntakeAnalyzer @script:Parameters } | Should -Throw '*bounded retries*'
        Should -Invoke Invoke-WebRequest -Times 10 -Exactly
        Should -Invoke Start-Sleep -Times 9 -Exactly
        Should -Invoke Invoke-WebRequest -Times 0 -ParameterFilter { $Method -eq 'Put' }
    }

    It 'Retries a transient transport failure' {
        $script:TransportCalls = 0
        Mock Invoke-WebRequest {
            $script:TransportCalls++
            if ($script:TransportCalls -eq 1) { throw [System.Net.Http.HttpRequestException]::new('sensitive transport details') }
            @{ StatusCode = 200; Headers = @{} }
        }
        Register-IntakeAnalyzer @script:Parameters
        Should -Invoke Start-Sleep -Times 1 -Exactly
    }

    It 'Stops at the operation deadline without issuing another request' {
        Mock Invoke-WebRequest { throw 'A request should not start with less than one second remaining.' }
        { Invoke-RegistrationRequest -Method Patch -Uri 'https://test.invalid' -Body '{}' -Headers @{} -Operation test -TimeoutSeconds 1 } | Should -Throw '*bounded retries*'
        Should -Invoke Invoke-WebRequest -Times 0
    }

    It 'Never logs bearer tokens or response bodies on retry exhaustion' {
        $script:StatusCodes.Clear()
        $script:StatusCodes.Enqueue(403)
        $script:Messages = [Collections.Generic.List[string]]::new()
        Mock Write-Host { $script:Messages.Add([string]$Object) }
        try { Register-IntakeAnalyzer @script:Parameters }
        catch { $script:Messages.Add($_.Exception.Message) }
        ($script:Messages -join "`n") | Should -Not -Match 'fake-secret-token|sensitive-response'
    }
}