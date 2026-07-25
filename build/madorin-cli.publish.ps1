#!/usr/bin/env pwsh
# Madorin AI CLI Native AOT publish script

param(
    [switch]$Rebuild,
    [ValidateSet("patch", "minor", "major")]
    [string]$Bump = "patch"
)

$ErrorActionPreference = 'Stop'

$BuildDir = $PSScriptRoot
$SolutionDir = (Resolve-Path (Join-Path $BuildDir '..')).Path
$ProjectFile = Join-Path $SolutionDir 'Src\Madorin.Ai.Runtime\src\Madorin.AI.Runtime.Cli\Madorin.AI.Runtime.Cli.csproj'
$BrandReleaseDirectoryName = 'Madorin-Cli'
$OutputDir = Join-Path $SolutionDir (Join-Path 'Realases' $BrandReleaseDirectoryName)
$ReleasesDir = Join-Path $SolutionDir 'Realases'
$CliName = 'madorin-ai-cli'
$PackageName = 'Madorin.Cli'

function Bump-ProjectVersion {
    $projectContent = Get-Content -Path $ProjectFile -Raw -Encoding UTF8
    $versionMatch = [regex]::Match($projectContent, '<Version>([^<]+)</Version>')
    if (-not $versionMatch.Success) {
        throw "Version was not found in project file: $ProjectFile"
    }

    $CurrentVersion = $versionMatch.Groups[1].Value
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

$NewVersion = Bump-ProjectVersion

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
    foreach ($directoryName in 'bin\Release', 'obj\Release') {
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

    $publishArgs = @('publish', $ProjectPath, '-c', 'Release', '-o', $OutputPath, '--disable-build-servers')
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

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Madorin AI CLI Native AOT Publish" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Project: $ProjectFile"
Write-Host "Output:  $OutputDir"
Write-Host "Version: $NewVersion"
Write-Host ""

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
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

Write-Host "[1/3] Publishing CLI (Release | win-x64 | AOT)..." -ForegroundColor Green

Invoke-DotNetPublishWithRetry `
    -ProjectPath $ProjectFile `
    -OutputPath $OutputDir `
    -Name 'CLI'

if ($LASTEXITCODE -ne 0) {
    Write-Host "CLI publish failed, exit code: $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "[2/3] Cleaning extra files..." -ForegroundColor Green
$junkExts = @('*.pdb', '*.xml', '*.deps.json', '*.runtimeconfig.dev.json')
foreach ($ext in $junkExts) {
    Get-ChildItem -Path $OutputDir -Filter $ext -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

$exePath = Join-Path $OutputDir "$CliName.exe"
if (Test-Path $exePath) {
    $size = [math]::Round((Get-Item $exePath).Length / 1MB, 2)
    Write-Host ""
    Write-Host "Publish completed." -ForegroundColor Green
    Write-Host "  Path: $exePath"
    Write-Host "  Size: ${size} MB"
} else {
    Write-Host "Warning: $CliName.exe was not found. Check publish output." -ForegroundColor Yellow
}

Write-Host "[3/3] Packaging to zip..." -ForegroundColor Green

$zipFileName = "$PackageName-v$NewVersion-win-x64.zip"
$hashFileName = "$PackageName-v$NewVersion-win-x64.sha256"
$zipPath = Join-Path $ReleasesDir $zipFileName
$hashPath = Join-Path $ReleasesDir $hashFileName

if (-not (Test-Path $ReleasesDir)) {
    New-Item -ItemType Directory -Path $ReleasesDir -Force | Out-Null
}

$packageItems = @(Get-ChildItem -Path $OutputDir -Force | Where-Object { $_.Extension -notin @('.zip', '.sha256') })
if ($packageItems.Count -gt 0) {
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    if (Test-Path $hashPath) { Remove-Item $hashPath -Force }

    Compress-Archive -Path $packageItems.FullName -DestinationPath $zipPath -CompressionLevel Optimal
    $hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash *$zipFileName" | Set-Content -Path $hashPath -Encoding Ascii

    $zipSizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
    Write-Host "  Zip:     $zipPath" -ForegroundColor Cyan
    Write-Host "  Size:    ${zipSizeMb} MB" -ForegroundColor Cyan
    Write-Host "  SHA256:  $hashPath" -ForegroundColor Cyan
} else {
    Write-Host "Warning: Output directory is empty, skipping package." -ForegroundColor Yellow
}
