#!/usr/bin/env pwsh
# Madorin AI CLI Native AOT publish script

param(
    [switch]$Rebuild,
    [switch]$NoBump,
    [ValidateSet("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")]
    [string]$Rid = "win-x64",
    [ValidateSet("patch", "minor", "major")]
    [string]$Bump = "patch"
)

$ErrorActionPreference = 'Stop'

$BuildDir = [System.IO.Path]::GetFullPath($PSScriptRoot)
$SolutionDir = [System.IO.Path]::GetFullPath((Join-Path $BuildDir '..'))
$SolutionFile = Join-Path $SolutionDir 'Madorin.AI.Runtime.slnx'
$PackageVersionsFile = Join-Path $SolutionDir 'Directory.Packages.props'
$ProjectFile = Join-Path $SolutionDir 'src/Madorin.AI.Runtime.Cli/Madorin.AI.Runtime.Cli.csproj'
$ContractsProjectFile = Join-Path $SolutionDir 'src/Madorin.AI.Runtime.Contracts/Madorin.AI.Runtime.Contracts.csproj'
$ClientProjectFile = Join-Path $SolutionDir 'src/Madorin.AI.Runtime.Client/Madorin.AI.Runtime.Client.csproj'
$BrandReleaseDirectoryName = 'Madorin-Cli'
$PackagesDirectoryName = 'Packages'
$ReleasesDir = Join-Path $SolutionDir 'releases'
$OutputDir = Join-Path $ReleasesDir $BrandReleaseDirectoryName
$PackagesDir = Join-Path $ReleasesDir $PackagesDirectoryName
$CliName = 'madorin-cli'
$exeFileName = $CliName
if ($Rid.StartsWith('win')) { $exeFileName = "$CliName.exe" }
$PackageName = 'Madorin.Cli'
$SkillDirectoryName = 'madorin-cli'
$SkillSourceDir = Join-Path $SolutionDir "skills/$SkillDirectoryName"
$SkillOutputDir = Join-Path $OutputDir $SkillDirectoryName
$BinDir = Join-Path $SkillOutputDir 'bin'

if (-not (Test-Path -LiteralPath $SolutionFile -PathType Leaf)) {
    throw "Solution file was not found: $SolutionFile"
}

if (-not (Test-Path -LiteralPath $ProjectFile -PathType Leaf)) {
    throw "CLI project file was not found: $ProjectFile"
}

if (-not (Test-Path -LiteralPath $PackageVersionsFile -PathType Leaf)) {
    throw "Central package versions file was not found: $PackageVersionsFile"
}

foreach ($packageProjectFile in $ContractsProjectFile, $ClientProjectFile) {
    if (-not (Test-Path -LiteralPath $packageProjectFile -PathType Leaf)) {
        throw "Package project file was not found: $packageProjectFile"
    }
}

if (-not (Test-Path -LiteralPath $SkillSourceDir -PathType Container)) {
    throw "Skill source directory was not found: $SkillSourceDir"
}

function Get-ProjectVersion {
    $projectContent = Get-Content -Path $ProjectFile -Raw -Encoding UTF8
    if ([string]::IsNullOrEmpty($projectContent)) {
        throw "Project file is empty or could not be read: $ProjectFile"
    }
    $versionMatch = [regex]::Match($projectContent, '<Version>([^<]+)</Version>')
    if (-not $versionMatch.Success) {
        throw "Version was not found in project file: $ProjectFile"
    }
    return $versionMatch.Groups[1].Value
}

function Bump-ProjectVersion {
    $projectContent = Get-Content -Path $ProjectFile -Raw -Encoding UTF8
    if ([string]::IsNullOrEmpty($projectContent)) {
        throw "Project file is empty or could not be read: $ProjectFile"
    }
    $CurrentVersion = Get-ProjectVersion
    $versionParts = $CurrentVersion.Split('.')
    if ($versionParts.Length -lt 3) {
        throw "Version format incorrect, expected Major.Minor.Patch: $CurrentVersion"
    }

    $major = [int]$versionParts[0]
    $minor = [int]$versionParts[1]
    $patch = [int]$versionParts[2]
    $OldVersion = $CurrentVersion

    switch ($Bump) {
        "major" { $major++; $minor = 0; $patch = 0 }
        "minor" { $minor++; $patch = 0 }
        "patch" { $patch++ }
    }

    $NewVersion = "$major.$minor.$patch"

    $projectContent = $projectContent -replace "<Version>([^<]+)</Version>", "<Version>$NewVersion</Version>"

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($ProjectFile, $projectContent, $utf8NoBom)

    Write-Host "Version bumped: $OldVersion -> $NewVersion" -ForegroundColor Yellow
    return $NewVersion
}

function Set-ContractsPackageVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $packageVersionsContent = Get-Content -Path $PackageVersionsFile -Raw -Encoding UTF8
    if ([string]::IsNullOrEmpty($packageVersionsContent)) {
        throw "Central package versions file is empty or could not be read: $PackageVersionsFile"
    }

    $versionPattern = '<MadorinRuntimeContractsVersion>[^<]+</MadorinRuntimeContractsVersion>'
    if (-not [regex]::IsMatch($packageVersionsContent, $versionPattern)) {
        throw "MadorinRuntimeContractsVersion was not found in: $PackageVersionsFile"
    }

    $packageVersionsContent = [regex]::Replace(
        $packageVersionsContent,
        $versionPattern,
        "<MadorinRuntimeContractsVersion>$Version</MadorinRuntimeContractsVersion>")

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($PackageVersionsFile, $packageVersionsContent, $utf8NoBom)
}

$NewVersion = if ($NoBump) { Get-ProjectVersion } else { Bump-ProjectVersion }
Set-ContractsPackageVersion -Version $NewVersion

$RestorePackagesRoot = [System.IO.Path]::GetFullPath((Join-Path $SolutionDir 'obj/release-packages'))
$RestorePackagesDir = [System.IO.Path]::GetFullPath((Join-Path $RestorePackagesRoot $NewVersion))
$ReleaseNuGetConfigFile = Join-Path $RestorePackagesDir 'NuGet.Config'
$restoreRootPrefix = $RestorePackagesRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $RestorePackagesDir.StartsWith($restoreRootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Restore packages directory escapes the expected root: $RestorePackagesDir"
}

function Stop-DotNetBuildProcesses {
    Get-Process -Name 'dotnet', 'MSBuild', 'VBCSCompiler' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

function Clear-NuGetHttpCache {
    dotnet nuget locals http-cache --clear | Out-Host
}

function Clear-ProjectReleaseCache {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath
    )

    $projectDirectory = Split-Path $ProjectPath -Parent
    foreach ($directoryName in 'bin/Release', 'obj/Release') {
        $cacheDirectory = Join-Path $projectDirectory $directoryName
        if (Test-Path $cacheDirectory) {
            Remove-Item $cacheDirectory -Recurse -Force
        }
    }
}

function Invoke-DotNetPublishWithRetry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath,

        [Parameter(Mandatory = $true)]
        [string]$OutputPath,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($Rebuild) {
        Clear-ProjectReleaseCache -ProjectPath $ProjectPath
    }

    $publishArgs = @(
        'publish',
        $ProjectPath,
        '-c', 'Release',
        '-r', $Rid,
        '--self-contained', 'true',
        '-o', $OutputPath,
        '--disable-build-servers',
        '--nologo',
        "-p:MadorinRuntimeContractsVersion=$NewVersion",
        "-p:RestoreConfigFile=$ReleaseNuGetConfigFile",
        "-p:RestorePackagesPath=$RestorePackagesDir"
    )
    if ($Rebuild) {
        $publishArgs += '-t:Rebuild;Publish'
    }
    & dotnet @publishArgs

    if ($LASTEXITCODE -eq 0) {
        return
    }

    Write-Host "$Name publish failed, retrying after clearing build servers and NuGet HTTP cache..." -ForegroundColor Yellow
    Stop-DotNetBuildProcesses
    Clear-NuGetHttpCache

    & dotnet @publishArgs
}

function Invoke-DotNetPackWithRetry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath,

        [Parameter(Mandatory = $true)]
        [string]$OutputPath,

        [Parameter(Mandatory = $true)]
        [string]$Version,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($Rebuild) {
        Clear-ProjectReleaseCache -ProjectPath $ProjectPath
    }

    $packArgs = @(
        'pack',
        $ProjectPath,
        '-c', 'Release',
        '-o', $OutputPath,
        '--disable-build-servers',
        '--nologo',
        "-p:Version=$Version",
        "-p:PackageVersion=$Version",
        "-p:AssemblyVersion=$Version",
        "-p:FileVersion=$Version",
        "-p:InformationalVersion=$Version",
        "-p:MadorinRuntimeContractsVersion=$Version",
        "-p:RestoreConfigFile=$ReleaseNuGetConfigFile",
        "-p:RestorePackagesPath=$RestorePackagesDir"
    )
    & dotnet @packArgs

    if ($LASTEXITCODE -eq 0) {
        return
    }

    Write-Host "$Name pack failed, retrying after clearing build servers and NuGet HTTP cache..." -ForegroundColor Yellow
    Stop-DotNetBuildProcesses
    Clear-NuGetHttpCache

    & dotnet @packArgs
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Madorin AI CLI Native AOT Publish" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Project:   $ProjectFile"
Write-Host "Contracts: $ContractsProjectFile"
Write-Host "Client:    $ClientProjectFile"
Write-Host "Skill:     $SkillSourceDir"
Write-Host "Output:    $SkillOutputDir"
Write-Host "Packages:  $PackagesDir"
Write-Host "Version:   $NewVersion"
Write-Host ""

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}
if (-not (Test-Path $PackagesDir)) {
    New-Item -ItemType Directory -Path $PackagesDir -Force | Out-Null
}

$running = Get-Process -Name $CliName -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "[*] Stopping running $CliName process..." -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

$cleanExts = @('*.exe', '*.dll', '*.json', '*.config', '*.manifest', '*.db', '*.db-shm', '*.db-wal')
foreach ($ext in $cleanExts) {
    Get-ChildItem -Path $OutputDir -Filter $ext -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

Write-Host "[1/6] Copying skill files..." -ForegroundColor Green

if (Test-Path $SkillOutputDir) {
    Remove-Item $SkillOutputDir -Recurse -Force
}
Copy-Item -Path $SkillSourceDir -Destination $SkillOutputDir -Recurse -Force

if (Test-Path $BinDir) {
    Get-ChildItem -Path $BinDir -Force | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $BinDir -Force | Out-Null
}

if (Test-Path -LiteralPath $RestorePackagesDir -PathType Container) {
    [System.IO.Directory]::Delete($RestorePackagesDir, $true)
}
[System.IO.Directory]::CreateDirectory($RestorePackagesDir) | Out-Null

$escapedPackagesDir = [System.Security.SecurityElement]::Escape($PackagesDir)
$releaseNuGetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="madorin-local" value="$escapedPackagesDir" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
    <add key="netor.me" value="http://nuget.netor.me/v3/index.json" allowInsecureConnections="true" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="madorin-local">
      <package pattern="Madorin.AI.Runtime.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
    <packageSource key="netor.me">
      <package pattern="Netor.*" />
      <package pattern="Madorin.AI.Runtime.*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($ReleaseNuGetConfigFile, $releaseNuGetConfig, $utf8NoBom)

$contractsPackagePath = Join-Path $PackagesDir "Madorin.AI.Runtime.Contracts.$NewVersion.nupkg"
$clientPackagePath = Join-Path $PackagesDir "Madorin.AI.Runtime.Client.$NewVersion.nupkg"
$expectedPackages = @($contractsPackagePath, $clientPackagePath)

foreach ($packagePath in $expectedPackages) {
    if (Test-Path -LiteralPath $packagePath -PathType Leaf) {
        [System.IO.File]::Delete($packagePath)
    }
}

Write-Host "[2/6] Packing Contracts NuGet package (Release | v$NewVersion)..." -ForegroundColor Green

Invoke-DotNetPackWithRetry `
    -ProjectPath $ContractsProjectFile `
    -OutputPath $PackagesDir `
    -Version $NewVersion `
    -Name 'Contracts'

if ($LASTEXITCODE -ne 0) {
    Write-Host "Contracts pack failed, exit code: $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

if (-not (Test-Path -LiteralPath $contractsPackagePath -PathType Leaf)) {
    throw "Expected Contracts NuGet package was not found: $contractsPackagePath"
}

Write-Host "[3/6] Publishing CLI (Release | $Rid | AOT)..." -ForegroundColor Green

Invoke-DotNetPublishWithRetry `
    -ProjectPath $ProjectFile `
    -OutputPath $BinDir `
    -Name 'CLI'

if ($LASTEXITCODE -ne 0) {
    Write-Host "CLI publish failed, exit code: $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "[4/6] Packing Client NuGet package (Release | v$NewVersion)..." -ForegroundColor Green

Invoke-DotNetPackWithRetry `
    -ProjectPath $ClientProjectFile `
    -OutputPath $PackagesDir `
    -Version $NewVersion `
    -Name 'Client'

if ($LASTEXITCODE -ne 0) {
    Write-Host "Client pack failed, exit code: $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

foreach ($packagePath in $expectedPackages) {
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "Expected NuGet package was not found: $packagePath"
    }

    $packageSizeKb = [math]::Round((Get-Item -LiteralPath $packagePath).Length / 1KB, 2)
    Write-Host "  Package: $packagePath (${packageSizeKb} KB)" -ForegroundColor Cyan
}

Write-Host "[5/6] Cleaning extra files..." -ForegroundColor Green
$junkExts = @('*.pdb', '*.xml', '*.deps.json', '*.runtimeconfig.dev.json')
foreach ($ext in $junkExts) {
    Get-ChildItem -Path $BinDir -Filter $ext -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

$exePath = Join-Path $BinDir $exeFileName
if (Test-Path $exePath) {
    $size = [math]::Round((Get-Item $exePath).Length / 1MB, 2)
    Write-Host ""
    Write-Host "Publish completed." -ForegroundColor Green
    Write-Host "  Path: $exePath"
    Write-Host "  Size: ${size} MB"
} else {
    Write-Host "Warning: $exeFileName was not found. Check publish output." -ForegroundColor Yellow
}

Write-Host "[6/6] Packaging CLI to zip..." -ForegroundColor Green

$zipFileName = "$PackageName-v$NewVersion-$Rid.zip"
$hashFileName = "$PackageName-v$NewVersion-$Rid.sha256"
$zipPath = Join-Path $ReleasesDir $zipFileName
$hashPath = Join-Path $ReleasesDir $hashFileName

if (-not (Test-Path $ReleasesDir)) {
    New-Item -ItemType Directory -Path $ReleasesDir -Force | Out-Null
}

if (Test-Path $SkillOutputDir) {
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    if (Test-Path $hashPath) { Remove-Item $hashPath -Force }

    Compress-Archive -Path $SkillOutputDir -DestinationPath $zipPath -CompressionLevel Optimal
    $hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash *$zipFileName" | Set-Content -Path $hashPath -Encoding Ascii

    $zipSizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
    Write-Host "  Zip:     $zipPath" -ForegroundColor Cyan
    Write-Host "  Size:    ${zipSizeMb} MB" -ForegroundColor Cyan
    Write-Host "  SHA256:  $hashPath" -ForegroundColor Cyan
} else {
    Write-Host "Warning: Skill output directory was not found: $SkillOutputDir. Skipping package." -ForegroundColor Yellow
}
