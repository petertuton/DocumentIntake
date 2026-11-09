#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.7.1' }
BeforeAll {
    . (Join-Path $PSScriptRoot 'register-providers.ps1')
    function az { }
    function azd { }
}

Describe 'Provider registration' -Tag 'Unit' {
    BeforeEach {
        $script:States = [Collections.Generic.Queue[string]]::new()
        $script:States.Enqueue('Registered')
        Mock Write-Host { }
        Mock Start-Sleep { }
        Mock Get-AzdValue { $null }
        Mock Resolve-DeploymentValue { $Value }
        Mock Invoke-DeploymentCommand {
            $global:LASTEXITCODE = 0
            $Output = & az @ArgumentList
            @{ ExitCode = $global:LASTEXITCODE; Output = $Output }
        }
        Mock az {
            $global:LASTEXITCODE = 0
            if ($args[0] -eq 'account') { return '{"id":"sub","tenantId":"tenant"}' }
            if ($args[1] -eq 'show') {
                if ($script:States.Count -gt 1) { return $script:States.Dequeue() }
                return $script:States.Peek()
            }
        }
    }

    It 'Skips an already registered provider and targets every CLI call explicitly' {
        Register-DeploymentProvider -SubscriptionId sub -TenantId tenant
        Should -Invoke az -Times 0 -ParameterFilter { $args[1] -eq 'register' }
        Should -Invoke az -Times 2 -Exactly -ParameterFilter { $args -contains '--subscription' -and $args -contains 'sub' }
    }

    It 'Registers once and polls until registration completes' {
        $script:States.Clear()
        @('NotRegistered', 'Registering', 'Registered') | ForEach-Object { $script:States.Enqueue($_) }
        Register-DeploymentProvider -SubscriptionId sub -TenantId tenant
        Should -Invoke az -Times 1 -Exactly -ParameterFilter { $args[1] -eq 'register' -and $args -contains 'Microsoft.App' -and $args -contains 'sub' }
        Should -Invoke Start-Sleep -Times 1 -Exactly
    }

    It 'Rejects missing subscription before contacting Azure' {
        { Register-DeploymentProvider } | Should -Throw '*AZURE_SUBSCRIPTION_ID is required*'
        Should -Invoke az -Times 0
    }

    It 'Rejects a tenant mismatch before registration' {
        { Register-DeploymentProvider -SubscriptionId sub -TenantId wrong } | Should -Throw '*does not match*'
        Should -Invoke az -Times 0 -ParameterFilter { $args[0] -eq 'provider' }
    }

    It 'Fails on a CLI authentication or permission error' {
        Mock az { $global:LASTEXITCODE = 1 }
        { Register-DeploymentProvider -SubscriptionId sub } | Should -Throw '*Subscription lookup failed*'
    }

    It 'Fails when registration is denied' {
        $script:States.Clear()
        $script:States.Enqueue('NotRegistered')
        Mock az { $global:LASTEXITCODE = 1 } -ParameterFilter { $args[1] -eq 'register' }
        { Register-DeploymentProvider -SubscriptionId sub } | Should -Throw '*Microsoft.App registration failed*'
    }

    It 'Stops polling after the bounded attempt count' {
        $script:States.Clear()
        $script:States.Enqueue('Registering')
        { Register-DeploymentProvider -SubscriptionId sub } | Should -Throw '*timed out*'
        Should -Invoke az -Times 31 -Exactly -ParameterFilter { $args[0] -eq 'provider' -and $args[1] -eq 'show' }
    }
}

Describe 'Deployment configuration' -Tag 'Unit' {
    BeforeAll {
        Import-Module powershell-yaml -RequiredVersion 0.4.12
        $script:Hooks = (Get-Content (Join-Path $PSScriptRoot '../azure.yaml') -Raw | ConvertFrom-Yaml).hooks
    }

    BeforeEach {
        $script:SavedValue = $env:DOCUMENT_INTAKE_TEST_VALUE
        Remove-Item Env:DOCUMENT_INTAKE_TEST_VALUE -ErrorAction SilentlyContinue
        Mock Get-AzdValue { 'from-azd' }
    }

    AfterEach { $env:DOCUMENT_INTAKE_TEST_VALUE = $script:SavedValue }

    It 'Prefers explicit values over environment and azd' {
        $env:DOCUMENT_INTAKE_TEST_VALUE = 'from-env'
        Resolve-DeploymentValue 'explicit' 'DOCUMENT_INTAKE_TEST_VALUE' | Should -Be 'explicit'
        Should -Invoke Get-AzdValue -Times 0
    }

    It 'Uses an environment value before azd' {
        $env:DOCUMENT_INTAKE_TEST_VALUE = 'from-env'
        Resolve-DeploymentValue '' 'DOCUMENT_INTAKE_TEST_VALUE' | Should -Be 'from-env'
        Should -Invoke Get-AzdValue -Times 0
    }

    It 'Uses the selected azd environment when no explicit or environment value exists' {
        Resolve-DeploymentValue '' 'DOCUMENT_INTAKE_TEST_VALUE' | Should -Be 'from-azd'
        Should -Invoke Get-AzdValue -Times 1 -Exactly
    }

    It 'Stops provisioning on either registration hook failure on <Platform>' -ForEach @(
        @{ Platform = 'windows'; Prefix = '' }, @{ Platform = 'posix'; Prefix = 'pwsh ' }
    ) {
        foreach ($Name in @('preprovision', 'postprovision')) {
            $script:Hooks[$Name][$Platform].continueOnError | Should -BeFalse
            $script:Hooks[$Name][$Platform].interactive | Should -BeFalse
        }
        $script:Hooks.preprovision[$Platform].run | Should -Be "${Prefix}./scripts/register-providers.ps1"
        $script:Hooks.postprovision[$Platform].run | Should -Be "${Prefix}./scripts/register-analyzers.ps1"
        $script:Hooks.postdeploy[$Platform].continueOnError | Should -BeTrue
    }
}

Describe 'Native deployment command boundary' -Tag 'Unit' {
    It 'Captures output and nonzero exit status without exposing stderr' {
        $Result = Invoke-DeploymentCommand -Command (Get-Process -Id $PID).Path -ArgumentList @('-NoProfile', '-NonInteractive', '-Command', "Write-Output 'native-output'; [Console]::Error.WriteLine('sensitive-stderr'); exit 7")
        $Result.ExitCode | Should -Be 7
        $Result.Output | Should -Be 'native-output'
    }

    It 'Kills a stuck native process tree at the timeout' {
        $Clock = [Diagnostics.Stopwatch]::StartNew()
        { Invoke-DeploymentCommand -Command (Get-Process -Id $PID).Path -ArgumentList @('-NoProfile', '-NonInteractive', '-Command', 'while ($true) {}') -TimeoutSeconds 1 } | Should -Throw '*timed out*'
        $Clock.Elapsed.TotalSeconds | Should -BeLessThan 8
    }

    It 'Does not start a CLI process after its deadline' {
        Mock Invoke-DeploymentCommand { throw 'Unexpected process start' }
        { Invoke-DeploymentAz -ArgumentList @('account', 'show') -Context test -Deadline ([DateTimeOffset]::UtcNow.AddSeconds(-1)) } | Should -Throw '*test failed*'
        Should -Invoke Invoke-DeploymentCommand -Times 0
    }
}