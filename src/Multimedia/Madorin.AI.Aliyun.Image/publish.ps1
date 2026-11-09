#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string[]]$Rid)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$directory = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $directory 'Publishing.psm1') -Force
Invoke-MultimediaProjectRelease -ProjectDirectory $PSScriptRoot -Rid $Rid

