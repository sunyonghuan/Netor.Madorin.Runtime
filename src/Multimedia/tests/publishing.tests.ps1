#requires -Version 5.1
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$multimediaRoot = Split-Path $PSScriptRoot -Parent
$failures = [Collections.Generic.List[string]]::new()
$script:checkCount = 0
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('multimedia-publishing-' + [guid]::NewGuid().ToString('N'))
$fixtureSource = Join-Path (Join-Path $fixture 'src') 'Multimedia'
$utf8 = [Text.UTF8Encoding]::new($false)

function Assert-Equal([string]$Name, $Expected, $Actual) {
    $script:checkCount++
    if ($Expected -ne $Actual) { $failures.Add("$Name expected '$Expected', got '$Actual'.") }
}

function Assert-Throws([string]$Name, [scriptblock]$Action) {
    $script:checkCount++
    $threw = $false
    try { $null = & $Action } catch { $threw = $true }
    if (-not $threw) { $failures.Add("$Name did not throw.") }
}

function Write-FixtureFile([string]$Path, [string]$Content) {
    $null = New-Item -ItemType Directory -Path (Split-Path $Path -Parent) -Force
    if ([IO.Path]::GetExtension($Path) -eq '.csproj' -and -not $Content.Contains('<Version>')) {
        $Content = $Content.Replace('</PropertyGroup>', '<Version>0.1.0</Version><SkillContractVersion>1.0</SkillContractVersion></PropertyGroup>')
    }
    [IO.File]::WriteAllText($Path, $Content, $utf8)
}

function New-FixtureProject([string]$Name, [string]$SkillName) {
    $directory = Join-Path $fixtureSource $Name
    Write-FixtureFile (Join-Path $directory 'Fixture.csproj') "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><OutputType>Exe</OutputType><AssemblyName>$SkillName</AssemblyName></PropertyGroup></Project>"
    Copy-Item -LiteralPath (Join-Path $multimediaRoot 'templates/project/publish.ps1') -Destination $directory
    $manifest = [ordered]@{
        schema_version = 1; skill_name = $SkillName; project = 'Fixture.csproj'; executable = $SkillName
        platforms = @(Get-MultimediaPlatforms)
    }
    Write-FixtureFile (Join-Path $directory 'publish.json') ($manifest | ConvertTo-Json)
    $skillSource = Join-Path $directory 'skill'
    Write-FixtureFile (Join-Path $skillSource 'SKILL.md') "---`nname: $SkillName`ndescription: Isolated publishing fixture.`n---`n# Publishing fixture`n"
    Write-FixtureFile (Join-Path $skillSource 'references/protocol.md') '# Fixture protocol'
    Write-FixtureFile (Join-Path $skillSource 'scripts/helper.ps1') 'Write-Output fixture'
    return $directory
}

try {
    $null = New-Item -ItemType Directory -Path $fixtureSource -Force
    foreach ($name in @('Publishing.psm1', 'ReleaseValidation.ps1', 'publish.ps1')) {
        Copy-Item -LiteralPath (Join-Path $multimediaRoot $name) -Destination $fixtureSource
    }
    Import-Module (Join-Path $fixtureSource 'Publishing.psm1') -Force
    $entry = Join-Path $fixtureSource 'publish.ps1'
    $hostRid = Get-MultimediaHostRid
    Assert-Equal 'supported platform count' 6 @(Get-MultimediaPlatforms).Count
    foreach ($rid in Get-MultimediaPlatforms) { Assert-MultimediaPlatform $rid $rid }
    Assert-Throws 'unsupported RID' { Assert-MultimediaPlatform 'freebsd-x64' 'win-x64' }
    Assert-Equal 'cross-OS RID is selectable for toolchain evaluation' $null (Assert-MultimediaPlatform 'linux-x64' 'win-x64')
    Assert-Equal 'cross-architecture RID is selectable for toolchain evaluation' $null (Assert-MultimediaPlatform 'win-arm64' 'win-x64')
    Assert-Throws 'unsupported RID' { Assert-MultimediaPlatform 'freebsd-x64' 'win-x64' }
    Assert-Equal 'empty discovery' 0 @(Get-MultimediaProjects $fixtureSource).Count
    $help = & $entry help | Out-String
    Assert-Equal 'help runtime layout' $true ($help.Contains('runtime/<rid>'))
    Assert-Equal 'empty list' $true ((& $entry list | Out-String).Contains('No multimedia projects registered'))
    Assert-Throws 'empty all' { & $entry all }
    Assert-Throws 'unknown project' { & $entry publish unknown }
    Assert-Throws 'missing project argument' { & $entry publish }
    Assert-Throws 'invalid command' { & $entry destroy }
    Assert-Throws 'list rejects RID' { & $entry list -Rid $hostRid }
    $one = New-FixtureProject 'FixtureOne' 'skill-fixture-one-image'
    $two = New-FixtureProject 'FixtureTwo' 'skill-fixture-two-video'
    $templateDirectory = Join-Path $fixtureSource 'templates/project'
    Write-FixtureFile (Join-Path $templateDirectory 'publish.json') '{}'
    Assert-Equal 'template excluded; two projects discovered' 2 @(Get-MultimediaProjects $fixtureSource).Count
    Assert-Equal 'list includes skill' $true ((& $entry list | Out-String).Contains('skill-fixture-one-image'))
    $manifestPath = Join-Path $one 'publish.json'
    $originalManifest = [IO.File]::ReadAllText($manifestPath)
    Write-FixtureFile (Join-Path $one 'Fixture.csproj') "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><OutputType>Library</OutputType><AssemblyName>skill-fixture-one-image</AssemblyName></PropertyGroup></Project>"
    Assert-Throws 'library project cannot publish as a skill' { Get-MultimediaProjects $fixtureSource }
    Write-FixtureFile (Join-Path $one 'Fixture.csproj') "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><OutputType>Exe</OutputType><AssemblyName>skill-fixture-one-image</AssemblyName></PropertyGroup></Project>"
    $projectWithRuntime = "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><OutputType>Exe</OutputType><AssemblyName>skill-fixture-one-image</AssemblyName><RuntimeIdentifier>win-x64</RuntimeIdentifier></PropertyGroup></Project>"
    Write-FixtureFile (Join-Path $one 'Fixture.csproj') $projectWithRuntime
    Assert-Throws 'fixed project RID rejected' { Get-MultimediaProjects $fixtureSource }
    Write-FixtureFile (Join-Path $one 'Fixture.csproj') $projectWithRuntime.Replace('<RuntimeIdentifier>win-x64</RuntimeIdentifier>', '<RuntimeIdentifier Condition=`"false`">win-x64</RuntimeIdentifier>')
    Assert-Throws 'conditional runtime identifier rejected' { Get-MultimediaProjects $fixtureSource }
    Write-FixtureFile (Join-Path $one 'Fixture.csproj') "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><OutputType>Exe</OutputType><AssemblyName>skill-fixture-one-image</AssemblyName></PropertyGroup></Project>"
    $manifest = $originalManifest | ConvertFrom-Json
    $manifest | Add-Member -NotePropertyName unexpected -NotePropertyValue $true
    Write-FixtureFile $manifestPath ($manifest | ConvertTo-Json)
    Assert-Throws 'unknown manifest field' { Get-MultimediaProjects $fixtureSource }
    $manifest = $originalManifest | ConvertFrom-Json
    $manifest.project = '../FixtureTwo/Fixture.csproj'
    Write-FixtureFile $manifestPath ($manifest | ConvertTo-Json)
    Assert-Throws 'project traversal' { Get-MultimediaProjects $fixtureSource }
    Write-FixtureFile $manifestPath $originalManifest
    Write-FixtureFile (Join-Path $one 'Fixture.csproj') '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><AssemblyName>unrelated-executable</AssemblyName></PropertyGroup></Project>'
    Assert-Throws 'assembly name mismatch' { Get-MultimediaProjects $fixtureSource }
    Write-FixtureFile (Join-Path $one 'Fixture.csproj') "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><OutputType>Exe</OutputType><AssemblyName>skill-fixture-one-image</AssemblyName></PropertyGroup></Project>"
    $skillFile = Join-Path $one 'skill/SKILL.md'
    $originalSkill = [IO.File]::ReadAllText($skillFile)
    Write-FixtureFile $skillFile $originalSkill.Replace('skill-fixture-one-image', 'skill-wrong-image')
    Assert-Throws 'mismatched YAML name' { Test-MultimediaSkillSource $one 'skill-fixture-one-image' }
    Write-FixtureFile $skillFile $originalSkill
    Write-FixtureFile $skillFile $originalSkill.Replace('skill-fixture-one-image', 'SKILL-Fixture-one-image')
    Assert-Throws 'case-sensitive YAML name mismatch' { Test-MultimediaSkillSource $one 'skill-fixture-one-image' }
    Write-FixtureFile $skillFile $originalSkill
    Write-FixtureFile (Join-Path $one 'skill/references/secrets.json') '{}'
    Assert-Throws 'secret packaging' { Test-MultimediaSkillSource $one 'skill-fixture-one-image' }
    Remove-Item -LiteralPath (Join-Path $one 'skill/references/secrets.json')
    $global:MultimediaFakeBuildMode = 'success'
    $global:MultimediaFakeBuildCount = 0
    $global:MultimediaFakeDirty = $false
    function global:git {
        $global:LASTEXITCODE = 0
        if ('rev-parse' -in $args) { return ('a' * 40) }
        if ($global:MultimediaFakeDirty) { return ' M src/Multimedia/Fixture.cs' }
    }
    function global:dotnet {
        $global:MultimediaFakeBuildCount++
        $outputIndex = [array]::IndexOf($args, '-o')
        $ridIndex = [array]::IndexOf($args, '-r')
        [xml]$projectXml = [IO.File]::ReadAllText([string]$args[1])
        $name = $projectXml.Project.PropertyGroup.AssemblyName
        if ($args[$ridIndex + 1].StartsWith('win-')) { $name += '.exe' }
        $global:LASTEXITCODE = 0
        if ($global:MultimediaFakeBuildMode -eq 'fail') { $global:LASTEXITCODE = 9; return }
        if ($global:MultimediaFakeBuildMode -ne 'missing') {
            [IO.File]::WriteAllText((Join-Path $args[$outputIndex + 1] $name), 'fake-native-binary')
        }
    }
    & $entry publish FixtureOne -Rid $hostRid
    $skillsRoot = Join-Path $fixture 'skills'
    $package = Join-Path $skillsRoot 'skill-fixture-one-image'
    foreach ($relative in @('SKILL.md', 'references/protocol.md', 'scripts/helper.ps1', 'scripts/invoke.ps1', 'scripts/invoke.sh', '.multimedia-release.json')) {
        Assert-Equal "package $relative" $true (Test-Path -LiteralPath (Join-Path $package $relative) -PathType Leaf)
    }
    Assert-Equal 'no nested skill source directory' $false (Test-Path -LiteralPath (Join-Path $package 'skill'))
    Assert-Equal 'runtime installed' $true (Test-Path -LiteralPath (Join-Path $package "runtime/$hostRid") -PathType Container)
    if ($env:OS -eq 'Windows_NT') {
        $fixtureBinary = Join-Path $package "runtime/$hostRid/skill-fixture-one-image.exe"
        Copy-Item -LiteralPath $env:ComSpec -Destination $fixtureBinary -Force
        $shell = (Get-Process -Id $PID).Path
        $launcher = Join-Path $package 'scripts/invoke.ps1'
        $forwarded = & $shell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $launcher /c echo --model 'value with spaces'
        Assert-Equal 'launcher forwards option' $true (($forwarded | Out-String).Contains('--model'))
        Assert-Equal 'launcher forwards spaced value' $true (($forwarded | Out-String).Contains('value with spaces'))
        & $shell -NoLogo -NoProfile -ExecutionPolicy Bypass -File $launcher /c exit 7
        Assert-Equal 'launcher preserves native exit code' 7 $LASTEXITCODE
    }
    $bash = Get-Command bash -ErrorAction SilentlyContinue
    if (-not $bash -and (Test-Path -LiteralPath 'C:/Program Files/Git/bin/bash.exe')) {
        $bash = Get-Item -LiteralPath 'C:/Program Files/Git/bin/bash.exe'
    }
    if ($bash) {
        $bashPath = if ($bash -is [IO.FileInfo]) { $bash.FullName } else { $bash.Source }
        & $bashPath -n (Join-Path $package 'scripts/invoke.sh')
        Assert-Equal 'generated Bash launcher parses' 0 $LASTEXITCODE
    }
    $otherRid = if ($hostRid -eq 'linux-x64') { 'osx-arm64' } else { 'linux-x64' }
    $otherRuntime = Join-Path $package "runtime/$otherRid"
    & (Join-Path $one 'publish.ps1') -Rid $otherRid
    Write-FixtureFile (Join-Path $otherRuntime 'keep.txt') 'previous-platform'
    & (Join-Path $one 'publish.ps1') -Rid $hostRid
    Assert-Equal 'standalone project release and RID preservation' 'previous-platform' ([IO.File]::ReadAllText((Join-Path $package "runtime/$otherRid/keep.txt")))
    Assert-Equal 'previous runtime is validated and preserved' 'fake-native-binary' ([IO.File]::ReadAllText((Join-Path $otherRuntime 'skill-fixture-one-image')))
    $recordPath = Join-Path $package '.multimedia-release.json'
    $recordText = [IO.File]::ReadAllText($recordPath)
    $record = $recordText | ConvertFrom-Json
    Assert-Equal 'release version recorded' '0.1.0' $record.version
    Assert-Equal 'source revision recorded' ('a' * 40) $record.source_revision
    Assert-Equal 'both actual RIDs recorded' 2 $record.rids.Count
    Assert-Equal 'shared hash recorded' 64 $record.shared_files_sha256.Length
    $beforeDirty = $global:MultimediaFakeBuildCount
    $global:MultimediaFakeDirty = $true
    Assert-Throws 'dirty source rejected before build' { & $entry publish FixtureOne -Rid $hostRid }
    $global:MultimediaFakeDirty = $false
    Assert-Equal 'dirty source caused no build' $beforeDirty $global:MultimediaFakeBuildCount
    Write-FixtureFile $recordPath '{"schema_version":1,"skill_name":"skill-fixture-one-image"}'
    Assert-Throws 'incomplete old record rejected' { & $entry publish FixtureOne -Rid $hostRid }
    Write-FixtureFile $recordPath $recordText
    $record = $recordText | ConvertFrom-Json
    $record.contract_version = '9.0'
    Write-FixtureFile $recordPath ($record | ConvertTo-Json)
    Assert-Throws 'same version different contract rejected' { & $entry publish FixtureOne -Rid $hostRid }
    Write-FixtureFile $recordPath $recordText
    $publishedReference = Join-Path $package 'references/protocol.md'
    $referenceText = [IO.File]::ReadAllText($publishedReference)
    Write-FixtureFile $publishedReference 'modified after publication'
    Assert-Throws 'tampered published shared hash rejected' { & $entry publish FixtureOne -Rid $hostRid }
    Write-FixtureFile $publishedReference $referenceText
    $record.source_revision = 'b' * 40
    Write-FixtureFile $recordPath ($record | ConvertTo-Json)
    Assert-Throws 'same version different source rejected' { & $entry publish FixtureOne -Rid $hostRid }
    Write-FixtureFile $recordPath $recordText
    $oldHash = (Get-FileHash -LiteralPath (Join-Path $package 'SKILL.md') -Algorithm SHA256).Hash
    Write-FixtureFile $skillFile ($originalSkill + 'Unpublished update')
    Assert-Throws 'same version shared file changes rejected' { & $entry publish FixtureOne -Rid $hostRid }
    Write-FixtureFile $skillFile $originalSkill
    $global:MultimediaFakeBuildMode = 'fail'
    Assert-Throws 'build failure' { & $entry publish FixtureOne -Rid $hostRid }
    Assert-Equal 'old package kept after build failure' $oldHash (Get-FileHash -LiteralPath (Join-Path $package 'SKILL.md') -Algorithm SHA256).Hash
    $global:MultimediaFakeBuildMode = 'missing'
    Assert-Throws 'missing compiled CLI' { & $entry publish FixtureOne -Rid $hostRid }
    Assert-Equal 'old package kept after missing CLI' $oldHash (Get-FileHash -LiteralPath (Join-Path $package 'SKILL.md') -Algorithm SHA256).Hash
    $global:MultimediaFakeBuildMode = 'success'
    function global:Move-Item {
        [CmdletBinding()]
        param([string]$LiteralPath, [string]$Destination)
        if ([IO.Path]::GetFileName($LiteralPath) -like '.skill-*.stage.*') {
            throw 'Simulated failure after the previous package was moved aside.'
        }
        Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination
    }
    try { Assert-Throws 'install failure rolls back previous package' { & $entry publish FixtureOne -Rid $hostRid } }
    finally { Remove-Item -LiteralPath 'Function:\Move-Item' -ErrorAction SilentlyContinue }
    Assert-Equal 'previous package restored after install failure' $oldHash (Get-FileHash -LiteralPath (Join-Path $package 'SKILL.md') -Algorithm SHA256).Hash
    $lockPath = Join-Path $skillsRoot '.multimedia-publish-locks/skill-fixture-one-image.lock'
    $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { Assert-Throws 'concurrent publication lock' { & $entry publish FixtureOne -Rid $hostRid } } finally { $lock.Dispose() }
    $unmanaged = Join-Path $skillsRoot 'skill-fixture-two-video'
    Write-FixtureFile (Join-Path $unmanaged 'keep.txt') 'unmanaged skill'
    Assert-Throws 'unmanaged skill overwrite rejected' { & $entry publish FixtureTwo -Rid $hostRid }
    Assert-Equal 'unmanaged skill preserved' 'unmanaged skill' ([IO.File]::ReadAllText((Join-Path $unmanaged 'keep.txt')))
    Remove-Item -LiteralPath (Join-Path $unmanaged 'keep.txt')
    Remove-Item -LiteralPath $unmanaged
    & $entry all -Rid $hostRid
    Assert-Equal 'all publishes second skill' $true (Test-Path -LiteralPath (Join-Path $skillsRoot 'skill-fixture-two-video/SKILL.md'))
    Assert-Equal 'no staging folders remain' 0 @(Get-ChildItem -LiteralPath $skillsRoot -Directory -Force | Where-Object { $_.Name -like '*.stage.*' -or $_.Name -like '*.backup.*' }).Count
    $linkedDirectory = Join-Path $fixture 'outside-link-target'
    Write-FixtureFile (Join-Path $linkedDirectory 'sentinel.txt') 'linked target'
    New-Item -ItemType Junction -Path (Join-Path $package 'linked-assets') -Target $linkedDirectory | Out-Null
    Assert-Throws 'linked published package cannot be replaced' { & $entry publish FixtureOne -Rid $hostRid }
    Assert-Equal 'linked target never followed or deleted' 'linked target' ([IO.File]::ReadAllText((Join-Path $linkedDirectory 'sentinel.txt')))
    [IO.Directory]::Delete((Join-Path $package 'linked-assets'), $false)
    $beforeFailingAll = $global:MultimediaFakeBuildCount
    $global:MultimediaFakeBuildMode = 'fail'
    Assert-Throws 'all stops on first project build failure' { & $entry all -Rid $hostRid }
    Assert-Equal 'all does not continue after build failure' ($beforeFailingAll + 1) $global:MultimediaFakeBuildCount
    Assert-Equal 'first previous package intact after all fails' $originalSkill ([IO.File]::ReadAllText((Join-Path $package 'SKILL.md')))
    Assert-Equal 'second previous package intact after all fails' $true (Test-Path -LiteralPath (Join-Path $skillsRoot 'skill-fixture-two-video/SKILL.md'))
    $global:MultimediaFakeBuildMode = 'success'
    $projectPath = Join-Path $one 'Fixture.csproj'
    Write-FixtureFile $projectPath ([IO.File]::ReadAllText($projectPath).Replace('<Version>0.1.0</Version>', '<Version>0.2.0</Version>'))
    Write-FixtureFile $skillFile ($originalSkill + 'Version 0.2.0')
    & $entry publish FixtureOne -Rid "$hostRid,$otherRid"
    $newRecord = [IO.File]::ReadAllText($recordPath) | ConvertFrom-Json
    Assert-Equal 'upgrade records new version' '0.2.0' $newRecord.version
    Assert-Equal 'upgrade stages both selected RIDs' 2 $newRecord.rids.Count
    Assert-Equal 'upgrade never retains old RID files' $false (Test-Path -LiteralPath (Join-Path $package "runtime/$otherRid/keep.txt"))
    $newSkill = [IO.File]::ReadAllText((Join-Path $package 'SKILL.md'))
    Write-FixtureFile $projectPath ([IO.File]::ReadAllText($projectPath).Replace('<Version>0.2.0</Version>', '<Version>0.3.0</Version>'))
    $global:MultimediaFakeBuildMode = 'fail'
    Assert-Throws 'upgrade build failure preserves old complete package' { & $entry publish FixtureOne -Rid @($hostRid, $otherRid) }
    Assert-Equal 'upgrade failure leaves current skill' $newSkill ([IO.File]::ReadAllText((Join-Path $package 'SKILL.md')))
    Assert-Equal 'upgrade failure leaves current version record' '0.2.0' (([IO.File]::ReadAllText($recordPath) | ConvertFrom-Json).version)
    $global:MultimediaFakeBuildMode = 'success'
    Write-FixtureFile (Join-Path $skillsRoot 'skill-unrelated-image/keep.txt') 'unrelated'
    $beforeAll = $global:MultimediaFakeBuildCount
    Write-FixtureFile (Join-Path $two 'skill/SKILL.md') 'invalid frontmatter'
    Assert-Throws 'all preflights every skill' { & $entry all -Rid $hostRid }
    Assert-Equal 'all preflight prevents builds' $beforeAll $global:MultimediaFakeBuildCount
    Assert-Equal 'unrelated skill unchanged' 'unrelated' ([IO.File]::ReadAllText((Join-Path $skillsRoot 'skill-unrelated-image/keep.txt')))
}
finally {
    Remove-Item -LiteralPath 'Function:\dotnet' -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath 'Function:\git' -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath 'Function:\Move-Item' -ErrorAction SilentlyContinue
    Remove-Module Publishing -ErrorAction SilentlyContinue
    Remove-Variable MultimediaFakeBuildMode, MultimediaFakeBuildCount, MultimediaFakeDirty -Scope Global -ErrorAction SilentlyContinue
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([char[]]@('/', '\'))
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    if ([IO.Path]::GetDirectoryName($resolvedFixture) -eq $temporaryRoot -and
        [IO.Path]::GetFileName($resolvedFixture).StartsWith('multimedia-publishing-') -and (Test-Path -LiteralPath $resolvedFixture)) {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}

if ($failures.Count -gt 0) {
    throw ($failures -join [Environment]::NewLine)
}
Write-Output "Publishing tests passed ($script:checkCount checks; simulated builds, no real Native AOT compilation)."
