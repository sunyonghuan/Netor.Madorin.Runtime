param(
    [Parameter(Mandatory = $true)][string]$RequestFile,
    [Parameter(Mandatory = $true)][string]$ApiKey,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'invoke.ps1') generate --request-file $RequestFile --api-key $ApiKey --output-dir $OutputDirectory
exit $LASTEXITCODE

