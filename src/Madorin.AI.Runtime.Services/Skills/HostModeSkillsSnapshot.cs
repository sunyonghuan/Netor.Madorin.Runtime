using System.Collections.ObjectModel;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Services.Skills;

/// <summary>
/// Builds host-mode Invocation skill fields: catalog metadata is retained for load.
/// Snapshot MaterializedPaths stay empty at request build; live Loadable is resolved later
/// via <c>MafSkillsAgentFactory.ResolveHostLoadableMaterializedPaths</c>
/// (progressive: activation; direct: full allowlist).
/// </summary>
public static class HostModeSkillsSnapshot
{
    public static HostModeSkillsMaps BuildMaps(SkillCatalogSnapshot? allowlistedCatalog)
    {
        if (allowlistedCatalog is null || allowlistedCatalog.Skills.Length == 0)
        {
            var emptyRoots = allowlistedCatalog is null
                ? []
                : BuildScopeRoots(allowlistedCatalog);
            return new HostModeSkillsMaps(
                new ReadOnlyDictionary<string, string>(
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
                new ReadOnlyDictionary<string, string>(
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
                emptyRoots);
        }

        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in allowlistedCatalog.Skills)
        {
            paths[skill.Name] = skill.MaterializedPath;
            descriptions[skill.Name] = skill.Description;
        }

        return new HostModeSkillsMaps(
            new ReadOnlyDictionary<string, string>(paths),
            new ReadOnlyDictionary<string, string>(descriptions),
            BuildScopeRoots(allowlistedCatalog));
    }

    /// <summary>
    /// Projects an InvocationSnapshot for host mode: keeps catalog version for script routing,
    /// but clears snapshot paths so host mode never uses independent-mode full-path Provider.
    /// Live Loadable (progressive activation or direct allowlist) is resolved at invoke time.
    /// </summary>
    public static InvocationSnapshot ProjectAdvertiseEmpty(
        InvocationSnapshot baseSnapshot,
        SkillCatalogSnapshot? catalogForVersion)
    {
        var version = catalogForVersion?.Version ?? baseSnapshot.SkillCatalogVersion;
        return baseSnapshot with
        {
            SkillCatalogVersion = version,
            // Empty here is intentional: host Loadable is dynamic (activation or direct allowlist).
            SkillNames = [],
            SkillContentHashes = [],
            SkillSourceIds = [],
            SkillMaterializedPaths = []
        };
    }

    public static void TryApplyToolResult(
        SkillActivationStore? store,
        string[]? allowlistSkillIds,
        string toolId,
        string? toolName,
        string? outputJson,
        string? skillDiscoveryMode = null)
    {
        if (store is null)
        {
            return;
        }

        _ = SkillSearchActivationHook.TryApplySearchResult(
            store,
            toolId,
            toolName,
            outputJson,
            allowlistSkillIds,
            skillDiscoveryMode: skillDiscoveryMode);
    }

    private static List<SkillScopeSourceRoot> BuildScopeRoots(SkillCatalogSnapshot catalog)
    {
        if (catalog.Sources.Length == 0)
        {
            return [];
        }

        var roots = new List<SkillScopeSourceRoot>(catalog.Sources.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in catalog.Sources)
        {
            if (source.Kind is not SkillSourceKind.Directory
                || string.IsNullOrWhiteSpace(source.Location)
                || !seen.Add(source.SourceId))
            {
                continue;
            }

            roots.Add(new SkillScopeSourceRoot(source.SourceId, source.Location));
        }

        return roots;
    }
}

public sealed record HostModeSkillsMaps(
    IReadOnlyDictionary<string, string> PathsByName,
    IReadOnlyDictionary<string, string> DescriptionsByName,
    IReadOnlyList<SkillScopeSourceRoot> ScopeSourceRoots);
