$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'invoke.ps1') metadata
exit $LASTEXITCODE
