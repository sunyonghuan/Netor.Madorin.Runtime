param(
    [Parameter(Mandatory = $true)][string]$RequestFile,
    [Parameter(Mandatory = $true)][string]$ApiKey,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$RequestId,
    [Parameter(Mandatory = $true)][string]$Model,
    [string]$ApiUrl = 'https://api.x.ai/v1'
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'invoke.ps1') generate --request-file $RequestFile --request-id $RequestId --model $Model --api-url $ApiUrl --api-key $ApiKey --output-dir $OutputDirectory
exit $LASTEXITCODE
