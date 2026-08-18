#Requires -Version 7.0
<#
.SYNOPSIS
    Local smoke test: drops a file into the inbox container and reports where it ends up.

.DESCRIPTION
    Assumes the Function App is deployed (or running locally against a real storage account
    with an Event Grid subscription pointed at it). Uploads the sample document, then polls
    the lifecycle containers until the blob appears in a terminal one.

.EXAMPLE
    ./scripts/smoke-test.ps1 -File ./samples/hipp-application.pdf
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$File,

    [string]$StorageAccount = $env:AZURE_STORAGE_ACCOUNT_NAME,

    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $File)) { throw "File not found: $File" }

if (-not $StorageAccount) {
    $line = (azd env get-values 2>$null) | Where-Object { $_ -like 'AZURE_STORAGE_ACCOUNT_NAME=*' } | Select-Object -First 1
    if ($line) { $StorageAccount = ($line -split '=', 2)[1].Trim('"') }
}

if (-not $StorageAccount) { throw 'Set -StorageAccount or run `azd env refresh` first.' }

$blobName = "smoke-$(Get-Date -Format 'yyyyMMddHHmmss')-$(Split-Path -Leaf $File)"

Write-Host "Uploading $blobName to inbox on $StorageAccount ..."
az storage blob upload `
    --account-name $StorageAccount --auth-mode login `
    --container-name inbox --name $blobName --file $File --only-show-errors | Out-Null

$terminal = @('completed', 'ignored', 'failed')
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)

while ((Get-Date) -lt $deadline) {
    foreach ($container in $terminal) {
        $found = az storage blob exists `
            --account-name $StorageAccount --auth-mode login `
            --container-name $container --name $blobName `
            --query exists -o tsv --only-show-errors

        if ($found -eq 'true') {
            Write-Host "Blob reached '$container'."

            if ($container -eq 'failed') {
                Write-Host 'Reason:'
                az storage blob metadata show `
                    --account-name $StorageAccount --auth-mode login `
                    --container-name $container --name $blobName -o json
                exit 1
            }

            exit 0
        }
    }

    Start-Sleep -Seconds 5
}

Write-Warning "Blob did not reach a terminal container within $TimeoutSeconds seconds."
foreach ($container in @('inbox', 'processing')) {
    $found = az storage blob exists `
        --account-name $StorageAccount --auth-mode login `
        --container-name $container --name $blobName `
        --query exists -o tsv --only-show-errors
    if ($found -eq 'true') { Write-Warning "Still sitting in '$container'." }
}
exit 1
