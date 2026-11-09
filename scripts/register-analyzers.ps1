#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Registers the Content Understanding classifier and analyzer defined in /analyzers.

.DESCRIPTION
    Run after `azd provision`. Reads the endpoint and ids from azd environment values
    (or from the parameters below) and PUTs each definition to the Content Understanding
    control plane using the signed-in Azure CLI identity. Content Understanding is a
    Foundry Tool and therefore uses the Azure AI Services account endpoint; project-scoped
    Foundry APIs use the separate AZURE_AI_PROJECT_ENDPOINT output. Ensures the
    deployment principal has Cognitive Services User and retries propagation failures.
.PARAMETER PrincipalId
    Deployment principal object ID, defaulting to AZURE_PRINCIPAL_ID from azd.
.PARAMETER PrincipalType
    User or ServicePrincipal, defaulting to AZURE_PRINCIPAL_TYPE from azd.
.EXAMPLE
    ./scripts/register-analyzers.ps1
.NOTES
    Azure CLI and azd must use the same tenant and principal. No tokens are logged.
#>
[CmdletBinding()]
param(
    [string]$Endpoint = $env:CONTENT_UNDERSTANDING_ENDPOINT,
    [string]$ApiVersion = $env:CONTENT_UNDERSTANDING_API_VERSION,
    [string]$ClassifierId = $env:CONTENT_UNDERSTANDING_CLASSIFIER_ID,
    [string]$AnalyzerId = $env:CONTENT_UNDERSTANDING_ANALYZER_ID,
    [string]$CompletionDeployment = $env:CONTENT_UNDERSTANDING_COMPLETION_DEPLOYMENT,
    [string]$EmbeddingDeployment = $env:CONTENT_UNDERSTANDING_EMBEDDING_DEPLOYMENT,
    [string]$SubscriptionId,
    [string]$TenantId,
    [string]$ResourceGroup,
    [string]$FoundryAccountName,
    [string]$PrincipalId,
    [string]$PrincipalType
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'deployment-common.ps1')

function Get-RegistrationToken {
    param([string]$SubscriptionId)
    $Token = Invoke-DeploymentAz -ArgumentList @('account', 'get-access-token', '--subscription', $SubscriptionId, '--resource', 'https://cognitiveservices.azure.com', '--query', 'accessToken', '-o', 'tsv') -Context 'Content Understanding token acquisition'
    if (-not $Token) { throw 'Could not acquire a Content Understanding access token. Run az login for the deployment tenant.' }
    return $Token
}

function Invoke-RegistrationRequest {
    [CmdletBinding()]
    param(
        [string]$Method,
        [string]$Uri,
        [string]$Body,
        [hashtable]$Headers,
        [string]$SubscriptionId,
        [string]$Operation,
        [ValidateRange(1, 10)][int]$MaxAttempts = 10,
        [ValidateRange(1, 600)][int]$TimeoutSeconds = 600
    )
    $Deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $ReacquiredToken = $false
    for ($Attempt = 1; $Attempt -le $MaxAttempts; $Attempt++) {
        $Remaining = [Math]::Floor(($Deadline - [DateTimeOffset]::UtcNow).TotalSeconds)
        if ($Remaining -lt 1) { break }
        $Response = $null
        $Status = 0
        try {
            $Response = Invoke-WebRequest -Method $Method -Uri $Uri -Body $Body -ContentType 'application/json' `
                -Headers $Headers -SkipHttpErrorCheck -MaximumRedirection 0 -TimeoutSec ([Math]::Min(30, $Remaining))
            $Status = [int]$Response.StatusCode
        }
        catch [System.Net.Http.HttpRequestException], [System.Threading.Tasks.TaskCanceledException], [System.TimeoutException] {
            $Status = 0
        }
        catch { throw "${Operation} request failed. Check endpoint connectivity and configuration; response details have been omitted." }
        if ($Status -ge 200 -and $Status -lt 300) {
            Write-Host "  -> HTTP $Status"
            return
        }
        if ($Status -eq 401 -and -not $ReacquiredToken) {
            $Headers.Authorization = "Bearer $(Get-RegistrationToken $SubscriptionId)"
            $ReacquiredToken = $true
        }
        elseif ($Status -notin @(0, 403, 408, 429, 500, 502, 503, 504)) {
            throw "${Operation} failed: HTTP $Status (nonretryable). Check authentication or analyzer configuration."
        }
        if ($Attempt -eq $MaxAttempts) { break }
        $Delay = [Math]::Min(60, 5 * [Math]::Pow(2, $Attempt - 1))
        if ($Status -eq 429 -and $Response.Headers['Retry-After']) {
            $RetryAfter = [string](@($Response.Headers['Retry-After'])[0])
            $Seconds = 0.0
            $RetryDate = [DateTimeOffset]::MinValue
            if ([double]::TryParse($RetryAfter, [ref]$Seconds)) { $Delay = [Math]::Clamp($Seconds, 0, 60) }
            elseif ([DateTimeOffset]::TryParse($RetryAfter, [ref]$RetryDate)) {
                $Delay = [Math]::Clamp(($RetryDate - [DateTimeOffset]::UtcNow).TotalSeconds, 0, 60)
            }
        }
        $Remaining = ($Deadline - [DateTimeOffset]::UtcNow).TotalSeconds
        if ($Remaining -le 0) { break }
        Write-Host "${Operation}: HTTP $Status; retry $Attempt/$MaxAttempts."
        Start-Sleep -Seconds ([Math]::Min($Delay, $Remaining))
    }
    throw "${Operation} failed after bounded retries (last HTTP $Status). Check Cognitive Services User propagation, endpoint connectivity, and service health; rerun scripts/register-analyzers.ps1."
}

function Confirm-RegistrationAccess {
    param([hashtable]$Configuration)
    $SubscriptionId = $Configuration.SubscriptionId
    $PrincipalId = $Configuration.PrincipalId
    $PrincipalType = $Configuration.PrincipalType
    $Account = Confirm-DeploymentSubscription $SubscriptionId $Configuration.TenantId
    if ($PrincipalType -eq 'User' -and $Account.user.type -eq 'user') {
        $CallerId = Invoke-DeploymentAz -ArgumentList @('ad', 'signed-in-user', 'show', '--subscription', $SubscriptionId, '--query', 'id', '-o', 'tsv') -Context 'Signed-in user object ID lookup'
    }
    elseif ($PrincipalType -eq 'ServicePrincipal' -and $Account.user.type -eq 'servicePrincipal') {
        $CallerId = Invoke-DeploymentAz -ArgumentList @('ad', 'sp', 'show', '--id', $Account.user.name, '--subscription', $SubscriptionId, '--query', 'id', '-o', 'tsv') -Context 'Service principal object ID lookup'
    }
    else { throw 'Azure CLI principal type does not match AZURE_PRINCIPAL_TYPE. Align Azure CLI and azd logins.' }
    if ($CallerId -ne $PrincipalId) { throw 'Azure CLI object ID does not match AZURE_PRINCIPAL_ID. Align Azure CLI and azd logins.' }

    $Scope = "/subscriptions/$SubscriptionId/resourceGroups/$($Configuration.ResourceGroup)/providers/Microsoft.CognitiveServices/accounts/$($Configuration.FoundryAccountName)"
    $Foundry = Invoke-DeploymentAz -ArgumentList @('cognitiveservices', 'account', 'show', '--name', $Configuration.FoundryAccountName, '--resource-group', $Configuration.ResourceGroup, '--subscription', $SubscriptionId, '-o', 'json') -Context 'Foundry account lookup' | ConvertFrom-Json
    $EndpointUri = [uri]$Configuration.Endpoint
    if ($Foundry.id -ne $Scope -or $EndpointUri.Scheme -ne 'https' -or $EndpointUri.UserInfo -or $EndpointUri.Query -or $EndpointUri.Fragment -or
        $EndpointUri.AbsoluteUri.TrimEnd('/') -ne ([string]$Foundry.properties.endpoint).TrimEnd('/')) {
        throw 'Content Understanding endpoint does not match the target Foundry account HTTPS endpoint.'
    }
    $RoleId = 'a97b65f3-24c7-4388-baec-2e87135dc908'
    $ListArguments = @('role', 'assignment', 'list', '--assignee-object-id', $PrincipalId, '--role', $RoleId, '--scope', $Scope, '--include-inherited', '--fill-principal-name', 'false', '--subscription', $SubscriptionId, '-o', 'json')
    $Assignments = @(Invoke-DeploymentAz -ArgumentList $ListArguments -Context 'Foundry role lookup' | ConvertFrom-Json | Where-Object {
        $_.principalId -eq $PrincipalId -and $_.roleDefinitionId.Split('/')[-1] -eq $RoleId -and
        ($_.scope -eq $Scope -or $Scope.StartsWith("$($_.scope)/", [StringComparison]::OrdinalIgnoreCase))
    })
    if ($Assignments.Count -gt 0) { return }
    try {
        $null = Invoke-DeploymentAz -ArgumentList @('role', 'assignment', 'create', '--assignee-object-id', $PrincipalId, '--assignee-principal-type', $PrincipalType, '--role', $RoleId, '--scope', $Scope, '--subscription', $SubscriptionId, '-o', 'none') -Context 'Foundry role assignment'
    }
    catch {
        $Assignments = @(Invoke-DeploymentAz -ArgumentList $ListArguments -Context 'Foundry role recheck' | ConvertFrom-Json | Where-Object {
            $_.principalId -eq $PrincipalId -and $_.roleDefinitionId.Split('/')[-1] -eq $RoleId -and $_.scope -eq $Scope
        })
        if ($Assignments.Count -eq 0) {
            throw 'Cannot ensure Cognitive Services User at the Foundry account. Ask an RBAC administrator to grant it to the deployment principal, then rerun this script.'
        }
    }
}

function Register-IntakeAnalyzer {
    [CmdletBinding()]
    param(
        [string]$Endpoint, [string]$ApiVersion, [string]$ClassifierId, [string]$AnalyzerId,
        [string]$CompletionDeployment, [string]$EmbeddingDeployment, [string]$SubscriptionId,
        [string]$TenantId, [string]$ResourceGroup, [string]$FoundryAccountName,
        [string]$PrincipalId, [string]$PrincipalType
    )
    $Keys = @{
        Endpoint = 'CONTENT_UNDERSTANDING_ENDPOINT'; ApiVersion = 'CONTENT_UNDERSTANDING_API_VERSION'
        ClassifierId = 'CONTENT_UNDERSTANDING_CLASSIFIER_ID'; AnalyzerId = 'CONTENT_UNDERSTANDING_ANALYZER_ID'
        CompletionDeployment = 'CONTENT_UNDERSTANDING_COMPLETION_DEPLOYMENT'; EmbeddingDeployment = 'CONTENT_UNDERSTANDING_EMBEDDING_DEPLOYMENT'
        SubscriptionId = 'AZURE_SUBSCRIPTION_ID'; TenantId = 'AZURE_TENANT_ID'; ResourceGroup = 'AZURE_RESOURCE_GROUP'
        FoundryAccountName = 'AZURE_FOUNDRY_ACCOUNT_NAME'; PrincipalId = 'AZURE_PRINCIPAL_ID'; PrincipalType = 'AZURE_PRINCIPAL_TYPE'
    }
    $Configuration = @{}
    foreach ($Name in $Keys.Keys) { $Configuration[$Name] = Resolve-DeploymentValue $PSBoundParameters[$Name] $Keys[$Name] }
    foreach ($Name in @('Endpoint', 'SubscriptionId', 'ResourceGroup', 'FoundryAccountName', 'PrincipalId', 'PrincipalType')) {
        if (-not $Configuration[$Name]) { throw "$($Keys[$Name]) is required for analyzer registration." }
    }
    $ObjectId = [guid]::Empty
    if (-not [guid]::TryParse($Configuration.PrincipalId, [ref]$ObjectId)) { throw 'PrincipalId must be a principal object-ID GUID, not a client name or subscription name.' }
    if ($Configuration.PrincipalType -notin @('User', 'ServicePrincipal')) { throw 'PrincipalType must be User or ServicePrincipal.' }
    $Defaults = @{
        ApiVersion = '2025-11-01'; ClassifierId = 'document_intake_classifier'; AnalyzerId = 'document_intake_form_analyzer'
        CompletionDeployment = 'gpt-5-mini'; EmbeddingDeployment = 'text-embedding-3-large'
    }
    foreach ($Name in $Defaults.Keys) { if (-not $Configuration[$Name]) { $Configuration[$Name] = $Defaults[$Name] } }
    $Endpoint = $Configuration.Endpoint.TrimEnd('/')
    $ApiVersion = [uri]::EscapeDataString($Configuration.ApiVersion)
    $CompletionDeployment = $Configuration.CompletionDeployment
    $EmbeddingDeployment = $Configuration.EmbeddingDeployment
    $Root = Split-Path -Parent $PSScriptRoot
    $Definitions = @(
        @{ Id = $Configuration.ClassifierId; Path = Join-Path $Root 'analyzers/contentunderstanding/document-intake-classifier.json' }
        @{ Id = $Configuration.AnalyzerId; Path = Join-Path $Root 'analyzers/contentunderstanding/document-intake-form-analyzer.json' }
    )
    foreach ($Definition in $Definitions) {
        $DefinitionObject = Get-Content -Raw -Path $Definition.Path | ConvertFrom-Json -AsHashtable
        $DefinitionObject['models'] = @{ completion = $CompletionDeployment; embedding = $EmbeddingDeployment }
        $Definition.Body = $DefinitionObject | ConvertTo-Json -Depth 20
    }
    Confirm-RegistrationAccess $Configuration
    $Headers = @{ Authorization = "Bearer $(Get-RegistrationToken $Configuration.SubscriptionId)" }
    $DefaultsBody = @{
        modelDeployments = @{
            $CompletionDeployment = $CompletionDeployment
            $EmbeddingDeployment = $EmbeddingDeployment
            'prebuilt-analyzer-completion' = $CompletionDeployment
            'prebuilt-analyzer-completion-mini' = $CompletionDeployment
            'prebuilt-analyzer-embedding' = $EmbeddingDeployment
        }
    } | ConvertTo-Json
    Write-Host 'Setting Content Understanding model defaults ...'
    Invoke-RegistrationRequest -Method Patch -Uri "$Endpoint/contentunderstanding/defaults?api-version=$ApiVersion" `
        -Body $DefaultsBody -Headers $Headers -SubscriptionId $Configuration.SubscriptionId -Operation 'Setting model defaults'
    foreach ($Definition in $Definitions) {
        $Id = [uri]::EscapeDataString($Definition.Id)
        Write-Host "Registering $($Definition.Id) ..."
        Invoke-RegistrationRequest -Method Put -Uri "$Endpoint/contentunderstanding/analyzers/${Id}?api-version=$ApiVersion&allowReplace=true" `
            -Body $Definition.Body -Headers $Headers -SubscriptionId $Configuration.SubscriptionId -Operation "Registering $($Definition.Id)"
    }
    Write-Host 'Analyzer registration complete.'
}

if ($MyInvocation.InvocationName -ne '.') {
    try { Register-IntakeAnalyzer @PSBoundParameters }
    catch {
        Write-Error -ErrorAction Continue $_.Exception.Message
        exit 1
    }
}
