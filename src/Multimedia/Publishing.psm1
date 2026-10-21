Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:SupportedRids = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')
. (Join-Path $PSScriptRoot 'ReleaseValidation.ps1')

function Get-MultimediaHostRid {
    if ($env:OS -eq 'Windows_NT') {
        $os = 'win'
        $architecture = $env:PROCESSOR_ARCHITEW6432
        if (-not $architecture) { $architecture = $env:PROCESSOR_ARCHITECTURE }
        $architecture = $architecture.ToLowerInvariant().Replace('amd64', 'x64')
    }
    else {
        $os = if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [Runtime.InteropServices.OSPlatform]::OSX)) { 'osx' } else { 'linux' }
        $architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    }
    $rid = "$os-$architecture"
    if ($rid -notin $script:SupportedRids) { throw "Unsupported host platform: $rid" }
    return $rid
}

function Get-MultimediaPlatforms { return $script:SupportedRids }

function Assert-MultimediaPlatform([string]$Rid, [string]$HostRid = (Get-MultimediaHostRid)) {
    if ($Rid -notin $script:SupportedRids) { throw "Unsupported RID: $Rid" }
    if ($HostRid -notin $script:SupportedRids) { throw "Unsupported host RID: $HostRid" }
}

function Resolve-MultimediaPath([string]$Root, [string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath)) {
        throw "Expected a non-empty relative path: $RelativePath"
    }
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd([char[]]@('/', '\'))
    $fullPath = [IO.Path]::GetFullPath((Join-Path $rootPath $RelativePath))
    $comparison = if ($env:OS -eq 'Windows_NT') { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not $fullPath.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, $comparison)) {
        throw "Path escapes its allowed directory: $RelativePath"
    }
    $current = $fullPath
    while ($current -and $current.Length -ge $rootPath.Length) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Links are not allowed in release paths: $current"
            }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $fullPath
}

function Read-MultimediaProject([string]$Directory) {
    $manifestPath = Resolve-MultimediaPath $Directory 'publish.json'
    $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    $fields = @('schema_version', 'skill_name', 'project', 'executable', 'platforms')
    foreach ($name in $manifest.PSObject.Properties.Name) {
        if ($name -notin $fields) { throw "Unknown publish.json field: $name ($Directory)" }
    }
    foreach ($name in $fields) {
        if ($name -notin $manifest.PSObject.Properties.Name) { throw "Missing publish.json field: $name ($Directory)" }
    }
    if (($manifest.schema_version -isnot [int] -and $manifest.schema_version -isnot [long]) -or
        $manifest.schema_version -ne 1) { throw 'Unsupported publish.json schema_version.' }
    if ($manifest.skill_name -isnot [string] -or $manifest.skill_name -cnotmatch '^skill-[a-z0-9]+(?:-[a-z0-9]+)*-(?:image|video|image-video)$') {
        throw "Invalid skill_name: $($manifest.skill_name)"
    }
    if ($manifest.executable -cne $manifest.skill_name) { throw 'executable must equal skill_name without a file extension.' }
    if ($manifest.project -isnot [string]) { throw 'project must be a relative .csproj path.' }
    $project = Resolve-MultimediaPath $Directory $manifest.project
    $entry = Resolve-MultimediaPath $Directory 'publish.ps1'
    if ([IO.Path]::GetExtension($project) -cne '.csproj' -or -not (Test-Path -LiteralPath $project -PathType Leaf)) {
        throw "Project file not found: $project"
    }
    try { [xml]$projectXml = [IO.File]::ReadAllText($project) }
    catch { throw "Project file is not valid XML: $project" }
    $outputTypes = [Collections.Generic.List[string]]::new()
    $assemblyNames = [Collections.Generic.List[string]]::new()
    $versions = [Collections.Generic.List[string]]::new()
    $contracts = [Collections.Generic.List[string]]::new()
    $runtimeIdentifierFound = $false
    foreach ($group in $projectXml.SelectNodes('/Project/PropertyGroup')) {
        foreach ($property in $group.ChildNodes) {
            if ($property.NodeType -ne [Xml.XmlNodeType]::Element) { continue }
            if ($property.LocalName -in @('RuntimeIdentifier', 'RuntimeIdentifiers')) {
                $runtimeIdentifierFound = $true
            }
            if ($group.HasAttribute('Condition') -or $property.HasAttribute('Condition')) { continue }
            if ($property.LocalName -ceq 'OutputType') { $outputTypes.Add($property.InnerText.Trim()) }
            if ($property.LocalName -ceq 'AssemblyName') { $assemblyNames.Add($property.InnerText.Trim()) }
            if ($property.LocalName -ceq 'Version') { $versions.Add($property.InnerText.Trim()) }
            if ($property.LocalName -ceq 'SkillContractVersion') { $contracts.Add($property.InnerText.Trim()) }
        }
    }
    if ($outputTypes.Count -ne 1 -or $outputTypes[0] -cne 'Exe') {
        throw "The CLI project must declare one unconditional OutputType=Exe (found $($outputTypes.Count)): $project"
    }
    if ($runtimeIdentifierFound) {
        throw "CLI projects must not fix a RuntimeIdentifier; the release script selects each RID: $project"
    }
    if ($assemblyNames.Count -ne 1 -or $assemblyNames[0] -cne $manifest.executable) {
        throw "The CLI project must declare one unconditional AssemblyName equal to '$($manifest.executable)': $project"
    }
    if ($versions.Count -ne 1 -or $versions[0] -cnotmatch '^\d+\.\d+\.\d+$' -or $contracts.Count -ne 1 -or $contracts[0] -cnotmatch '^\d+\.\d+$') {
        throw 'CLI project must declare unconditional Version (x.y.z) and SkillContractVersion (x.y).'
    }
    if (-not (Test-Path -LiteralPath $entry -PathType Leaf)) { throw "Project release script not found: $entry" }
    if ($manifest.platforms -isnot [array] -or $manifest.platforms.Count -eq 0) { throw 'platforms must be a non-empty JSON array.' }
    foreach ($rid in $manifest.platforms) {
        if ($rid -isnot [string] -or $rid -cnotin $script:SupportedRids) { throw "Unsupported project RID: $rid" }
    }
    if (@($manifest.platforms | Select-Object -Unique).Count -ne $manifest.platforms.Count) { throw 'Duplicate project RIDs.' }
    return [pscustomobject]@{
        Name = [IO.Path]::GetFileName($Directory)
        SkillName = $manifest.skill_name
        Project = $project
        Executable = $manifest.executable
        Platforms = @($manifest.platforms)
        Directory = $Directory
        PublishScript = $entry
        Version = $versions[0]
        ContractVersion = $contracts[0]
    }
}

function Get-MultimediaProjects([string]$Root = $PSScriptRoot) {
    $seen = @{}
    foreach ($directory in (Get-ChildItem -LiteralPath $Root -Directory | Sort-Object Name)) {
        if (-not (Test-Path -LiteralPath (Join-Path $directory.FullName 'publish.json') -PathType Leaf)) { continue }
        $project = Read-MultimediaProject $directory.FullName
        if ($seen.ContainsKey($project.SkillName)) { throw "Duplicate skill_name: $($project.SkillName)" }
        $seen[$project.SkillName] = $true
        $project
    }
}

function Test-MultimediaSkillSource([string]$Root, [string]$SkillName) {
    $null = Assert-MultimediaSkillSource $Root $SkillName
}

function Test-MultimediaReleaseTarget([object]$Project) {
    $repositoryRoot = Split-Path (Split-Path (Split-Path $Project.Directory -Parent) -Parent) -Parent
    $skillsRoot = Join-Path $repositoryRoot 'skills'
    if (-not (Test-Path -LiteralPath $skillsRoot)) { return }
    if (-not (Test-Path -LiteralPath $skillsRoot -PathType Container) -or
        ((Get-Item -LiteralPath $skillsRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to publish through a non-directory or linked skills root: $skillsRoot"
    }
    $target = Join-Path $skillsRoot $Project.SkillName
    if (-not (Test-Path -LiteralPath $target)) { return }
    if (-not (Test-Path -LiteralPath $target -PathType Container) -or
        ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        -not (Test-Path -LiteralPath (Join-Path $target '.multimedia-release.json') -PathType Leaf)) {
        throw "Refusing to replace an unmanaged skill package: $target"
    }
    $marker = [IO.File]::ReadAllText((Join-Path $target '.multimedia-release.json')) | ConvertFrom-Json
    if ($marker.schema_version -ne 1 -or $marker.skill_name -cne $Project.SkillName) {
        throw "Refusing to replace a skill package owned by another publisher: $target"
    }
    foreach ($entry in Get-ChildItem -LiteralPath $target -Force -Recurse) {
        if ((Get-Item -LiteralPath $entry.FullName -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to replace a skill package containing a link: $($entry.FullName)"
        }
    }
}

function Remove-MultimediaOwnedDirectory([string]$Root, [string]$Path, [string]$RequiredPrefix) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return }
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd([char[]]@('/', '\'))
    $fullPath = [IO.Path]::GetFullPath($Path)
    $parent = [IO.Path]::GetDirectoryName($fullPath).TrimEnd([char[]]@('/', '\'))
    $comparison = if ($env:OS -eq 'Windows_NT') { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not $parent.Equals($rootPath, $comparison) -or -not [IO.Path]::GetFileName($fullPath).StartsWith($RequiredPrefix, $comparison)) {
        throw "Refusing to remove a directory not owned by this release operation: $fullPath"
    }
    if ((Get-Item -LiteralPath $fullPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Refusing to remove a linked release directory: $fullPath"
    }
    Remove-Item -LiteralPath $fullPath -Recurse -Force
}

function Assert-MultimediaSkillSource([string]$Root, [string]$SkillName) {
    $source = Resolve-MultimediaPath $Root 'skill'
    $skillFile = Resolve-MultimediaPath $source 'SKILL.md'
    $references = Resolve-MultimediaPath $source 'references'
    if (-not (Test-Path -LiteralPath $skillFile -PathType Leaf)) { throw "Missing skill/SKILL.md: $Root" }
    if (-not (Test-Path -LiteralPath $references -PathType Container) -or
        -not (Get-ChildItem -LiteralPath $references -File -Recurse | Select-Object -First 1)) {
        throw "The skill package must contain at least one reference file: $Root"
    }
    $projectScripts = Resolve-MultimediaPath $Root 'publish.ps1'
    $projectDirectory = Split-Path $projectScripts -Parent
    $projectParent = [IO.Path]::GetDirectoryName($projectDirectory).TrimEnd([char[]]@('/', '\'))
    $multimediaRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([char[]]@('/', '\'))
    $comparison = if ($env:OS -eq 'Windows_NT') { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not $projectParent.Equals($multimediaRoot, $comparison)) {
        throw "Skill source directory is outside src/Multimedia: $Root"
    }
    $content = [IO.File]::ReadAllText($skillFile)
    if ($content -notmatch '(?s)^---[ \t]*\r?\n(?<front>.*?)\r?\n---(?:\r?\n|$)') {
        throw "SKILL.md must have YAML frontmatter: $skillFile"
    }
    $frontMatter = $matches.front
    if ($frontMatter -cnotmatch '(?m)^name:\s*["'']?' + [regex]::Escape($SkillName) + '["'']?\s*$') {
        throw "SKILL.md YAML name must match ${SkillName}: $skillFile"
    }
    if ($frontMatter -notmatch '(?m)^description:[ \t]*\S') {
        throw "SKILL.md YAML frontmatter must declare a description: $skillFile"
    }
    if ($content -match '(?im)^\s*(?:TODO|FIXME|REPLACE ME)\b|Replace this template|\{\{[^}]+\}\}') {
        throw "Skill package contains unresolved template text: $skillFile"
    }
    foreach ($entry in Get-ChildItem -LiteralPath $source -Force) {
        if ($entry.Name -cnotin @('SKILL.md', 'references', 'scripts', 'assets', 'agents')) {
            throw "Unsupported skill package entry: $($entry.Name)"
        }
    }
    $scriptDirectory = Resolve-MultimediaPath $source 'scripts'
    if (-not (Test-Path -LiteralPath $scriptDirectory -PathType Container) -or
        -not (Get-ChildItem -LiteralPath $scriptDirectory -File -Recurse | Select-Object -First 1)) {
        throw "The skill package must contain at least one script: $Root"
    }
    foreach ($file in Get-ChildItem -LiteralPath $source -Force -Recurse) {
        $relative = $file.FullName.Substring($source.Length).TrimStart([char[]]@('/', '\'))
        $null = Resolve-MultimediaPath $source $relative
        if ($file -is [IO.FileInfo] -and $file.DirectoryName -eq $scriptDirectory -and
            $file.Name -in @('invoke.ps1', 'invoke.sh')) {
            throw "scripts/invoke.ps1 and scripts/invoke.sh are reserved for generated runtime launchers: $relative"
        }
        if ($file.Name -match '^(?i:\.env(?:\..*)?|secrets\.json|appsettings(?:\..*)?\.json)$' -or
            ($file -is [IO.FileInfo] -and $file.Extension -match '^(?i:\.pfx|\.p12|\.pem|\.key|\.db|\.sqlite|\.sqlite3|\.png|\.jpe?g|\.webp|\.gif|\.mp4|\.mov|\.webm|\.mkv|\.log)$')) {
            throw "Sensitive, generated or media files cannot be included in a skill package: $relative"
        }
    }
    return $source
}

function New-MultimediaInvocationScripts([string]$ScriptsDirectory, [string]$Executable) {
    $windowsScript = @'
$ErrorActionPreference = 'Stop'
$rid = $env:MADORIN_SKILL_RID
if (-not $rid) {
    if ($env:OS -eq 'Windows_NT') {
        $arch = $env:PROCESSOR_ARCHITEW6432
        if (-not $arch) { $arch = $env:PROCESSOR_ARCHITECTURE }
        $rid = 'win-' + $arch.ToLowerInvariant().Replace('amd64', 'x64')
    }
    else {
        $arch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
        $rid = if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX)) { "osx-$arch" } else { "linux-$arch" }
    }
}
if ($rid -notin @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')) { throw "Unsupported RID: $rid" }
$exeName = 'EXECUTABLE'
if ($rid.StartsWith('win-')) { $exeName += '.exe' }
$runtimeRoot = Join-Path (Join-Path $PSScriptRoot '..') 'runtime'
$exe = Join-Path (Join-Path $runtimeRoot $rid) $exeName
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Runtime not installed for RID '$rid'." }
& $exe @args
exit $LASTEXITCODE
'@
    $windowsScript = $windowsScript.Replace('EXECUTABLE', $Executable)
    $unixScript = @'
#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
if [[ -n "${MADORIN_SKILL_RID:-}" ]]; then
    rid="$MADORIN_SKILL_RID"
else
    os="$(uname -s)"
    arch="$(uname -m)"
    case "$os" in
        Linux) platform=linux ;;
        Darwin) platform=osx ;;
        *) printf 'Unsupported operating system: %s\n' "$os" >&2; exit 2 ;;
    esac
    case "$arch" in
        x86_64|amd64) arch=x64 ;;
        aarch64|arm64) arch=arm64 ;;
        *) printf 'Unsupported architecture: %s\n' "$arch" >&2; exit 2 ;;
    esac
    rid="$platform-$arch"
fi
case "$rid" in
    win-x64|win-arm64|linux-x64|linux-arm64|osx-x64|osx-arm64) ;;
    *) printf 'Unsupported runtime identifier: %s\n' "$rid" >&2; exit 2 ;;
esac
exe="$script_dir/../runtime/$rid/EXECUTABLE"
if [[ ! -x "$exe" ]]; then
    printf "Runtime not installed or not executable for RID '%s'.\n" "$rid" >&2
    exit 127
fi
exec "$exe" "$@"
'@
    $unixScript = $unixScript.Replace('EXECUTABLE', $Executable)
    [IO.File]::WriteAllText((Join-Path $ScriptsDirectory 'invoke.ps1'), $windowsScript, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $ScriptsDirectory 'invoke.sh'), $unixScript, [Text.UTF8Encoding]::new($false))
}

function Invoke-MultimediaProjectRelease([string]$ProjectDirectory, [string[]]$Rid) {
    $Rid = @($Rid | ForEach-Object { $_.Split(',') })
    $projectDirectory = [IO.Path]::GetFullPath($ProjectDirectory)
    $parent = [IO.Path]::GetDirectoryName($projectDirectory)
    $comparison = if ($env:OS -eq 'Windows_NT') { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not $parent.Equals([IO.Path]::GetFullPath($PSScriptRoot), $comparison)) {
        throw 'Release projects must be direct children of src/Multimedia.'
    }
    $project = Read-MultimediaProject $projectDirectory
    if ($Rid.Count -eq 0 -or @($Rid | Select-Object -Unique).Count -ne $Rid.Count) { throw 'Select one or more unique RIDs.' }
    foreach ($platform in $Rid) {
        if ($platform -notin $project.Platforms) { throw "Project '$($project.Name)' does not support $platform." }
        Assert-MultimediaPlatform $platform
    }
    $source = Assert-MultimediaSkillSource $projectDirectory $project.SkillName
    $repositoryRoot = Split-Path (Split-Path (Split-Path $projectDirectory -Parent) -Parent) -Parent
    $sourceRevision = Get-MultimediaSourceRevision $repositoryRoot
    $skillsRoot = Join-Path $repositoryRoot 'skills'
    $null = New-Item -ItemType Directory -Path $skillsRoot -Force
    if ((Get-Item -LiteralPath $skillsRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Refusing to publish through a linked skills directory: $skillsRoot"
    }
    $target = Join-Path $skillsRoot $project.SkillName
    $locksRoot = Join-Path $skillsRoot '.multimedia-publish-locks'
    $null = New-Item -ItemType Directory -Path $locksRoot -Force
    if ((Get-Item -LiteralPath $locksRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Refusing to use a linked release lock directory: $locksRoot"
    }
    $lockPath = Join-Path $locksRoot ($project.SkillName + '.lock')
    $stage = Join-Path $skillsRoot ('.' + $project.SkillName + '.stage.' + [guid]::NewGuid().ToString('N'))
    $backup = Join-Path $skillsRoot ('.' + $project.SkillName + '.backup.' + [guid]::NewGuid().ToString('N'))
    $lock = $null
    $movedOld = $false
    try {
        $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        if (Test-Path -LiteralPath $target) {
            if (-not (Test-Path -LiteralPath $target -PathType Container) -or
                ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                -not (Test-Path -LiteralPath (Join-Path $target '.multimedia-release.json') -PathType Leaf)) {
                throw "Refusing to replace an unmanaged skill package: $target"
            }
            $existing = [IO.File]::ReadAllText((Join-Path $target '.multimedia-release.json')) | ConvertFrom-Json
            if ($existing.schema_version -ne 1 -or $existing.skill_name -cne $project.SkillName) {
                throw "Refusing to replace a skill package owned by another publisher: $target"
            }
            foreach ($entry in Get-ChildItem -LiteralPath $target -Force -Recurse) {
                if ((Get-Item -LiteralPath $entry.FullName -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                    throw "Refusing to replace a skill package containing a link: $($entry.FullName)"
                }
            }
        }
        $null = New-Item -ItemType Directory -Path $stage
        foreach ($entry in Get-ChildItem -LiteralPath $source -Force) {
            Copy-Item -LiteralPath $entry.FullName -Destination $stage -Recurse -Force
        }
        $null = New-Item -ItemType Directory -Path (Join-Path $stage 'scripts') -Force
        $null = New-Item -ItemType Directory -Path (Join-Path $stage 'runtime')
        New-MultimediaInvocationScripts (Join-Path $stage 'scripts') $project.Executable
        $marker = [ordered]@{ schema_version = 1; skill_name = $project.SkillName; version = $project.Version; contract_version = $project.ContractVersion;
            source_revision = $sourceRevision; shared_files_sha256 = (Get-MultimediaSharedHash $stage); rids = @($Rid) }
        $merge = $false
        if (Test-Path -LiteralPath $target) {
            $previous = Read-MultimediaReleaseRecord $target $project
            $merge = Assert-MultimediaReleaseCompatibility $previous ([pscustomobject]$marker)
        }
        if ($merge) {
            $previousRuntime = Join-Path $target 'runtime'
            if (Test-Path -LiteralPath $previousRuntime) {
                $null = Resolve-MultimediaPath $target 'runtime'
                foreach ($entry in Get-ChildItem -LiteralPath $previousRuntime -Recurse -Force) {
                    $relative = $entry.FullName.Substring($target.Length).TrimStart([char[]]@('/', '\'))
                    $null = Resolve-MultimediaPath $target $relative
                }
                foreach ($ridDirectory in Get-ChildItem -LiteralPath $previousRuntime -Directory) {
                    if ($ridDirectory.Name -notin $script:SupportedRids) { continue }
                    if ((Get-Item -LiteralPath $ridDirectory.FullName -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                        throw "Refusing to preserve a linked runtime directory: $($ridDirectory.FullName)"
                    }
                    if ($ridDirectory.Name -in $Rid) { continue }
                    $previousExecutableName = $project.Executable
                    if ($ridDirectory.Name.StartsWith('win-')) { $previousExecutableName += '.exe' }
                    if (-not (Test-Path -LiteralPath (Join-Path $ridDirectory.FullName $previousExecutableName) -PathType Leaf)) {
                        throw "Existing runtime package is missing '$previousExecutableName': $($ridDirectory.FullName)"
                    }
                    Copy-Item -LiteralPath $ridDirectory.FullName -Destination (Join-Path $stage 'runtime') -Recurse
                }
            }
        }
        foreach ($platform in $Rid) {
            $runtime = Join-Path (Join-Path $stage 'runtime') $platform
            $null = New-Item -ItemType Directory -Path $runtime -Force
            & dotnet publish $project.Project -c Release -r $platform --self-contained true --disable-build-servers --nologo -p:TargetFramework=net10.0 -p:PublishAot=true -p:UsePublishAotCross=true -p:DebugType=none -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true -o $runtime
            if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($project.Name) [$platform] with exit code $LASTEXITCODE." }
            $binaryName = $project.Executable
            if ($platform.StartsWith('win-')) { $binaryName += '.exe' }
            if (-not (Test-Path -LiteralPath (Join-Path $runtime $binaryName) -PathType Leaf)) {
                throw "Published executable is missing: $(Join-Path $runtime $binaryName)"
            }
            if ($env:OS -ne 'Windows_NT') {
                & chmod '+x' (Join-Path $runtime $binaryName)
                if ($LASTEXITCODE -ne 0) { throw 'Unable to mark CLI executable.' }
            }
        }
        foreach ($entry in Get-ChildItem -LiteralPath $stage -File -Recurse -Force) {
            if ($entry.Name -match '^(?i:\.env(?:\..*)?|secrets\.json|appsettings(?:\..*)?\.json)$' -or
                $entry.Extension -match '^(?i:\.pfx|\.p12|\.pem|\.key|\.db|\.sqlite|\.sqlite3|\.log|\.png|\.jpe?g|\.webp|\.gif|\.mp4|\.mov|\.webm|\.mkv)$') {
                throw "Sensitive configuration cannot be published: $($entry.Name)"
            }
        }
        if ($env:OS -ne 'Windows_NT') {
            $chmod = Get-Command chmod -ErrorAction Stop
            foreach ($executablePath in @((Get-ChildItem -LiteralPath $stage -Filter '*.sh' -File -Recurse | ForEach-Object FullName))) {
                & $chmod.Source '+x' $executablePath
                if ($LASTEXITCODE -ne 0) { throw "Failed to mark executable: $executablePath" }
            }
        }
        if ((Get-MultimediaSourceRevision $repositoryRoot) -cne $sourceRevision -or (Get-MultimediaSharedHash $stage) -cne $marker.shared_files_sha256) { throw 'Source or shared files changed while publishing.' }
        $marker.rids = @((Get-ChildItem -LiteralPath (Join-Path $stage 'runtime') -Directory).Name | Sort-Object -CaseSensitive)
        [IO.File]::WriteAllText((Join-Path $stage '.multimedia-release.json'), ($marker | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $target) {
            Move-Item -LiteralPath $target -Destination $backup
            $movedOld = $true
        }
        Move-Item -LiteralPath $stage -Destination $target
        if ($movedOld) {
            $movedOld = $false
            try { Remove-MultimediaOwnedDirectory $skillsRoot $backup ('.' + $project.SkillName + '.backup.') }
            catch { Write-Warning "Published package is current; prior package remains at '$backup': $($_.Exception.Message)" }
        }
        Write-Host "Published $($project.SkillName) [$Rid] -> $target"
    }
    catch {
        if ($movedOld -and -not (Test-Path -LiteralPath $target) -and (Test-Path -LiteralPath $backup)) {
            Move-Item -LiteralPath $backup -Destination $target
            $movedOld = $false
        }
        throw
    }
    finally {
        try { Remove-MultimediaOwnedDirectory $skillsRoot $stage ('.' + $project.SkillName + '.stage.') }
        catch { Write-Warning "Temporary release directory remains at '$stage': $($_.Exception.Message)" }
        if ($movedOld -and (Test-Path -LiteralPath $backup)) {
            Write-Warning "Previous package remains at '$backup'; the published target is intact."
        }
        if ($lock) { $lock.Dispose() }
    }
}

Export-ModuleMember -Function Get-MultimediaHostRid, Get-MultimediaPlatforms, Assert-MultimediaPlatform, Get-MultimediaProjects, Test-MultimediaSkillSource, Test-MultimediaReleaseTarget, Invoke-MultimediaProjectRelease
