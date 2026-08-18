#Requires -Version 7.0
<#
.SYNOPSIS
    Creates the BlobCreated Event Grid subscription after the Function App deploy completes.

.DESCRIPTION
    The system topic is created during `azd provision`, but the AzureFunction endpoint is not
    valid until the code is actually deployed. This script waits until after `azd deploy` to
    register the subscription so Event Grid validation succeeds on a fresh environment.
#>
[CmdletBinding()]
param(
    [string]$ResourceGroup = $env:AZURE_RESOURCE_GROUP,
    [string]$FunctionAppName = $env:AZURE_FUNCTION_APP_NAME,
    [string]$StorageAccountName = $env:AZURE_STORAGE_ACCOUNT_NAME,
    [string]$SubscriptionId = $env:AZURE_SUBSCRIPTION_ID,
    [string]$InboxContainerName = 'inbox',
    [string]$EventSubscriptionName = 'inbox-blob-created'
)

$ErrorActionPreference = 'Stop'

function Get-AzdValue {
    param([string]$Key)
    try {
        $line = (azd env get-values 2>$null) | Where-Object { $_ -like "$Key=*" } | Select-Object -First 1
        if ($line) { return ($line -split '=', 2)[1].Trim('"') }
    }
    catch { }
    return $null
}

if (-not $ResourceGroup) { $ResourceGroup = Get-AzdValue 'AZURE_RESOURCE_GROUP' }
if (-not $FunctionAppName) { $FunctionAppName = Get-AzdValue 'AZURE_FUNCTION_APP_NAME' }
if (-not $StorageAccountName) { $StorageAccountName = Get-AzdValue 'AZURE_STORAGE_ACCOUNT_NAME' }
if (-not $SubscriptionId) { $SubscriptionId = Get-AzdValue 'AZURE_SUBSCRIPTION_ID' }

if (-not $ResourceGroup -or -not $FunctionAppName -or -not $StorageAccountName) {
    Write-Warning 'AZD environment values are missing; skipping Event Grid subscription registration.'
    exit 0
}

if (-not $SubscriptionId) {
    # Fall back to the CLI's active subscription only if azd did not tell us which one to use.
    $SubscriptionId = az account show --query id -o tsv
}
if (-not $SubscriptionId) {
    throw 'Could not resolve the target Azure subscription id. Run `az login` first.'
}

$systemTopicName = "evgt-$StorageAccountName"
$functionResourceId = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.Web/sites/$FunctionAppName/functions/BlobCreatedTrigger"
$subjectPrefix = "/blobServices/default/containers/$InboxContainerName/blobs/"

$eventgridInstalled = $false
try {
    $eventgridInstalled = (az extension show --name eventgrid --query name -o tsv 2>$null) -eq 'eventgrid'
}
catch { }

if (-not $eventgridInstalled) {
    Write-Host 'Installing Azure Event Grid CLI extension...'
    az extension add --name eventgrid --upgrade -y
}

$existing = az eventgrid system-topic event-subscription show `
    --name $EventSubscriptionName `
    --resource-group $ResourceGroup `
    --system-topic-name $systemTopicName `
    --subscription $SubscriptionId `
    2>$null

if ($LASTEXITCODE -eq 0 -and $existing) {
    Write-Host "Event Grid subscription '$EventSubscriptionName' already exists."
    exit 0
}

Write-Host "Registering Event Grid subscription '$EventSubscriptionName' for '$FunctionAppName'..."
az eventgrid system-topic event-subscription create `
    --name $EventSubscriptionName `
    --resource-group $ResourceGroup `
    --system-topic-name $systemTopicName `
    --subscription $SubscriptionId `
    --endpoint $functionResourceId `
    --endpoint-type azurefunction `
    --included-event-types Microsoft.Storage.BlobCreated `
    --subject-begins-with $subjectPrefix `
    --max-delivery-attempts 10 `
    --event-ttl 1440

if ($LASTEXITCODE -ne 0) {
    throw "Event Grid subscription creation failed with exit code $LASTEXITCODE."
}

Write-Host 'Event Grid subscription registration complete.'
