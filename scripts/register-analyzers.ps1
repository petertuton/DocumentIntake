#Requires -Version 7.0
<#
.SYNOPSIS
    Registers the Content Understanding classifier and analyzer defined in /analyzers.

.DESCRIPTION
    Run after `azd provision`. Reads the endpoint and ids from azd environment values
    (or from the parameters below) and PUTs each definition to the Content Understanding
    control plane using the signed-in Azure CLI identity.
#>
[CmdletBinding()]
param(
    [string]$Endpoint = $env:CONTENT_UNDERSTANDING_ENDPOINT,
    [string]$ApiVersion = $env:CONTENT_UNDERSTANDING_API_VERSION,
    [string]$ClassifierId = $env:CONTENT_UNDERSTANDING_CLASSIFIER_ID,
    [string]$AnalyzerId = $env:CONTENT_UNDERSTANDING_ANALYZER_ID
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

if (-not $Endpoint) { $Endpoint = Get-AzdValue 'CONTENT_UNDERSTANDING_ENDPOINT' }
if (-not $ApiVersion) { $ApiVersion = Get-AzdValue 'CONTENT_UNDERSTANDING_API_VERSION' }
if (-not $ClassifierId) { $ClassifierId = Get-AzdValue 'CONTENT_UNDERSTANDING_CLASSIFIER_ID' }
if (-not $AnalyzerId) { $AnalyzerId = Get-AzdValue 'CONTENT_UNDERSTANDING_ANALYZER_ID' }

if (-not $Endpoint) {
    Write-Warning 'CONTENT_UNDERSTANDING_ENDPOINT is not set; skipping analyzer registration.'
    exit 0
}

if (-not $ApiVersion) { $ApiVersion = '2025-11-01' }
if (-not $ClassifierId) { $ClassifierId = 'document-intake-classifier' }
if (-not $AnalyzerId) { $AnalyzerId = 'document-intake-form-analyzer' }

$root = Split-Path -Parent $PSScriptRoot
$definitions = @(
    @{ Id = $ClassifierId; Path = Join-Path $root 'analyzers/contentunderstanding/document-intake-classifier.json' }
    @{ Id = $AnalyzerId;   Path = Join-Path $root 'analyzers/contentunderstanding/document-intake-form-analyzer.json' }
)

$token = az account get-access-token --resource 'https://cognitiveservices.azure.com' --query accessToken -o tsv
if (-not $token) { throw 'Could not acquire an access token. Run `az login` first.' }

foreach ($definition in $definitions) {
    if (-not (Test-Path $definition.Path)) {
        throw "Definition file not found: $($definition.Path)"
    }

    $uri = "$($Endpoint.TrimEnd('/'))/contentunderstanding/analyzers/$($definition.Id)?api-version=$ApiVersion"
    $body = Get-Content -Raw -Path $definition.Path

    Write-Host "Registering $($definition.Id) ..."

    try {
        $response = Invoke-WebRequest -Method Put -Uri $uri -Body $body `
            -ContentType 'application/json' `
            -Headers @{ Authorization = "Bearer $token" } `
            -SkipHttpErrorCheck

        if ($response.StatusCode -ge 400) {
            throw "HTTP $($response.StatusCode): $($response.Content)"
        }

        Write-Host "  -> HTTP $($response.StatusCode)"
    }
    catch {
        Write-Error "Failed to register $($definition.Id): $_"
        throw
    }
}

Write-Host 'Analyzer registration complete.'
