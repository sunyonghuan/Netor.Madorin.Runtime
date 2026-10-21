function Get-MultimediaSourceRevision([string]$RepositoryRoot) {
    $revision = & git -C $RepositoryRoot rev-parse --verify HEAD
    if ($LASTEXITCODE -ne 0 -or $revision -cnotmatch '^[0-9a-f]{40}$') { throw 'A committed Git source revision is required for publication.' }
    $dirty = & git -C $RepositoryRoot status --porcelain --untracked-files=all -- src/Multimedia Directory.Build.props Directory.Build.targets Directory.Packages.props NuGet.Config global.json
    if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Commit multimedia sources, skill resources and build inputs before publishing mergeable packages.' }
    return [string]$revision
}

function Get-MultimediaSharedHash([string]$Root) {
    $lines = [Collections.Generic.List[string]]::new()
    $files = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($name in @('SKILL.md', 'references', 'scripts')) {
        $path = Resolve-MultimediaPath $Root $name
        foreach ($entry in Get-ChildItem -LiteralPath $path -File -Recurse -Force) {
            $relative = $entry.FullName.Substring($Root.Length).TrimStart([char[]]@('/', '\')).Replace('\', '/')
            $null = Resolve-MultimediaPath $Root $relative
            $files.Add($relative, $entry.FullName)
        }
    }
    $names = [string[]]@($files.Keys)
    [Array]::Sort($names, [StringComparer]::Ordinal)
    foreach ($name in $names) {
        $hash = (Get-FileHash -LiteralPath $files[$name] -Algorithm SHA256).Hash.ToLowerInvariant()
        $lines.Add($name + "`t" + $hash + "`n")
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.UTF8Encoding]::new($false).GetBytes(($lines -join ''))))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Read-MultimediaReleaseRecord([string]$Root, [object]$Project) {
    $record = [IO.File]::ReadAllText((Join-Path $Root '.multimedia-release.json')) | ConvertFrom-Json
    $expected = @('schema_version', 'skill_name', 'version', 'contract_version', 'source_revision', 'shared_files_sha256', 'rids')
    foreach ($name in $expected) {
        if ($name -notin $record.PSObject.Properties.Name) { throw "Release record lacks $name; package was not replaced." }
    }
    if (@($record.PSObject.Properties.Name).Count -ne $expected.Count -or ($record.schema_version -isnot [int] -and $record.schema_version -isnot [long]) -or
        $record.schema_version -ne 1 -or $record.skill_name -cne $Project.SkillName -or
        $record.version -isnot [string] -or $record.version -cnotmatch '^\d+\.\d+\.\d+$' -or $record.contract_version -isnot [string] -or $record.contract_version -cnotmatch '^\d+\.\d+$' -or
        $record.source_revision -cnotmatch '^[0-9a-f]{40}$' -or $record.shared_files_sha256 -cnotmatch '^[0-9a-f]{64}$' -or
        $record.rids -isnot [array] -or $record.rids.Count -eq 0 -or @($record.rids | Select-Object -Unique).Count -ne $record.rids.Count) {
        throw 'Invalid release record; package was not replaced.'
    }
    foreach ($entry in Get-ChildItem -LiteralPath $Root -Recurse -Force) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Published package contains a link.' }
    }
    $runtime = Resolve-MultimediaPath $Root 'runtime'
    if (@(Get-ChildItem -LiteralPath $runtime -Directory).Count -ne $record.rids.Count) { throw 'Release record does not match installed RID directories.' }
    foreach ($rid in $record.rids) {
        if ($rid -isnot [string] -or $rid -cnotin $script:SupportedRids) { throw 'Unknown installed RID.' }
        $binary = $Project.Executable
        if ($rid.StartsWith('win-')) { $binary += '.exe' }
        if (-not (Test-Path -LiteralPath (Resolve-MultimediaPath $Root "runtime/$rid/$binary") -PathType Leaf)) { throw 'Installed RID is missing its CLI.' }
    }
    if ((Get-MultimediaSharedHash $Root) -cne $record.shared_files_sha256) { throw 'Published shared files do not match their release hash.' }
    return $record
}

function Assert-MultimediaReleaseCompatibility([object]$Previous, [object]$Current) {
    if ($Previous.version -cne $Current.version) {
        if ([version]$Current.version -le [version]$Previous.version) { throw 'Skill version must increase when replacing a different version.' }
        return $false
    }
    foreach ($field in @('skill_name', 'contract_version', 'source_revision', 'shared_files_sha256')) {
        if ($Previous.$field -cne $Current.$field) { throw "Same-version RID merge rejected: $field differs." }
    }
    return $true
}
