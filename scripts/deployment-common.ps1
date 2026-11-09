#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Shared configuration and checked Azure CLI calls for deployment hooks.
.DESCRIPTION
    Loading this file defines functions only and never contacts Azure.
#>
[CmdletBinding()]
param()

function Get-AzdValue {
    param([string]$Key)
    try {
        $Value = azd env get-value $Key 2>$null
        if ($LASTEXITCODE -eq 0 -and $Value) { return ([string]$Value).Trim() }
    }
    catch { }
    return $null
}

function Resolve-DeploymentValue {
    param([string]$Value, [string]$Key)
    if (-not [string]::IsNullOrWhiteSpace($Value)) { return $Value.Trim() }
    $Value = [Environment]::GetEnvironmentVariable($Key)
    if (-not [string]::IsNullOrWhiteSpace($Value)) { return $Value.Trim() }
    return Get-AzdValue $Key
}

function Invoke-DeploymentAz {
    param([string[]]$ArgumentList, [string]$Context)
    try {
        $Result = & az @ArgumentList 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'Azure CLI returned a nonzero exit code.' }
        return $Result
    }
    catch { throw "${Context} failed. Check Azure CLI login, target subscription, and required permissions." }
}

function Confirm-DeploymentSubscription {
    param([string]$SubscriptionId, [string]$TenantId)
    if (-not $SubscriptionId) { throw 'AZURE_SUBSCRIPTION_ID is required; select an azd environment or pass SubscriptionId.' }
    $Account = Invoke-DeploymentAz -ArgumentList @('account', 'show', '--subscription', $SubscriptionId, '-o', 'json') -Context 'Subscription lookup' | ConvertFrom-Json
    if ($Account.id -ne $SubscriptionId -or ($TenantId -and $Account.tenantId -ne $TenantId)) {
        throw 'Azure CLI subscription or tenant does not match the deployment environment. Run az login --tenant and az account set --subscription.'
    }
    return $Account
}