#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Position = 0)][ValidateSet('list', 'publish', 'all', 'platforms', 'help')][string]$Command = 'help',
    [Parameter(Position = 1)][string]$Project,
    [string[]]$Rid
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Publishing.psm1') -Force

if ($Command -eq 'help') {
    Write-Output @'
Usage: publish.ps1 <command> [project-or-skill] [-Rid <rid>]
  list                  List projects, skill names, RIDs and individual commands.
  publish <name>        Publish one project using its own publish.ps1.
  all                   Publish every registered project.
  -Rid rid1,rid2        Stage all selected RIDs before replacing a skill package.
  platforms             List supported RIDs and the current host RID.
  help                  Show this help.
Output: <repository>/skills/<skill_name>/{SKILL.md,references,scripts,runtime/<rid>}
  Native AOT uses the repository cross-compilation toolchain where supported.
'@
    return
}
if ($Command -ne 'publish' -and $Project) { throw "Command '$Command' does not accept a project name." }
if ($Command -in @('list', 'platforms') -and $Rid) { throw "Command '$Command' does not accept -Rid." }
if ($Command -eq 'platforms') {
    Write-Output ('Host RID: ' + (Get-MultimediaHostRid))
    Get-MultimediaPlatforms
    return
}
$projects = @(Get-MultimediaProjects)
if ($Command -eq 'list') {
    if ($projects.Count -eq 0) { Write-Output 'No multimedia projects registered. Add a project with publish.json and publish.ps1.'; return }
    $projects | Select-Object Name, SkillName, @{ Name = 'Platforms'; Expression = { $_.Platforms -join ', ' } },
        @{ Name = 'Command'; Expression = { "./publish.ps1 publish $($_.Name) -Rid <rid>" } } | Format-Table -Wrap -AutoSize
    return
}
if ($Command -eq 'publish') {
    if (-not $Project) { throw 'publish requires a project or skill name. Run list first.' }
    $projects = @($projects | Where-Object { $_.Name -eq $Project -or $_.SkillName -eq $Project })
    if ($projects.Count -ne 1) { throw "Expected one project matching '$Project'; found $($projects.Count). Run list first." }
}
if ($projects.Count -eq 0) { throw 'No multimedia projects registered. Nothing was published.' }
if (-not $Rid) { $Rid = Get-MultimediaHostRid }
$Rid = @($Rid | ForEach-Object { $_.Split(',') })
if (@($Rid | Select-Object -Unique).Count -ne $Rid.Count) { throw 'Duplicate RID selection.' }
foreach ($platform in $Rid) { Assert-MultimediaPlatform $platform }
$preflightProjects = $projects
if ($Command -eq 'all') {
    $projects = @($projects | Sort-Object SkillName)
}
foreach ($item in $projects) {
    foreach ($platform in $Rid) {
        if ($platform -notin $item.Platforms) { throw "Project '$($item.Name)' does not support $platform. Nothing was published." }
    }
    Test-MultimediaSkillSource $item.Directory $item.SkillName
}
foreach ($item in $projects) {
    Write-Host "Publishing $($item.SkillName) [$Rid]..."
    $LASTEXITCODE = 0
    & $item.PublishScript -Rid $Rid
    $succeeded = $?
    if (-not $succeeded -or $LASTEXITCODE -ne 0) {
        throw "Project release script failed: $($item.Name) (exit code $LASTEXITCODE)."
    }
}
Write-Host "Published $($projects.Count) skill(s) for $Rid."
