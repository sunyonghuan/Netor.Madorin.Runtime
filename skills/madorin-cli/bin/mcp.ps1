#!/usr/bin/env pwsh
# mcp.ps1 - PowerShell entry that preserves JSON quoting (cmd.exe shim loses inner quotes)
$bridge = Join-Path (Split-Path -Parent $PSScriptRoot) 'mcp\bin\mcp-bridge.mjs'
if (-not (Test-Path $bridge)) {
  [Console]::Error.WriteLine("Error: bridge not found at $bridge")
  exit 2
}
& node $bridge @args
exit $LASTEXITCODE
