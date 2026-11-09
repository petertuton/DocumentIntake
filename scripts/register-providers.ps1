#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Ensures Microsoft.App is registered before provisioning.
.DESCRIPTION
    Uses the explicit or selected azd subscription, never the active CLI default.
.PARAMETER SubscriptionId
    Target subscription, defaulting to AZURE_SUBSCRIPTION_ID in the azd environment.
.PARAMETER TenantId
    Optional expected tenant, defaulting to AZURE_TENANT_ID.
.EXAMPLE
    ./scripts/register-providers.ps1 -SubscriptionId '<subscription-id>'
.NOTES
    Runs as the azd preprovision hook. Registration has a five-minute deadline.
#>
[CmdletBinding()]
param([string]$SubscriptionId, [string]$TenantId)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'deployment-common.ps1')

function Register-DeploymentProvider {
    [CmdletBinding()]
    param(
        [string]$SubscriptionId,
        [string]$TenantId,
        [ValidateRange(1, 300)][int]$TimeoutSeconds = 300
    )
    $SubscriptionId = Resolve-DeploymentValue $SubscriptionId 'AZURE_SUBSCRIPTION_ID'
    $TenantId = Resolve-DeploymentValue $TenantId 'AZURE_TENANT_ID'
    $Deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $null = Confirm-DeploymentSubscription $SubscriptionId $TenantId -Deadline $Deadline
    $ShowArguments = @('provider', 'show', '--namespace', 'Microsoft.App', '--subscription', $SubscriptionId, '--query', 'registrationState', '-o', 'tsv')
    $State = Invoke-DeploymentAz -ArgumentList $ShowArguments -Context 'Microsoft.App registration lookup' -Deadline $Deadline
    if ($State -eq 'Registered') {
        Write-Host 'Microsoft.App is already registered.'
        return
    }
    $null = Invoke-DeploymentAz -ArgumentList @('provider', 'register', '--namespace', 'Microsoft.App', '--subscription', $SubscriptionId, '-o', 'none') -Context 'Microsoft.App registration' -Deadline $Deadline
    for ($Attempt = 0; $Attempt -lt 30 -and [DateTimeOffset]::UtcNow -lt $Deadline; $Attempt++) {
        $State = Invoke-DeploymentAz -ArgumentList $ShowArguments -Context 'Microsoft.App registration lookup' -Deadline $Deadline
        if ($State -eq 'Registered') {
            Write-Host 'Microsoft.App registration complete.'
            return
        }
        $Remaining = ($Deadline - [DateTimeOffset]::UtcNow).TotalSeconds
        if ($Remaining -gt 0) { Start-Sleep -Seconds ([Math]::Min(10, $Remaining)) }
    }
    throw 'Microsoft.App registration timed out. Check provider registration in the target subscription, then rerun azd up.'
}

if ($MyInvocation.InvocationName -ne '.') {
    try { Register-DeploymentProvider @PSBoundParameters }
    catch {
        Write-Error -ErrorAction Continue $_.Exception.Message
        exit 1
    }
}