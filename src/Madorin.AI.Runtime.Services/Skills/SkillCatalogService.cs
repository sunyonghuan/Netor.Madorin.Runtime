using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Services.Skills;

/// <summary>One immutable Skill entry used by an Invocation.</summary>
public sealed record SkillCatalogEntry(
    string Name,
    string Description,
    string SourceId,
    string ContentHash,
    string MaterializedPath,
    ImmutableArray<string> Resources,
    ImmutableArray<string> Scripts)
{
    public SkillCatalogSkill ToContract() =>
        new(Name, Description, SourceId, ContentHash, MaterializedPath, Resources.ToArray(), Scripts.ToArray());
}

public sealed class SkillCatalogSnapshot
{
    private readonly ImmutableDictionary<string, SkillCatalogEntry> _skills;

    internal SkillCatalogSnapshot(
        string version,
        IEnumerable<SkillCatalogEntry> skills,
        IEnumerable<SkillSourceResult> sources,
        IEnumerable<SkillCatalogDiagnostic> diagnostics)
    {
        Version = version;
        _skills = skills.ToImmutableDictionary(static x => x.Name, StringComparer.OrdinalIgnoreCase);
        Skills = _skills.Values.OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        Sources = sources.ToImmutableArray();
        Diagnostics = diagnostics.ToImmutableArray();
    }

    public string Version { get; }
    public ImmutableArray<SkillCatalogEntry> Skills { get; }
    public ImmutableArray<SkillSourceResult> Sources { get; }
    public ImmutableArray<SkillCatalogDiagnostic> Diagnostics { get; }

    public bool TryGetSkill(string name, out SkillCatalogEntry? skill)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            skill = null;
            return false;
        }

        return _skills.TryGetValue(name, out skill);
    }

    public SkillCatalogSummary ToContract() =>
        new(Version, Skills.Length, Skills.Select(static x => x.ToContract()).ToArray(), Sources.ToArray(), Diagnostics.ToArray());
}

public sealed class SkillCatalogRefreshException(string code, string message, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    public string Code { get; } = code;
}

public enum SkillCatalogSourceScope
{
    Effective = 0,
    Workspace = 1,
    UserGlobal = 2
}

/// <summary>
/// Resolves, validates and materializes local directory Skills. Instances own their
/// catalog and materialization root, so Runtime processes never share mutable state.
/// </summary>
public sealed class SkillCatalogService : IAsyncDisposable
{
    private const int MaxCatalogSkills = 512;
    private const int MaxSkillFiles = 256;
    private const int MaxSkillDefinitionBytes = 1024 * 1024;
    private const int MaxSkillFileBytes = 8 * 1024 * 1024;
    private const int MaxSkillTotalBytes = 16 * 1024 * 1024;
    private const int WatchDebounceMilliseconds = 250;
    private static readonly Regex SkillNamePattern = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1", ".sh", ".py", ".js", ".cs", ".csx"
    };
    private static readonly HashSet<string> ResourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt", ".json", ".yaml", ".yml", ".csv", ".xml",
        ".html", ".css", ".js", ".ts", ".png", ".jpg", ".jpeg", ".gif", ".svg"
    };

    private readonly string _workspaceRoot;
    private readonly string _materializationRoot;
    private readonly string _runtimeInstanceId;
    private readonly IReadOnlyList<SkillSourceDescriptor> _explicitSources;
    private readonly SkillCatalogSourceScope _sourceScope;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _watcherSync = new();
    private readonly Timer _staleTimer;
    private List<FileSystemWatcher> _watchers = [];
    private SkillCatalogSnapshot _current;
    private long _changeGeneration;
    private int _isStale;
    private int _disposed;

    public SkillCatalogService(
        string workspaceRoot,
        string dataDirectory,
        string runtimeInstanceId,
        IReadOnlyList<SkillSourceDescriptor>? explicitSources = null,
        SkillCatalogSourceScope sourceScope = SkillCatalogSourceScope.Effective)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        var instanceKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runtimeInstanceId)))[..24]
            .ToLowerInvariant();
        _materializationRoot = Path.Combine(
            Path.GetFullPath(dataDirectory),
            "skill-catalog",
            "instances",
            instanceKey);
        _runtimeInstanceId = runtimeInstanceId;
        _explicitSources = explicitSources ?? [];
        _sourceScope = sourceScope;
        _staleTimer = new Timer(
            static state => ((SkillCatalogService)state!).MarkStale(),
            this,
            Timeout.Infinite,
            Timeout.Infinite);
        _current = new SkillCatalogSnapshot("empty", [], [], []);
    }

    public SkillCatalogSnapshot Current => Volatile.Read(ref _current);

    public bool IsStale => Volatile.Read(ref _isStale) != 0;

    public event Action<SkillCatalogChangedEvent>? Changed;

    public async Task<SkillCatalogSnapshot> InitializeAsync(CancellationToken ct = default) =>
        await RefreshAsync(ct).ConfigureAwait(false);

    public async Task<SkillCatalogSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previous = Current;
            var observedGeneration = Volatile.Read(ref _changeGeneration);
            SkillCatalogSnapshot next;
            try
            {
                next = await BuildSnapshotAsync(ct).ConfigureAwait(false);
            }
            catch (SkillCatalogRefreshException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                throw new SkillCatalogRefreshException(
                    RuntimeErrorCodes.SkillRefreshFailed,
                    "Skill catalog refresh failed.",
                    ex);
            }

            Volatile.Write(ref _current, next);
            RebuildWatchers(next.Sources);
            Volatile.Write(
                ref _isStale,
                Volatile.Read(ref _changeGeneration) == observedGeneration ? 0 : 1);

            if (!string.Equals(previous.Version, next.Version, StringComparison.Ordinal))
            {
                var changedNames = previous.Skills.Select(static x => x.Name)
                    .Concat(next.Skills.Select(static x => x.Name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(name => !SameSkill(previous, next, name))
                    .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                Changed?.Invoke(new SkillCatalogChangedEvent(
                    _runtimeInstanceId,
                    previous.Version,
                    next.Version,
                    next.Skills.Length,
                    changedNames,
                    DateTimeOffset.UtcNow));
            }

            return Current;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task<SkillCatalogSnapshot> GetSnapshotForInvocationAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (!IsStale)
        {
            return Current;
        }

        try
        {
            return await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (SkillCatalogRefreshException)
        {
            // A transient refresh failure must not invalidate an already captured catalog.
            return Current;
        }
    }

    public SkillCatalogSummary GetSummary() => Current.ToContract();

    public bool TryGetSkill(string name, out SkillCatalogEntry? skill) => Current.TryGetSkill(name, out skill);

    public async Task<string> LoadSkillAsync(string name, CancellationToken ct = default)
    {
        if (!Current.TryGetSkill(name, out var skill) || skill is null)
        {
            throw new KeyNotFoundException($"Skill '{name}' was not found in catalog '{Current.Version}'.");
        }

        var path = Path.Combine(skill.MaterializedPath, "SKILL.md");
        return await ReadBoundedTextAsync(path, skill.MaterializedPath, ct).ConfigureAwait(false);
    }

    public async Task<byte[]> ReadResourceAsync(string name, string relativePath, CancellationToken ct = default)
    {
        if (!Current.TryGetSkill(name, out var skill) || skill is null)
        {
            throw new KeyNotFoundException($"Skill '{name}' was not found in catalog '{Current.Version}'.");
        }

        var normalized = NormalizeRelativePath(relativePath);
        if (normalized.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)
            || !skill.Resources.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            throw new SkillCatalogRefreshException(
                RuntimeErrorCodes.SkillResourceBlocked,
                $"Resource '{relativePath}' is not declared by Skill '{name}'.");
        }

        var path = EnsureWithin(skill.MaterializedPath, normalized);
        return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
    }

    internal static IReadOnlyList<SkillSourceDescriptor> ResolveSources(
        string workspaceRoot,
        IReadOnlyList<SkillSourceDescriptor> explicitSources,
        SkillCatalogSourceScope sourceScope = SkillCatalogSourceScope.Effective)
    {
        var workspace = Path.Combine(Path.GetFullPath(workspaceRoot), ".madorin", "skills");
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var global = Path.Combine(userHome, ".madorin", "skills");
        var result = new List<SkillSourceDescriptor>();
        if (sourceScope is SkillCatalogSourceScope.Effective or SkillCatalogSourceScope.Workspace)
        {
            result.Add(new SkillSourceDescriptor("workspace", SkillSourceKind.Directory, workspace));
        }

        if (sourceScope is SkillCatalogSourceScope.Effective or SkillCatalogSourceScope.UserGlobal)
        {
            result.Add(new SkillSourceDescriptor("user-global", SkillSourceKind.Directory, global));
        }

        if (sourceScope is SkillCatalogSourceScope.Effective)
        {
            result.AddRange(explicitSources);
        }

        return result;
    }

    private async Task<SkillCatalogSnapshot> BuildSnapshotAsync(CancellationToken ct)
    {
        var sources = ResolveSources(_workspaceRoot, _explicitSources, _sourceScope);
        var diagnostics = new List<SkillCatalogDiagnostic>();
        var sourceResults = new List<SkillSourceResult>();
        var normalizedLocations = new HashSet<string>(GetPathComparer());
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<SkillCandidate>();

        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(source.SourceId) || !sourceIds.Add(source.SourceId))
            {
                throw new SkillCatalogRefreshException(
                    RuntimeErrorCodes.SkillSourceUnavailable,
                    $"Skill source ID '{source.SourceId}' is empty or duplicated.");
            }

            if (source.Kind is not SkillSourceKind.Directory)
            {
                diagnostics.Add(new SkillCatalogDiagnostic(
                    RuntimeErrorCodes.SkillSourceKindNotSupported,
                    "error",
                    $"Skill source kind '{source.Kind}' is not supported by this Runtime.",
                    source.SourceId));
                throw new SkillCatalogRefreshException(
                    RuntimeErrorCodes.SkillSourceKindNotSupported,
                    $"Skill source '{source.SourceId}' uses unsupported kind '{source.Kind}'.");
            }

            var location = NormalizeLocation(source.Location);
            if (PathsOverlap(location, _materializationRoot))
            {
                throw new SkillCatalogRefreshException(
                    RuntimeErrorCodes.SkillSourceUnavailable,
                    $"Skill source '{source.SourceId}' overlaps the Runtime materialization directory.");
            }

            if (!normalizedLocations.Add(location))
            {
                diagnostics.Add(new SkillCatalogDiagnostic(
                    "SkillSourceDuplicate",
                    "warning",
                    "The normalized source directory was already scanned; this source was skipped.",
                    source.SourceId));
                sourceResults.Add(new SkillSourceResult(source.SourceId, source.Kind, location, "duplicate", 0, "SkillSourceDuplicate"));
                continue;
            }

            var isAutomatic = source.SourceId is "workspace" or "user-global";
            if (!Directory.Exists(location))
            {
                if (!isAutomatic)
                {
                    throw new SkillCatalogRefreshException(
                        RuntimeErrorCodes.SkillSourceNotFound,
                        $"Explicit Skill source '{source.SourceId}' does not exist: '{location}'.");
                }

                sourceResults.Add(new SkillSourceResult(source.SourceId, source.Kind, location, "empty", 0));
                continue;
            }

            if (File.GetAttributes(location).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new SkillCatalogRefreshException(
                    RuntimeErrorCodes.SkillSourceUnavailable,
                    $"Skill source '{source.SourceId}' cannot be a symbolic link or reparse point.");
            }

            var sourceCandidates = new List<SkillCandidate>();
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(location).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                    {
                        diagnostics.Add(new SkillCatalogDiagnostic(
                            RuntimeErrorCodes.SkillResourceBlocked,
                            "error",
                            "A Skill directory cannot be a symbolic link or reparse point.",
                            source.SourceId,
                            Path.GetFileName(directory),
                            directory));
                        continue;
                    }

                    var skillFile = Path.Combine(directory, "SKILL.md");
                    if (!File.Exists(skillFile))
                    {
                        continue;
                    }

                    try
                    {
                        var candidate = await ParseCandidateAsync(source, directory, skillFile, diagnostics, ct).ConfigureAwait(false);
                        if (candidate is not null)
                        {
                            sourceCandidates.Add(candidate);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        diagnostics.Add(new SkillCatalogDiagnostic(
                            RuntimeErrorCodes.SkillDefinitionInvalid,
                            "error",
                            $"The Skill could not be read: {ex.Message}",
                            source.SourceId,
                            Path.GetFileName(directory),
                            skillFile));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SkillCatalogRefreshException(
                    RuntimeErrorCodes.SkillSourceUnavailable,
                    $"Skill source '{source.SourceId}' could not be enumerated.",
                    ex);
            }

            var validSourceCount = 0;
            foreach (var group in sourceCandidates.GroupBy(static x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var conflicts = group.ToArray();
                if (conflicts.Length > 1)
                {
                    foreach (var conflict in conflicts)
                    {
                        diagnostics.Add(new SkillCatalogDiagnostic(
                            RuntimeErrorCodes.SkillDuplicateInSource,
                            "error",
                            $"Skill name '{conflict.Name}' is duplicated within source '{source.SourceId}'; all conflicting entries were disabled.",
                            source.SourceId,
                            conflict.Name,
                            conflict.Directory));
                    }

                    continue;
                }

                candidates.Add(conflicts[0]);
                validSourceCount++;
            }

            sourceResults.Add(new SkillSourceResult(source.SourceId, source.Kind, location, "loaded", validSourceCount));
        }

        var winningCandidates = new Dictionary<string, SkillCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidates.GroupBy(static x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var winner = group.First();
            if (group.Skip(1).Any())
            {
                foreach (var shadowed in group.Skip(1))
                {
                    diagnostics.Add(new SkillCatalogDiagnostic(
                        "SkillShadowed",
                        "warning",
                        $"Skill '{shadowed.Name}' was shadowed by source '{winner.Source.SourceId}'.",
                        shadowed.Source.SourceId,
                        shadowed.Name,
                        shadowed.Directory));
                }
            }

            winningCandidates[group.Key] = winner;
        }

        if (winningCandidates.Count > MaxCatalogSkills)
        {
            throw new SkillCatalogRefreshException(
                RuntimeErrorCodes.SkillSourceUnavailable,
                $"The effective Skill catalog exceeds the {MaxCatalogSkills}-Skill limit.");
        }

        if (winningCandidates.Count == 0)
        {
            return new SkillCatalogSnapshot("empty", [], sourceResults, diagnostics);
        }

        var version = ComputeVersion(winningCandidates.Values.Select(static x => x.ToEntryPlaceholder()));
        var materialized = await MaterializeSnapshotAsync(version, winningCandidates.Values, ct).ConfigureAwait(false);
        return new SkillCatalogSnapshot(version, materialized.Values, sourceResults, diagnostics);
    }

    private static async Task<SkillCandidate?> ParseCandidateAsync(
        SkillSourceDescriptor source,
        string directory,
        string skillFile,
        List<SkillCatalogDiagnostic> diagnostics,
        CancellationToken ct)
    {
        var discoveredFiles = EnumerateSkillFiles(
            source,
            directory,
            Path.GetFileName(directory),
            diagnostics,
            ct);
        if (discoveredFiles is null)
        {
            return null;
        }

        var definitionFiles = discoveredFiles
            .Where(static file => file.RelativePath.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (definitionFiles.Length != 1
            || !definitionFiles[0].RelativePath.Equals("SKILL.md", StringComparison.Ordinal))
        {
            diagnostics.Add(new SkillCatalogDiagnostic(
                RuntimeErrorCodes.SkillDefinitionInvalid,
                "error",
                "A Skill must contain exactly one root SKILL.md with canonical casing.",
                source.SourceId,
                null,
                skillFile));
            return null;
        }

        var definition = await ReadFileWithinLimitAsync(
            definitionFiles[0].FullPath,
            MaxSkillDefinitionBytes,
            ct).ConfigureAwait(false);
        if (definition is null)
        {
            diagnostics.Add(new SkillCatalogDiagnostic(
                RuntimeErrorCodes.SkillResourceBlocked,
                "error",
                $"SKILL.md exceeds the {MaxSkillDefinitionBytes}-byte limit.",
                source.SourceId,
                null,
                skillFile));
            return null;
        }

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(definition);
        }
        catch (DecoderFallbackException)
        {
            diagnostics.Add(new SkillCatalogDiagnostic(RuntimeErrorCodes.SkillDefinitionInvalid, "error", "SKILL.md is not valid UTF-8.", source.SourceId, null, skillFile));
            return null;
        }

        if (!TryParseFrontmatter(text, out var frontmatter, out _))
        {
            diagnostics.Add(new SkillCatalogDiagnostic(RuntimeErrorCodes.SkillDefinitionInvalid, "error", "SKILL.md must start with YAML frontmatter.", source.SourceId, null, skillFile));
            return null;
        }

        var name = frontmatter.GetValueOrDefault("name")?.Trim();
        var description = frontmatter.GetValueOrDefault("description")?.Trim();
        if (string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(description)
            || !SkillNamePattern.IsMatch(name)
            || !string.Equals(Path.GetFileName(directory), name, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new SkillCatalogDiagnostic(RuntimeErrorCodes.SkillDefinitionInvalid, "error", "Skill name, description, or directory name is invalid.", source.SourceId, name, skillFile));
            return null;
        }

        var resources = new List<string>();
        var scripts = new List<string>();
        var files = new List<SkillCandidateFile>
        {
            new("SKILL.md", definition)
        };
        var totalBytes = definition.LongLength;
        foreach (var file in discoveredFiles
            .Where(static file => !file.RelativePath.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static file => file.RelativePath, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var relative = file.RelativePath;
            var isScript = relative.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase);
            if (isScript && !ScriptExtensions.Contains(Path.GetExtension(file.FullPath)))
            {
                diagnostics.Add(new SkillCatalogDiagnostic(
                    RuntimeErrorCodes.SkillScriptUnsupported,
                    "warning",
                    $"Script extension '{Path.GetExtension(file.FullPath)}' is not supported.",
                    source.SourceId,
                    name,
                    file.FullPath));
                continue;
            }

            if (!isScript && !ResourceExtensions.Contains(Path.GetExtension(file.FullPath)))
            {
                diagnostics.Add(new SkillCatalogDiagnostic(
                    RuntimeErrorCodes.SkillResourceBlocked,
                    "warning",
                    $"Resource extension '{Path.GetExtension(file.FullPath)}' is not exposed.",
                    source.SourceId,
                    name,
                    file.FullPath));
                continue;
            }

            var content = await ReadFileWithinLimitAsync(file.FullPath, MaxSkillFileBytes, ct).ConfigureAwait(false);
            if (content is null || totalBytes + content.LongLength > MaxSkillTotalBytes)
            {
                diagnostics.Add(new SkillCatalogDiagnostic(
                    RuntimeErrorCodes.SkillResourceBlocked,
                    "error",
                    content is null
                        ? $"File exceeds the {MaxSkillFileBytes}-byte limit."
                        : $"Skill files exceed the {MaxSkillTotalBytes}-byte total limit.",
                    source.SourceId,
                    name,
                    file.FullPath));
                continue;
            }

            totalBytes += content.LongLength;
            files.Add(new SkillCandidateFile(relative, content));
            if (isScript)
            {
                scripts.Add(relative);
            }
            else
            {
                resources.Add(relative);
            }
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.OrderBy(static x => x.RelativePath, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"{file.RelativePath.Length}:{file.RelativePath}:{file.Content.Length}:"));
            hash.AppendData(file.Content);
        }

        return new SkillCandidate(
            name,
            description,
            source,
            directory,
            resources.ToImmutableArray(),
            scripts.ToImmutableArray(),
            files.ToImmutableArray(),
            Convert.ToHexString(hash.GetHashAndReset()));
    }

    private async Task<Dictionary<string, SkillCatalogEntry>> MaterializeSnapshotAsync(
        string version,
        IEnumerable<SkillCandidate> candidates,
        CancellationToken ct)
    {
        var candidateList = candidates.OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var versionRoot = Path.Combine(_materializationRoot, version);
        if (!Directory.Exists(versionRoot))
        {
            Directory.CreateDirectory(_materializationRoot);
            var stagingRoot = Path.Combine(
                _materializationRoot,
                $".{version}.{Guid.NewGuid():N}.tmp");
            try
            {
                Directory.CreateDirectory(stagingRoot);
                foreach (var candidate in candidateList)
                {
                    ct.ThrowIfCancellationRequested();
                    var skillRoot = EnsureWithin(stagingRoot, candidate.Name);
                    foreach (var file in candidate.Files)
                    {
                        var destination = EnsureWithin(skillRoot, file.RelativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        await File.WriteAllBytesAsync(destination, file.Content, ct).ConfigureAwait(false);
                    }
                }

                try
                {
                    Directory.Move(stagingRoot, versionRoot);
                }
                catch (IOException) when (Directory.Exists(versionRoot))
                {
                    DeleteStagingDirectory(stagingRoot);
                }
            }
            catch
            {
                DeleteStagingDirectory(stagingRoot);
                throw;
            }
        }

        var materialized = new Dictionary<string, SkillCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidateList)
        {
            materialized[candidate.Name] = candidate.ToEntry(Path.Combine(versionRoot, candidate.Name));
        }

        return materialized;
    }

    private static List<SkillDiscoveredFile>? EnumerateSkillFiles(
        SkillSourceDescriptor source,
        string root,
        string skillName,
        List<SkillCatalogDiagnostic> diagnostics,
        CancellationToken ct)
    {
        var files = new List<SkillDiscoveredFile>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);
        while (pendingDirectories.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var child in Directory.EnumerateDirectories(directory).OrderByDescending(static x => x, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new SkillCatalogDiagnostic(
                        RuntimeErrorCodes.SkillResourceBlocked,
                        "error",
                        "Symbolic-link directories and reparse points are not allowed in a Skill.",
                        source.SourceId,
                        skillName,
                        child));
                    continue;
                }

                pendingDirectories.Push(child);
            }

            foreach (var file in Directory.EnumerateFiles(directory).OrderBy(static x => x, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new SkillCatalogDiagnostic(
                        RuntimeErrorCodes.SkillResourceBlocked,
                        "error",
                        "Symbolic-link files and reparse points are not allowed in a Skill.",
                        source.SourceId,
                        skillName,
                        file));
                    continue;
                }

                files.Add(new SkillDiscoveredFile(
                    Path.GetRelativePath(root, file).Replace('\\', '/'),
                    file));
                if (files.Count > MaxSkillFiles)
                {
                    diagnostics.Add(new SkillCatalogDiagnostic(
                        RuntimeErrorCodes.SkillResourceBlocked,
                        "error",
                        $"Skill '{skillName}' exceeds the {MaxSkillFiles}-file limit.",
                        source.SourceId,
                        skillName,
                        root));
                    return null;
                }
            }
        }

        var ambiguousPaths = files
            .GroupBy(static x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .SelectMany(static group => group)
            .ToArray();
        if (ambiguousPaths.Length > 0)
        {
            foreach (var file in ambiguousPaths)
            {
                diagnostics.Add(new SkillCatalogDiagnostic(
                    RuntimeErrorCodes.SkillResourceBlocked,
                    "error",
                    "Case-insensitive duplicate paths are not allowed in a Skill.",
                    source.SourceId,
                    skillName,
                    file.FullPath));
            }

            return null;
        }

        return files;
    }

    private static async Task<byte[]?> ReadFileWithinLimitAsync(
        string path,
        int maxBytes,
        CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        if (stream.Length > maxBytes)
        {
            return null;
        }

        var content = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(content, ct).ConfigureAwait(false);
        return content;
    }

    private void RebuildWatchers(IEnumerable<SkillSourceResult> sources)
    {
        var replacements = new List<FileSystemWatcher>();
        try
        {
            foreach (var location in sources
                .Where(static source => source.Kind is SkillSourceKind.Directory)
                .Select(static source => source.Location)
                .Distinct(GetPathComparer()))
            {
                if (Directory.Exists(location))
                {
                    replacements.Add(CreateWatcher(location, includeSubdirectories: true, location));
                }

                var parent = Directory.GetParent(location)?.FullName;
                var nearestExistingParent = FindNearestExistingDirectory(parent);
                if (nearestExistingParent is not null
                    && !string.Equals(nearestExistingParent, location, GetPathComparison()))
                {
                    replacements.Add(CreateWatcher(nearestExistingParent, includeSubdirectories: false, location));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            foreach (var watcher in replacements)
            {
                watcher.Dispose();
            }

            MarkStale();
            return;
        }

        List<FileSystemWatcher> previous;
        lock (_watcherSync)
        {
            previous = _watchers;
            _watchers = replacements;
        }

        foreach (var watcher in previous)
        {
            watcher.Dispose();
        }
    }

    private FileSystemWatcher CreateWatcher(
        string watchRoot,
        bool includeSubdirectories,
        string sourceLocation)
    {
        var watcher = new FileSystemWatcher(watchRoot)
        {
            IncludeSubdirectories = includeSubdirectories,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size
                | NotifyFilters.CreationTime,
            InternalBufferSize = 16 * 1024
        };
        watcher.Changed += (_, args) => ScheduleStaleIfRelevant(sourceLocation, args.FullPath);
        watcher.Created += (_, args) => ScheduleStaleIfRelevant(sourceLocation, args.FullPath);
        watcher.Deleted += (_, args) => ScheduleStaleIfRelevant(sourceLocation, args.FullPath);
        watcher.Renamed += (_, args) =>
        {
            ScheduleStaleIfRelevant(sourceLocation, args.OldFullPath);
            ScheduleStaleIfRelevant(sourceLocation, args.FullPath);
        };
        watcher.Error += (_, _) => ScheduleStale();
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void ScheduleStaleIfRelevant(string sourceLocation, string changedPath)
    {
        if (IsSameOrUnder(changedPath, sourceLocation)
            || IsSameOrUnder(sourceLocation, changedPath))
        {
            ScheduleStale();
        }
    }

    private void ScheduleStale()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            _staleTimer.Change(WatchDebounceMilliseconds, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void MarkStale()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            Interlocked.Increment(ref _changeGeneration);
            Volatile.Write(ref _isStale, 1);
        }
    }

    private static string? FindNearestExistingDirectory(string? path)
    {
        var candidate = path;
        while (candidate is not null && !Directory.Exists(candidate))
        {
            candidate = Directory.GetParent(candidate)?.FullName;
        }

        return candidate;
    }

    private static bool PathsOverlap(string first, string second) =>
        IsSameOrUnder(first, second) || IsSameOrUnder(second, first);

    private static bool IsSameOrUnder(string candidate, string root)
    {
        var fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(fullCandidate, fullRoot, GetPathComparison())
            || fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, GetPathComparison());
    }

    private void DeleteStagingDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath)
            && IsSameOrUnder(fullPath, _materializationRoot)
            && Path.GetFileName(fullPath).EndsWith(".tmp", StringComparison.Ordinal))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private static bool TryParseFrontmatter(string text, out Dictionary<string, string> values, out string body)
    {
        values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        body = text;
        using var reader = new StringReader(text.TrimStart('\uFEFF'));
        if (!string.Equals(reader.ReadLine()?.Trim(), "---", StringComparison.Ordinal))
        {
            return false;
        }

        var line = reader.ReadLine();
        while (line is not null && !string.Equals(line.Trim(), "---", StringComparison.Ordinal))
        {
            var separator = line.IndexOf(':');
            if (separator > 0)
            {
                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim().Trim('"', '\'');
                values[key] = value;
            }

            line = reader.ReadLine();
        }

        if (line is null)
        {
            return false;
        }

        body = text[(text.IndexOf("---", StringComparison.Ordinal) + 3)..];
        var second = body.IndexOf("---", StringComparison.Ordinal);
        body = second >= 0 ? body[(second + 3)..].TrimStart() : string.Empty;
        return true;
    }

    private static string NormalizeLocation(string location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        return Path.GetFullPath(location.Trim());
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(static part => part is ".." or "."))
        {
            throw new SkillCatalogRefreshException(RuntimeErrorCodes.SkillResourceBlocked, "Resource paths cannot contain traversal segments.");
        }

        return normalized;
    }

    private static string EnsureWithin(string root, string relative)
    {
        var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, GetPathComparison()))
        {
            throw new SkillCatalogRefreshException(RuntimeErrorCodes.SkillResourceBlocked, "Resource path escapes the Skill directory.");
        }

        return candidate;
    }

    private static async Task<string> ReadBoundedTextAsync(string path, string root, CancellationToken ct)
    {
        _ = EnsureWithin(root, Path.GetFileName(path));
        return await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
    }

    private static string ComputeVersion(IEnumerable<SkillCatalogEntry> entries)
    {
        var value = string.Join('\n', entries.OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase).Select(static x => $"{x.Name}|{x.SourceId}|{x.ContentHash}"));
        return "v-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24].ToLowerInvariant();
    }

    private static bool SameSkill(SkillCatalogSnapshot first, SkillCatalogSnapshot second, string name) =>
        first.TryGetSkill(name, out var a)
        && second.TryGetSkill(name, out var b)
        && string.Equals(a!.ContentHash, b!.ContentHash, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.SourceId, b.SourceId, StringComparison.Ordinal);

    private static StringComparer GetPathComparer() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison GetPathComparison() => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _staleTimer.DisposeAsync().ConfigureAwait(false);
            List<FileSystemWatcher> watchers;
            lock (_watcherSync)
            {
                watchers = _watchers;
                _watchers = [];
            }

            foreach (var watcher in watchers)
            {
                watcher.Dispose();
            }

            _refreshGate.Dispose();
        }
    }

    private sealed record SkillDiscoveredFile(string RelativePath, string FullPath);

    private sealed record SkillCandidateFile(string RelativePath, byte[] Content);

    private sealed record SkillCandidate(
        string Name,
        string Description,
        SkillSourceDescriptor Source,
        string Directory,
        ImmutableArray<string> Resources,
        ImmutableArray<string> Scripts,
        ImmutableArray<SkillCandidateFile> Files,
        string ContentHash)
    {
        public SkillCatalogEntry ToEntryPlaceholder() => new(Name, Description, Source.SourceId, ContentHash, string.Empty, Resources, Scripts);
        public SkillCatalogEntry ToEntry(string materializedPath) => new(Name, Description, Source.SourceId, ContentHash, materializedPath, Resources, Scripts);
    }
}
