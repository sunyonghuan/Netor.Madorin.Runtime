using System.Collections.Concurrent;

namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>
/// Run-scoped skill activation table for host-mode progressive discovery.
/// Allowlist is applied when computing Loadable; activations themselves are run-wide.
/// </summary>
public sealed class SkillActivationStore
{
    private readonly ConcurrentDictionary<string, SkillActivationEntry> _activations =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Upserts an activation when the name is within the current allowlist.</summary>
    /// <returns><see langword="true"/> when the name was accepted into the activation set.</returns>
    public bool TryActivate(string name, string? scope, string[]? allowlistSkillIds, string? shortDescription = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalized = name.Trim();
        if (!IsAllowed(normalized, allowlistSkillIds))
        {
            return false;
        }

        _activations.AddOrUpdate(
            normalized,
            _ => new SkillActivationEntry(normalized, NormalizeScope(scope), NormalizeDescription(shortDescription)),
            (_, existing) => existing with
            {
                Scope = NormalizeScope(scope) ?? existing.Scope,
                ShortDescription = NormalizeDescription(shortDescription) ?? existing.ShortDescription
            });
        return true;
    }

    /// <summary>Returns whether <paramref name="name"/> is currently loadable under the allowlist.</summary>
    public bool IsLoadable(string name, string[]? allowlistSkillIds)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return _activations.ContainsKey(name.Trim())
            && IsAllowed(name.Trim(), allowlistSkillIds);
    }

    /// <summary>Returns activated names that remain inside the allowlist (Loadable set).</summary>
    public string[] GetLoadableNames(string[]? allowlistSkillIds)
    {
        if (_activations.IsEmpty)
        {
            return [];
        }

        if (allowlistSkillIds is { Length: 0 })
        {
            return [];
        }

        HashSet<string>? allowed = null;
        if (allowlistSkillIds is { Length: > 0 })
        {
            allowed = new HashSet<string>(allowlistSkillIds, StringComparer.OrdinalIgnoreCase);
        }

        return _activations.Keys
            .Where(name => allowed is null || allowed.Contains(name))
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Returns a snapshot of activation entries (including those outside a later allowlist).</summary>
    public IReadOnlyList<SkillActivationEntry> Snapshot() =>
        _activations.Values
            .OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static bool IsAllowed(string name, string[]? allowlistSkillIds)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        // null = undeclared allowlist → all catalog names allowed (compat / use with care).
        if (allowlistSkillIds is null)
        {
            return true;
        }

        // [] = explicitly none.
        if (allowlistSkillIds.Length == 0)
        {
            return false;
        }

        foreach (var id in allowlistSkillIds)
        {
            if (string.Equals(id, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? NormalizeScope(string? scope) =>
        string.IsNullOrWhiteSpace(scope) ? null : scope.Trim();

    private static string? NormalizeDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();
}

/// <summary>One activated skill entry inside a run.</summary>
public sealed record SkillActivationEntry(
    string Name,
    string? Scope,
    string? ShortDescription);
