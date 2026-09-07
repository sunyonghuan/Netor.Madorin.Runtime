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
            // the progressive-discovery tool surface (probe A / live outbound tools).
            return Task.FromResult<IList<AgentSkill>>(
            [
                new AgentInlineSkill(
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
                    serializerOptions: null)
            ]);
        }

        var skills = new List<AgentSkill>(paths.Length);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(path))
            {
                continue;
            }

            var skillMd = Path.Combine(path, "SKILL.md");
            if (!File.Exists(skillMd))
            {
                continue;
            }

            string content;
            try
            {
                content = File.ReadAllText(skillMd);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (!TryReadFrontmatterName(content, out var name) || string.IsNullOrWhiteSpace(name))
            {
                name = Path.GetFileName(
                    path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var description = ResolveShortDescription(name);
            var body = ExtractBody(content);
            skills.Add(new AgentInlineSkill(
                name,
                description,
                body,
                license: null,
                compatibility: null,
                allowedTools: null,
                metadata: null,
                serializerOptions: null));
        }

        return Task.FromResult<IList<AgentSkill>>(skills);
    }

    private string ResolveShortDescription(string name)
    {
        if (_context.ActivationStore is not null)
        {
            foreach (var entry in _context.ActivationStore.Snapshot())
            {
                if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(entry.ShortDescription))
                {
                    return TruncateDescription(entry.ShortDescription);
                }
            }
        }

        if (_context.CatalogDescriptionsByName is not null
            && _context.CatalogDescriptionsByName.TryGetValue(name, out var catalogDescription)
            && !string.IsNullOrWhiteSpace(catalogDescription))
        {
            return TruncateDescription(catalogDescription);
        }

        return name;
    }

    private static string TruncateDescription(string description)
    {
        const int maxLength = 160;
        var trimmed = description.Trim();
        if (trimmed.Length <= maxLength)
        {
            return trimmed;
        }

        return trimmed[..maxLength].TrimEnd() + "…";
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
