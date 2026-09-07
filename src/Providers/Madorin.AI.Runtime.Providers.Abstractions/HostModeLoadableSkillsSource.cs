using Microsoft.Agents.AI;

namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>
/// Host-mode MAF skills source that exposes only the current Loadable set.
/// Empty Loadable still yields a live source so <c>load_skill</c> tools stay registered.
/// Loadable growth is picked up on each <see cref="GetSkillsAsync"/> (DisableCaching).
/// File scripts continue to run through Runtime <c>builtin.skill.script.run</c> + Grant.
/// </summary>
public sealed class HostModeLoadableSkillsSource : AgentSkillsSource
{
    /// <summary>
    /// Upper bound for Advertise / discovery descriptions (MAF frontmatter-safe).
    /// Search hit descriptions are preferred intact; only oversize text is trimmed at a boundary.
    /// </summary>
    public const int MaxAdvertiseDescriptionLength = 1024;

    private static readonly string[] ResourceDirectories = ["references", "assets"];

    private readonly SkillsInvocationContext _context;

    public HostModeLoadableSkillsSource(
        SkillsInvocationContext context,
        AgentFileSkillScriptRunner? scriptRunner = null)
    {
        _ = scriptRunner;
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public override Task<IList<AgentSkill>> GetSkillsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var paths = MafSkillsAgentFactory.ResolveHostLoadableMaterializedPaths(_context);
        if (paths.Length == 0)
        {
            // MAF only registers load_skill / read_skill_resource / run_skill_script when the
            // skills context is non-empty. Keep a tiny guide skill so empty Loadable still exposes
            // the skill tool surface (probe A / live outbound tools).
            return Task.FromResult<IList<AgentSkill>>([CreateEmptyLoadableGuideSkill()]);
        }

        var skills = new List<AgentSkill>(paths.Length);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryCreateSkill(path, out var skill) || skill is null)
            {
                continue;
            }

            skills.Add(skill);
        }

        return Task.FromResult<IList<AgentSkill>>(skills);
    }

    private AgentInlineSkill CreateEmptyLoadableGuideSkill()
    {
        if (SkillDiscoveryModes.IsDirect(_context.SkillDiscoveryMode))
        {
            return new AgentInlineSkill(
                "host-skills-guide",
                "Direct skill discovery is enabled, but no allowlisted skills are currently loadable.",
                """
                Host-mode direct discovery has an empty Loadable set.
                1. Ensure AgentRef.skillIds lists enabled skill names (empty list means all off).
                2. Skills must exist in the Runtime catalog with a resolvable path.
                3. Call load_skill(name) only for allowlisted names; skill.search is not required.
                Do not invent skill names or absolute paths.
                """,
                license: null,
                compatibility: null,
                allowedTools: null,
                metadata: null,
                serializerOptions: null);
        }

        return new AgentInlineSkill(
            "host-skills-guide",
            "Progressive skill discovery is enabled. Call skill.search first, then load_skill.",
            """
            Host-mode skills start empty.
            1. Call skill.search to discover allowlisted skills.
            2. After search hits are activated by Runtime, call load_skill(name).
            3. Use read_skill_resource / run_skill_script only for loaded skills.
            Do not invent skill names or absolute paths.
            """,
            license: null,
            compatibility: null,
            allowedTools: null,
            metadata: null,
            serializerOptions: null);
    }

    private bool TryCreateSkill(string materializedPath, out AgentInlineSkill? skill)
    {
        skill = null;
        if (!Directory.Exists(materializedPath))
        {
            return false;
        }

        var skillMd = Path.Combine(materializedPath, "SKILL.md");
        if (!File.Exists(skillMd))
        {
            return false;
        }

        string content;
        try
        {
            content = File.ReadAllText(skillMd);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        if (!TryReadFrontmatterName(content, out var name) || string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileName(
                materializedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var description = ResolveAdvertiseDescription(name);
        var body = ExtractBody(content);
        skill = new AgentInlineSkill(
            name,
            description,
            body,
            license: null,
            compatibility: null,
            allowedTools: null,
            metadata: null,
            serializerOptions: null);

        foreach (var (resourceName, resourceValue) in EnumerateResources(materializedPath))
        {
            skill.AddResource(resourceName, resourceValue, description: null);
        }

        return true;
    }

    private string ResolveAdvertiseDescription(string name)
    {
        if (_context.ActivationStore is not null)
        {
            foreach (var entry in _context.ActivationStore.Snapshot())
            {
                if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(entry.ShortDescription))
                {
                    // Search hits should keep full discovery text (within MAF-safe bound).
                    return BoundDescription(entry.ShortDescription);
                }
            }
        }

        if (_context.CatalogDescriptionsByName is not null
            && _context.CatalogDescriptionsByName.TryGetValue(name, out var catalogDescription)
            && !string.IsNullOrWhiteSpace(catalogDescription))
        {
            return BoundDescription(catalogDescription);
        }

        return name;
    }

    /// <summary>
    /// Keeps full descriptions when possible. Only oversize text is cut at a sentence/paragraph
    /// boundary so capability clauses are not sliced mid-phrase.
    /// </summary>
    public static string BoundDescription(
        string description,
        int maxLength = MaxAdvertiseDescriptionLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);

        var trimmed = description.Trim();
        if (trimmed.Length <= maxLength)
        {
            return trimmed;
        }

        var window = trimmed.AsSpan(0, maxLength);
        var cut = LastBoundaryIndex(window);
        if (cut < Math.Min(80, maxLength / 2))
        {
            cut = maxLength;
        }

        return string.Concat(window[..cut].TrimEnd(), "…");
    }

    private static int LastBoundaryIndex(ReadOnlySpan<char> window)
    {
        for (var i = window.Length - 1; i >= 0; i--)
        {
            var c = window[i];
            if (c is '.' or '!' or '?' or '。' or '！' or '？' or '\n' or ';')
            {
                return i + 1;
            }
        }

        for (var i = window.Length - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(window[i]))
            {
                return i;
            }
        }

        return window.Length;
    }

    private static IEnumerable<(string Name, string Value)> EnumerateResources(string skillPath)
    {
        foreach (var directoryName in ResourceDirectories)
        {
            var directory = Path.Combine(skillPath, directoryName);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                var relative = Path.GetRelativePath(skillPath, file).Replace('\\', '/');
                yield return (relative, text);

                // Also expose bare file name for callers that omit the directory prefix
                // (MAF docs: "style-guide" not "style-guide.md" / "references/FAQ.md").
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrWhiteSpace(fileName)
                    && !string.Equals(fileName, relative, StringComparison.OrdinalIgnoreCase))
                {
                    yield return (fileName, text);
                }
            }
        }
    }

    private static bool TryReadFrontmatterName(string skillMd, out string name)
    {
        name = string.Empty;
        if (!skillMd.StartsWith("---", StringComparison.Ordinal))
        {
            return false;
        }

        var end = skillMd.IndexOf("---", 3, StringComparison.Ordinal);
        if (end < 0)
        {
            return false;
        }

        var yaml = skillMd[3..end];
        foreach (var rawLine in yaml.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            name = line["name:".Length..].Trim().Trim('"', '\'');
            return name.Length > 0;
        }

        return false;
    }

    private static string ExtractBody(string skillMd)
    {
        if (!skillMd.StartsWith("---", StringComparison.Ordinal))
        {
            return skillMd;
        }

        var end = skillMd.IndexOf("---", 3, StringComparison.Ordinal);
        if (end < 0 || end + 3 >= skillMd.Length)
        {
            return skillMd;
        }

        return skillMd[(end + 3)..].Trim();
    }
}
