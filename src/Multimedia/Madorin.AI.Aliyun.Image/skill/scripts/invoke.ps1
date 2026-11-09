#!/usr/bin/env pwsh
$ErrorActionPreference = 'Stop'
$rid = if ($IsWindows) { 'win-x64' } elseif ($IsMacOS) { 'osx-x64' } else { 'linux-x64' }
$exe = Join-Path $PSScriptRoot "../../runtime/$rid/skill-aliyun-image"
if ($IsWindows) { $exe += '.exe' }
& $exe @args
exit $LASTEXITCODE

