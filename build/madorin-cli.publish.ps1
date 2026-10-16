#!/usr/bin/env pwsh
# Madorin AI CLI Native AOT publish script

param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Target,
    [switch]$Rebuild,
    [switch]$NoBump,
    [switch]$SkipProviders,
    [string]$Rid = "",
    [ValidateSet("patch", "minor", "major")]
    [string]$Bump = "patch"
)

$ErrorActionPreference = 'Stop'

# Provider abbreviation mapping
$ProviderAbbreviations = @{
    'o' = 'openai'
    'oa' = 'openai'
    'openai' = 'openai'
    'a' = 'anthropic'
    'ant' = 'anthropic'
    'anthropic' = 'anthropic'
    'oc' = 'openai-compatible'
    'oaic' = 'openai-compatible'
    'openai-compatible' = 'openai-compatible'
    'd' = 'deepseek'
    'ds' = 'deepseek'
    'deepseek' = 'deepseek'
    'k' = 'kimi'
    'kimi' = 'kimi'
    'ali' = 'aliyun'
    'aliyun' = 'aliyun'
    'az' = 'azure-openai'
    'azure' = 'azure-openai'
    'azure-openai' = 'azure-openai'
    'g' = 'gemini'
    'gem' = 'gemini'
    'gemini' = 'gemini'
    'glm' = 'glm'
    'ol' = 'ollama'
    'ollama' = 'ollama'
}

$PlatformCatalog = @(
    [pscustomobject]@{ Name = 'win'; Aliases = @('win', 'windows'); Rid = 'win-x64'; Note = 'Windows x64' }
    [pscustomobject]@{ Name = 'linux'; Aliases = @('linux'); Rid = 'linux-x64'; Note = 'Linux x64 (glibc 2.35)' }
    [pscustomobject]@{ Name = 'mac'; Aliases = @('mac', 'osx', 'macos'); Rid = 'osx-arm64'; Note = 'macOS Apple Silicon' }
)

function Show-PublishHelp {
    Write-Host ""
    Write-Host "Madorin CLI Publish Script" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Usage:" -ForegroundColor Yellow
    Write-Host "  .\madorin-cli.publish.ps1 [platform|target]" -ForegroundColor White
    Write-Host ""
    Write-Host "Platforms:" -ForegroundColor Yellow
    Write-Host "  (none)                   - Publish win, linux, and mac" -ForegroundColor White
    Write-Host "  win (windows)            - Windows x64           -> releases/Madorin-Cli/win-x64" -ForegroundColor White
    Write-Host "  linux                    - Linux x64             -> releases/Madorin-Cli/linux-x64" -ForegroundColor White
    Write-Host "  mac (osx, macos)         - macOS Apple Silicon   -> releases/Madorin-Cli/osx-arm64" -ForegroundColor White
    Write-Host "  list, help               - Print this list" -ForegroundColor White
    Write-Host ""
    Write-Host "Content targets (optional, comma-separated, can follow a platform):" -ForegroundColor Yellow
    Write-Host "  cli (c, 主程序)           - Publish CLI only" -ForegroundColor White
    Write-Host "  all (a, 全部)            - Publish CLI and every provider" -ForegroundColor White
    Write-Host ""
    Write-Host "Providers:" -ForegroundColor Yellow
    Write-Host "  openai (o, oa)           - OpenAI" -ForegroundColor White
    Write-Host "  anthropic (ant, a)       - Anthropic" -ForegroundColor White
    Write-Host "  openai-compatible (oc)   - OpenAI Compatible" -ForegroundColor White
    Write-Host "  deepseek (ds, d)         - DeepSeek" -ForegroundColor White
    Write-Host "  kimi (k)                 - Kimi" -ForegroundColor White
    Write-Host "  aliyun (ali)             - Aliyun" -ForegroundColor White
    Write-Host "  azure-openai (az, azure) - Azure OpenAI" -ForegroundColor White
    Write-Host "  gemini (gem, g)          - Gemini" -ForegroundColor White
    Write-Host "  glm                      - GLM" -ForegroundColor White
    Write-Host "  ollama (ol)              - Ollama" -ForegroundColor White
    Write-Host ""
    Write-Host "Examples:" -ForegroundColor Yellow
    Write-Host "  .\madorin-cli.publish.ps1                    # 三个平台，CLI 和全部 provider" -ForegroundColor Green
    Write-Host "  .\madorin-cli.publish.ps1 win                # 只发 Windows" -ForegroundColor Green
    Write-Host "  .\madorin-cli.publish.ps1 linux,mac          # Linux 和 macOS" -ForegroundColor Green
    Write-Host "  .\madorin-cli.publish.ps1 win cli            # Windows，只发 CLI" -ForegroundColor Green
    Write-Host "  .\madorin-cli.publish.ps1 list               # 打印可发布平台" -ForegroundColor Green
    Write-Host ""
    Write-Host "Additional options:" -ForegroundColor Yellow
    Write-Host "  -Rebuild                 - Clean and rebuild" -ForegroundColor White
    Write-Host "  -NoBump                  - Don't bump version" -ForegroundColor White
    Write-Host "  -SkipProviders           - Publish CLI only" -ForegroundColor White
    Write-Host "  -Rid <rid>               - Override the platform RID" -ForegroundColor White
    Write-Host "  -Bump <patch|minor|major> - Version bump type (default: patch)" -ForegroundColor White
    Write-Host ""
    exit 0
}

function Resolve-PlatformToken {
    param([string]$Token)

    $normalized = $Token.ToLowerInvariant()
    foreach ($platform in $PlatformCatalog) {
        if ($platform.Aliases -contains $normalized -or $platform.Rid -eq $normalized) {
            return $platform.Rid
        }
    }

    return $null
}

function Resolve-PublishRequest {
    param(
        [string]$InputTarget,
        [string]$ExplicitRid
    )

    $tokens = @()
    if (-not [string]::IsNullOrWhiteSpace($InputTarget)) {
        $tokens = @($InputTarget -split '[,\s]+' | ForEach-Object { $_.Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }

    if ($tokens.Count -eq 1 -and @('list', 'help', '-h', '--help', '/?') -contains $tokens[0].ToLowerInvariant()) {
        Show-PublishHelp
    }

    $platforms = [System.Collections.Generic.List[string]]::new()
    $providers = [System.Collections.Generic.List[string]]::new()
    $content = 'all'

    foreach ($token in $tokens) {
        $normalized = $token.ToLowerInvariant()
        $platformRid = Resolve-PlatformToken -Token $normalized
        if ($null -ne $platformRid) {
            if (-not $platforms.Contains($platformRid)) {
                $platforms.Add($platformRid)
            }
            continue
        }

        if ($normalized -eq 'all' -or $normalized -eq 'a' -or $normalized -eq '全部') {
            $content = 'all'
            continue
        }

        if ($normalized -eq 'cli' -or $normalized -eq 'c' -or $normalized -eq '主程序') {
            $content = 'cli'
            continue
        }

        if ($ProviderAbbreviations.ContainsKey($normalized)) {
            $content = 'providers'
            $slug = $ProviderAbbreviations[$normalized]
            if (-not $providers.Contains($slug)) {
                $providers.Add($slug)
            }
            continue
        }

        Write-Host "Unknown publish argument: '$token'. Use 'list' to see platforms and providers." -ForegroundColor Red
        exit 1
    }

    if (-not [string]::IsNullOrWhiteSpace($ExplicitRid)) {
        $explicitPlatform = Resolve-PlatformToken -Token $ExplicitRid
        if ($null -eq $explicitPlatform) {
            Write-Host "Unsupported RID '$ExplicitRid'. Use 'list' to see platforms." -ForegroundColor Red
            exit 1
        }

        $platforms = [System.Collections.Generic.List[string]]::new()
        $platforms.Add($explicitPlatform)
    }

    if ($platforms.Count -eq 0) {
        foreach ($platform in $PlatformCatalog) {
            $platforms.Add($platform.Rid)
        }
    }

    return @{
        Type = $content
        Providers = @($providers)
        Rids = @($platforms)
    }
}

$TargetText = if ($null -eq $Target -or $Target.Count -eq 0) { '' } else { $Target -join ' ' }
$PublishConfig = Resolve-PublishRequest -InputTarget $TargetText -ExplicitRid $Rid

$BuildDir = [System.IO.Path]::GetFullPath($PSScriptRoot)
$SolutionDir = [System.IO.Path]::GetFullPath((Join-Path $BuildDir '..'))
$SolutionFile = Join-Path $SolutionDir 'Madorin.AI.Runtime.slnx'
$PackageVersionsFile = Join-Path $SolutionDir 'Directory.Packages.props'
$DirectoryBuildPropsFile = Join-Path $SolutionDir 'Directory.Build.props'
$ProjectFile = Join-Path $SolutionDir 'src/Madorin.AI.Runtime.Cli/Madorin.AI.Runtime.Cli.csproj'
$ContractsProjectFile = Join-Path $SolutionDir 'src/Madorin.AI.Runtime.Contracts/Madorin.AI.Runtime.Contracts.csproj'
$ClientProjectFile = Join-Path $SolutionDir 'src/Madorin.AI.Runtime.Client/Madorin.AI.Runtime.Client.csproj'
$BrandReleaseDirectoryName = 'Madorin-Cli'
$PackagesDirectoryName = 'Packages'
$ReleasesDir = Join-Path $SolutionDir 'releases'
$OutputDir = Join-Path $ReleasesDir $BrandReleaseDirectoryName
$PackagesDir = Join-Path $ReleasesDir $PackagesDirectoryName
$CliName = 'madorin-cli'
$PackageName = 'Madorin.Cli'
$SkillDirectoryName = 'madorin-cli'
$SkillSourceDir = Join-Path $SolutionDir "skills/$SkillDirectoryName"
$ProviderWorkersManifestFile = Join-Path $BuildDir 'provider-workers.json'
$script:CurrentRid = ''
$script:SkillOutputDir = ''
$script:BinDir = ''

if (-not (Test-Path -LiteralPath $SolutionFile -PathType Leaf)) {
    throw "Solution file was not found: $SolutionFile"
}

if (-not (Test-Path -LiteralPath $ProjectFile -PathType Leaf)) {
    throw "CLI project file was not found: $ProjectFile"
}

if (-not (Test-Path -LiteralPath $PackageVersionsFile -PathType Leaf)) {
    throw "Central package versions file was not found: $PackageVersionsFile"
}

if (-not (Test-Path -LiteralPath $DirectoryBuildPropsFile -PathType Leaf)) {
    throw "Directory.Build.props was not found: $DirectoryBuildPropsFile"
}

foreach ($packageProjectFile in $ContractsProjectFile, $ClientProjectFile) {
    if (-not (Test-Path -LiteralPath $packageProjectFile -PathType Leaf)) {
        throw "Package project file was not found: $packageProjectFile"
    }
}

if (-not (Test-Path -LiteralPath $SkillSourceDir -PathType Container)) {
    throw "Skill source directory was not found: $SkillSourceDir"
}

if (-not (Test-Path -LiteralPath $ProviderWorkersManifestFile -PathType Leaf)) {
    throw "Provider workers manifest was not found: $ProviderWorkersManifestFile"
}

function Get-ProjectVersion {
    $propsContent = Get-Content -Path $DirectoryBuildPropsFile -Raw -Encoding UTF8
    if ([string]::IsNullOrEmpty($propsContent)) {
        throw "Directory.Build.props is empty or could not be read: $DirectoryBuildPropsFile"
    }
    $versionMatch = [regex]::Match($propsContent, '<MadorinCliVersion>([^<]+)</MadorinCliVersion>')
    if (-not $versionMatch.Success) {
        throw "MadorinCliVersion was not found in: $DirectoryBuildPropsFile"
    }
    return $versionMatch.Groups[1].Value
}

function Bump-ProjectVersion {
    $propsContent = Get-Content -Path $DirectoryBuildPropsFile -Raw -Encoding UTF8
    if ([string]::IsNullOrEmpty($propsContent)) {
        throw "Directory.Build.props is empty or could not be read: $DirectoryBuildPropsFile"
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

    $propsContent = $propsContent -replace "<MadorinCliVersion>([^<]+)</MadorinCliVersion>", "<MadorinCliVersion>$NewVersion</MadorinCliVersion>"

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($DirectoryBuildPropsFile, $propsContent, $utf8NoBom)

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
        '-r', $script:CurrentRid,
        '--self-contained', 'true',
        '-o', $OutputPath,
        '--disable-build-servers',
        '--nologo',
        "-p:Version=$NewVersion",
        "-p:AssemblyVersion=$NewVersion",
        "-p:FileVersion=$NewVersion",
        "-p:InformationalVersion=$NewVersion",
        "-p:MadorinRuntimeContractsVersion=$NewVersion",
        "-p:RestoreConfigFile=$ReleaseNuGetConfigFile",
        "-p:RestorePackagesPath=$RestorePackagesDir",
        '-p:UsePublishAotCross=true'
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

function Get-ProviderWorkerFileName {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Slug
    )

    if ($script:CurrentRid.StartsWith('win')) {
        return "madorin-provider-$Slug.exe"
    }

    return "madorin-provider-$Slug"
}

function Publish-ProviderWorkers {
    param(
        [string[]]$ProviderFilter = @()
    )

    $workersDoc = Get-Content -LiteralPath $ProviderWorkersManifestFile -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -eq $workersDoc.workers -or $workersDoc.workers.Count -eq 0) {
        throw "Provider workers manifest did not list any workers: $ProviderWorkersManifestFile"
    }

    $workersToPublish = if ($ProviderFilter.Count -gt 0) {
        $workersDoc.workers | Where-Object { $ProviderFilter -contains $_.slug }
    } else {
        $workersDoc.workers
    }

    if ($workersToPublish.Count -eq 0) {
        Write-Host "No matching providers found for filter: $($ProviderFilter -join ', ')" -ForegroundColor Yellow
        return
    }

    $index = 0
    foreach ($worker in $workersToPublish) {
        $index++
        $projectPath = Join-Path $SolutionDir $worker.project
        if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
            throw "Provider worker project was not found: $projectPath"
        }

        Write-Host "  [$index/$($workersToPublish.Count)] $($worker.protocol) ($($worker.slug))" -ForegroundColor Cyan
        Invoke-DotNetPublishWithRetry `
            -ProjectPath $projectPath `
            -OutputPath $script:BinDir `
            -Name "Provider $($worker.slug)"

        if ($LASTEXITCODE -ne 0) {
            Write-Host "Provider worker '$($worker.slug)' publish failed, exit code: $LASTEXITCODE" -ForegroundColor Red
            exit $LASTEXITCODE
        }

        $workerFile = Join-Path $script:BinDir (Get-ProviderWorkerFileName -Slug $worker.slug)
        if (-not (Test-Path -LiteralPath $workerFile -PathType Leaf)) {
            throw "Provider worker output was not found: $workerFile"
        }

        $workerSizeMb = [math]::Round((Get-Item -LiteralPath $workerFile).Length / 1MB, 2)
        Write-Host "    $workerFile (${workerSizeMb} MB)" -ForegroundColor Cyan
    }
}

function Write-ProvidersRuntimeManifest {
    param(
        [string[]]$ProviderFilter = @()
    )

    $workersDoc = Get-Content -LiteralPath $ProviderWorkersManifestFile -Raw -Encoding UTF8 | ConvertFrom-Json

    $workersToInclude = if ($ProviderFilter.Count -gt 0) {
        $workersDoc.workers | Where-Object { $ProviderFilter -contains $_.slug }
    } else {
        $workersDoc.workers
    }

    $entries = @()
    foreach ($worker in $workersToInclude) {
        $entries += [pscustomobject]@{
            protocol = $worker.protocol
            slug     = $worker.slug
            fileName = Get-ProviderWorkerFileName -Slug $worker.slug
        }
    }

    $manifest = [pscustomobject]@{
        version = $NewVersion
        rid     = $script:CurrentRid
        workers = $entries
    }

    $manifestPath = Join-Path $script:BinDir 'providers.manifest.json'
    $json = $manifest | ConvertTo-Json -Depth 5
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($manifestPath, $json, $utf8NoBom)
    Write-Host "  Manifest: $manifestPath" -ForegroundColor Cyan
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Madorin AI CLI Native AOT Publish" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Target:    $($PublishConfig.Type)" -ForegroundColor Yellow
Write-Host "Platforms: $($PublishConfig.Rids -join ', ')" -ForegroundColor Yellow
if ($PublishConfig.Providers.Count -gt 0) {
    Write-Host "Providers: $($PublishConfig.Providers -join ', ')" -ForegroundColor Yellow
}
Write-Host "Project:   $ProjectFile"
Write-Host "Contracts: $ContractsProjectFile"
Write-Host "Client:    $ClientProjectFile"
Write-Host "Skill:     $SkillSourceDir"
Write-Host "Output:    $OutputDir\<rid>\$SkillDirectoryName"
Write-Host "Packages:  $PackagesDir"
Write-Host "Version:   $NewVersion"
Write-Host ""

$shouldPublishCli = $PublishConfig.Type -eq 'all' -or $PublishConfig.Type -eq 'cli'
$shouldPublishProviders = $PublishConfig.Type -eq 'all' -or $PublishConfig.Type -eq 'providers'

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

$legacySkillOutputDir = Join-Path $OutputDir $SkillDirectoryName
if (Test-Path -LiteralPath $legacySkillOutputDir -PathType Container) {
    Write-Host "[*] Removing legacy shared output: $legacySkillOutputDir" -ForegroundColor Yellow
    Remove-Item -LiteralPath $legacySkillOutputDir -Recurse -Force
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

Write-Host "[2/7] Packing Contracts NuGet package (Release | v$NewVersion)..." -ForegroundColor Green

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

function Initialize-PlatformOutput {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PlatformRid
    )

    $script:CurrentRid = $PlatformRid
    $platformDir = Join-Path $OutputDir $PlatformRid
    $script:SkillOutputDir = Join-Path $platformDir $SkillDirectoryName
    $script:BinDir = Join-Path $script:SkillOutputDir 'bin'

    if (Test-Path -LiteralPath $script:SkillOutputDir) {
        Remove-Item -LiteralPath $script:SkillOutputDir -Recurse -Force
    }

    Copy-Item -Path $SkillSourceDir -Destination $script:SkillOutputDir -Recurse -Force
    if (Test-Path -LiteralPath $script:BinDir) {
        Get-ChildItem -LiteralPath $script:BinDir -Force | Remove-Item -Recurse -Force
    } else {
        New-Item -ItemType Directory -Path $script:BinDir -Force | Out-Null
    }
}

function Assert-NativePublishOutput {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PlatformRid
    )

    $unexpected = @()
    if ($PlatformRid.StartsWith('linux')) {
        $unexpected = @(Get-ChildItem -LiteralPath $script:BinDir -File -Filter '*.dll' -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match 'e_sqlite3' })
        $expected = Join-Path $script:BinDir 'libe_sqlite3.so'
        if (-not (Test-Path -LiteralPath $expected -PathType Leaf)) {
            throw "Linux publish is missing libe_sqlite3.so: $expected"
        }
    } elseif ($PlatformRid.StartsWith('osx')) {
        $unexpected = @(Get-ChildItem -LiteralPath $script:BinDir -File -Filter '*.dll' -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match 'e_sqlite3' })
        $expected = @(Get-ChildItem -LiteralPath $script:BinDir -File -Filter 'libe_sqlite3*.dylib' -ErrorAction SilentlyContinue)
        if ($expected.Count -eq 0) {
            throw "macOS publish is missing libe_sqlite3 dylib under: $script:BinDir"
        }
    } elseif ($PlatformRid.StartsWith('win')) {
        $expected = Join-Path $script:BinDir 'e_sqlite3.dll'
        if (-not (Test-Path -LiteralPath $expected -PathType Leaf)) {
            throw "Windows publish is missing e_sqlite3.dll: $expected"
        }
    }

    if ($unexpected.Count -gt 0) {
        $names = ($unexpected | ForEach-Object { $_.Name }) -join ', '
        throw "Publish for $PlatformRid contains the wrong SQLite native library: $names"
    }
}

$platformIndex = 0
foreach ($platformRid in $PublishConfig.Rids) {
    $platformIndex++
    Write-Host ""
    Write-Host "======== Platform $platformIndex/$($PublishConfig.Rids.Count): $platformRid ========" -ForegroundColor Cyan
    Initialize-PlatformOutput -PlatformRid $platformRid
    Write-Host "[1/7] Copied skill files to $script:SkillOutputDir" -ForegroundColor Green

    if ($shouldPublishCli) {
        Write-Host "[3/7] Publishing CLI (Release | $platformRid | AOT)..." -ForegroundColor Green

        Invoke-DotNetPublishWithRetry `
            -ProjectPath $ProjectFile `
            -OutputPath $script:BinDir `
            -Name 'CLI'

        if ($LASTEXITCODE -ne 0) {
            Write-Host "CLI publish failed, exit code: $LASTEXITCODE" -ForegroundColor Red
            exit $LASTEXITCODE
        }
    } else {
        Write-Host "[3/7] Skipping CLI (target: $($PublishConfig.Type))." -ForegroundColor Yellow
    }

    if ($shouldPublishProviders) {
        if ($SkipProviders) {
            Write-Host "[4/7] Skipping provider workers (-SkipProviders)." -ForegroundColor Yellow
        } else {
            Write-Host "[4/7] Publishing provider workers (Release | $platformRid | AOT)..." -ForegroundColor Green
            if ($PublishConfig.Type -eq 'providers') {
                Publish-ProviderWorkers -ProviderFilter $PublishConfig.Providers
            } else {
                Publish-ProviderWorkers
            }
        }
    } else {
        Write-Host "[4/7] Skipping provider workers (target: cli)." -ForegroundColor Yellow
    }

    Write-Host "[6/7] Cleaning extra files..." -ForegroundColor Green
    $junkExts = @('*.pdb', '*.xml', '*.deps.json', '*.runtimeconfig.dev.json')
    foreach ($ext in $junkExts) {
        Get-ChildItem -Path $script:BinDir -Filter $ext -File -ErrorAction SilentlyContinue |
            Remove-Item -Force
    }

    if ($shouldPublishCli) {
        $exeFileName = if ($platformRid.StartsWith('win')) { "$CliName.exe" } else { $CliName }
        $exePath = Join-Path $script:BinDir $exeFileName
        if (Test-Path $exePath) {
            $size = [math]::Round((Get-Item $exePath).Length / 1MB, 2)
            Write-Host ""
            Write-Host "CLI published." -ForegroundColor Green
            Write-Host "  Path: $exePath"
            Write-Host "  Size: ${size} MB"
        } else {
            throw "CLI output was not found: $exePath"
        }
    }

    if ($shouldPublishProviders -and -not $SkipProviders) {
        if ($PublishConfig.Type -eq 'providers') {
            Write-ProvidersRuntimeManifest -ProviderFilter $PublishConfig.Providers
        } else {
            Write-ProvidersRuntimeManifest
        }
    }

    if ($shouldPublishCli) {
        Assert-NativePublishOutput -PlatformRid $platformRid
    }

    if (-not $platformRid.StartsWith('win') -and (Get-Command chmod -ErrorAction SilentlyContinue)) {
        Get-ChildItem -Path $script:BinDir -File -ErrorAction SilentlyContinue |
            Where-Object { [string]::IsNullOrEmpty($_.Extension) } |
            ForEach-Object {
                & chmod 755 -- $_.FullName
            }
    }

    if ($shouldPublishCli) {
        Write-Host "[7/7] Packaging CLI to zip..." -ForegroundColor Green

        $zipFileName = "$PackageName-v$NewVersion-$platformRid.zip"
        $hashFileName = "$PackageName-v$NewVersion-$platformRid.sha256"
        $zipPath = Join-Path $ReleasesDir $zipFileName
        $hashPath = Join-Path $ReleasesDir $hashFileName

        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
        if (Test-Path $hashPath) { Remove-Item $hashPath -Force }

        Compress-Archive -Path $script:SkillOutputDir -DestinationPath $zipPath -CompressionLevel Optimal
        $hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash *$zipFileName" | Set-Content -Path $hashPath -Encoding Ascii

        $zipSizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
        Write-Host "  Zip:     $zipPath" -ForegroundColor Cyan
        Write-Host "  Size:    ${zipSizeMb} MB" -ForegroundColor Cyan
        Write-Host "  SHA256:  $hashPath" -ForegroundColor Cyan
    } else {
        Write-Host "[7/7] Skipping packaging (target: $($PublishConfig.Type))." -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "[5/7] Packing Client NuGet package (Release | v$NewVersion)..." -ForegroundColor Green

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

Write-Host ""
Write-Host "Publish completed successfully!" -ForegroundColor Green
Write-Host "Platforms: $($PublishConfig.Rids -join ', ')" -ForegroundColor Green
Write-Host ""
