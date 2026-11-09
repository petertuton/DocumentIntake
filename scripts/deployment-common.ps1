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
        $Result = Invoke-DeploymentCommand -Command azd -ArgumentList @('env', 'get-value', $Key) -TimeoutSeconds 30
        if ($Result.ExitCode -eq 0 -and $Result.Output) { return $Result.Output.Trim() }
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

function Invoke-DeploymentCommand {
    param([string]$Command, [string[]]$ArgumentList, [int]$TimeoutSeconds = 60)
    $Configuration = @{ Command = $Command; Arguments = @($ArgumentList) } | ConvertTo-Json -Compress
    $EncodedConfiguration = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Configuration))
    $Script = @"
`$ErrorActionPreference = 'Stop'
`$Configuration = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$EncodedConfiguration')) | ConvertFrom-Json
`$CommandArguments = @(`$Configuration.Arguments)
& `$Configuration.Command @CommandArguments
exit `$LASTEXITCODE
"@
    $StartInfo = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    foreach ($Argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand', [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Script)))) {
        $StartInfo.ArgumentList.Add($Argument)
    }
    $StartInfo.UseShellExecute = $false
    $StartInfo.CreateNoWindow = $true
    $StartInfo.RedirectStandardOutput = $true
    $StartInfo.RedirectStandardError = $true
    $Process = [Diagnostics.Process]::new()
    $Process.StartInfo = $StartInfo
    try {
        $null = $Process.Start()
        $Output = $Process.StandardOutput.ReadToEndAsync()
        $ErrorOutput = $Process.StandardError.ReadToEndAsync()
        if (-not $Process.WaitForExit($TimeoutSeconds * 1000)) {
            $Process.Kill($true)
            throw 'Deployment CLI command timed out.'
        }
        $null = $ErrorOutput.GetAwaiter().GetResult()
        return @{ ExitCode = $Process.ExitCode; Output = $Output.GetAwaiter().GetResult().Trim() }
    }
    finally { $Process.Dispose() }
}

function Invoke-DeploymentAz {
    param([string[]]$ArgumentList, [string]$Context, [DateTimeOffset]$Deadline = [DateTimeOffset]::MaxValue)
    try {
        $Remaining = [Math]::Min([double]60, [Math]::Floor(($Deadline - [DateTimeOffset]::UtcNow).TotalSeconds))
        if ($Remaining -lt 1) { throw 'Deployment CLI deadline elapsed.' }
        $Result = Invoke-DeploymentCommand -Command az -ArgumentList $ArgumentList -TimeoutSeconds $Remaining
        if ($Result.ExitCode -ne 0) { throw 'Azure CLI returned a nonzero exit code.' }
        return $Result.Output
    }
    catch { throw "${Context} failed. Check Azure CLI login, target subscription, and required permissions." }
}

function Confirm-DeploymentSubscription {
    param([string]$SubscriptionId, [string]$TenantId, [DateTimeOffset]$Deadline = [DateTimeOffset]::MaxValue)
    if (-not $SubscriptionId) { throw 'AZURE_SUBSCRIPTION_ID is required; select an azd environment or pass SubscriptionId.' }
    $Account = Invoke-DeploymentAz -ArgumentList @('account', 'show', '--subscription', $SubscriptionId, '-o', 'json') -Context 'Subscription lookup' -Deadline $Deadline | ConvertFrom-Json
    if ($Account.id -ne $SubscriptionId -or ($TenantId -and $Account.tenantId -ne $TenantId)) {
        throw 'Azure CLI subscription or tenant does not match the deployment environment. Run az login --tenant and az account set --subscription.'
    }
    return $Account
}