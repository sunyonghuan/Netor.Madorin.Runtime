#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string[]]$Rid)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$modulePath = $null
$directory = $PSScriptRoot
while ($directory) {
    $candidate = Join-Path $directory 'Publishing.psm1'
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { $modulePath = $candidate; break }
    $directory = Split-Path $directory -Parent
}
if (-not $modulePath) { throw 'Could not locate the shared Multimedia publishing module.' }
Import-Module $modulePath -Force
Invoke-MultimediaProjectRelease -ProjectDirectory $PSScriptRoot -Rid $Rid
