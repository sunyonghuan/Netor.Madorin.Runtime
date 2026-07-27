[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Name,

    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string]$ExecutableName,

    [Parameter(Mandatory = $true)]
    [string[]]$RuntimeFiles,

    [hashtable]$Metadata = @{},

    [switch]$CreateZip
)

$ErrorActionPreference = 'Stop'
$publishPath = [System.IO.Path]::GetFullPath($PublishDirectory)
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
if (-not [System.IO.Directory]::Exists($publishPath)) {
    throw "Publish directory does not exist: $publishPath"
}

[System.IO.Directory]::CreateDirectory($outputPath) | Out-Null
$fileReports = @()
foreach ($relativePath in $RuntimeFiles) {
    $fullPath = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($publishPath, $relativePath))
    $publishPrefix = $publishPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($publishPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Runtime file escapes the publish directory: $relativePath"
    }

    if (-not [System.IO.File]::Exists($fullPath)) {
        throw "Runtime file does not exist: $fullPath"
    }

    $item = Get-Item -LiteralPath $fullPath
    $fileReports += [pscustomobject][ordered]@{
        path = $relativePath.Replace('\', '/')
        bytes = $item.Length
        mib = [math]::Round($item.Length / 1MB, 4)
        sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$executable = $fileReports | Where-Object { $_.path -eq $ExecutableName.Replace('\', '/') }
if ($null -eq $executable) {
    throw "Executable '$ExecutableName' is not included in RuntimeFiles."
}

$runtimeSetBytes = ($fileReports | Measure-Object -Property bytes -Sum).Sum
$zipReport = $null
if ($CreateZip) {
    $zipPath = [System.IO.Path]::Combine($outputPath, "$Name.zip")
    if ([System.IO.File]::Exists($zipPath)) {
        [System.IO.File]::Delete($zipPath)
    }

    $archivePaths = $RuntimeFiles | ForEach-Object {
        [System.IO.Path]::Combine($publishPath, $_)
    }
    Compress-Archive -LiteralPath $archivePaths -DestinationPath $zipPath -CompressionLevel Optimal
    $zipItem = Get-Item -LiteralPath $zipPath
    $zipReport = [ordered]@{
        path = $zipItem.FullName
        bytes = $zipItem.Length
        mib = [math]::Round($zipItem.Length / 1MB, 4)
        sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$repositoryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($PSScriptRoot, '..'))
$gitCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$gitStatus = @(& git -C $repositoryRoot status --short)
$report = [ordered]@{
    schemaVersion = 1
    name = $Name
    generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    repositoryRoot = $repositoryRoot
    gitCommit = $gitCommit
    gitDirty = $gitStatus.Count -gt 0
    gitStatus = $gitStatus
    sdkVersion = (& dotnet --version).Trim()
    publishDirectory = $publishPath
    executableBytes = $executable.bytes
    executableMiB = $executable.mib
    runtimeSetBytes = $runtimeSetBytes
    runtimeSetMiB = [math]::Round($runtimeSetBytes / 1MB, 4)
    files = $fileReports
    transportZip = $zipReport
    metadata = $Metadata
}

$manifestPath = [System.IO.Path]::Combine($outputPath, "$Name.manifest.json")
[System.IO.File]::WriteAllText(
    $manifestPath,
    ($report | ConvertTo-Json -Depth 8),
    [System.Text.UTF8Encoding]::new($false))
Write-Output $manifestPath
