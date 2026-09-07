using System.Text.Json;
using System.Text.RegularExpressions;

namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>
/// Recognizes host <c>skill.search</c> tool results and upserts Run-level activations.
/// This is the only same-turn activation path for host-mode progressive discovery.
/// </summary>
public static partial class SkillSearchActivationHook
{
    public const string CanonicalToolId = "host.skill.search";
    public const string DisplayName = "skill.search";
    public const string ProviderSafeName = "skill_search";

    /// <summary>Returns whether the tool identity refers to host skill.search.</summary>
    public static bool IsSkillSearchTool(string? toolId, string? toolName = null)
    {
        if (MatchesSkillSearch(toolId))
        {
            return true;
        }

        return MatchesSkillSearch(toolName);
    }

    /// <summary>
    /// Parses search hits from tool output JSON and activates names within the allowlist.
    /// </summary>
    /// <returns>Number of names newly accepted or refreshed in the activation store.</returns>
    public static int TryApplySearchResult(
        SkillActivationStore store,
        string? toolId,
        string? toolName,
        string? outputJson,
        string[]? allowlistSkillIds)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!IsSkillSearchTool(toolId, toolName))
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(outputJson))
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(outputJson);
            var hits = LocateHitsArray(document.RootElement);
            if (hits is null)
            {
                return 0;
            }

            var applied = 0;
            foreach (var hit in hits.Value.EnumerateArray())
            {
                if (!TryReadHit(hit, out var name, out var scope, out var description))
                {
                    continue;
                }

                if (store.TryActivate(name, scope, allowlistSkillIds, description))
                {
                    applied++;
                }
            }

            return applied;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static bool MatchesSkillSearch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Equals(CanonicalToolId, StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals(DisplayName, StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals(ProviderSafeName, StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".skill.search", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("_skill_search", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".skill_search", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Versioned / encoded host ids, e.g. host.host..skill.search..1.0.0 or host.x.skill_search.v1
        return SkillSearchIdentityRegex().IsMatch(trimmed);
    }

    [GeneratedRegex(
        @"(^|[.\-_])skill[.\-_]search([.\-_]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SkillSearchIdentityRegex();

    private static JsonElement? LocateHitsArray(JsonElement root)
    {
        if (root.ValueKind is JsonValueKind.Array)
        {
            return root;
        }

        if (root.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("hits", out var hits) && hits.ValueKind is JsonValueKind.Array)
        {
            return hits;
        }

        if (root.TryGetProperty("results", out var results) && results.ValueKind is JsonValueKind.Array)
        {
            return results;
        }

        if (root.TryGetProperty("skills", out var skills) && skills.ValueKind is JsonValueKind.Array)
        {
            return skills;
        }

        return null;
    }

    private static bool TryReadHit(
        JsonElement hit,
        out string name,
        out string? scope,
        out string? description)
    {
        name = string.Empty;
        scope = null;
        description = null;
        if (hit.ValueKind is JsonValueKind.String)
        {
            name = hit.GetString()?.Trim() ?? string.Empty;
            return name.Length > 0;
        }

        if (hit.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }

        if (!TryGetString(hit, "name", out var resolvedName)
            && !TryGetString(hit, "skillName", out resolvedName)
            && !TryGetString(hit, "skill", out resolvedName)
            && !TryGetString(hit, "id", out resolvedName))
        {
            return false;
        }

        name = resolvedName ?? string.Empty;
        _ = TryGetString(hit, "scope", out scope)
            || TryGetString(hit, "source", out scope)
            || TryGetString(hit, "sourceId", out scope);
        _ = TryGetString(hit, "description", out description)
            || TryGetString(hit, "shortDescription", out description)
            || TryGetString(hit, "summary", out description);
        return name.Length > 0;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind is not JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()?.Trim();
        return !string.IsNullOrWhiteSpace(value);
    }
}
