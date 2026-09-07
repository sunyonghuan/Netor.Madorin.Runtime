namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>
/// Host-mode skill discovery strategy constants and parsing
/// (<c>AgentRef.skillDiscoveryMode</c> / <c>SkillsInvocationContext.SkillDiscoveryMode</c>).
/// </summary>
/// <remarks>
/// Capability gating is <c>SkillsCapabilities.DiscoveryDirect</c> (initialize JSON) — hosts must
/// not send <c>direct</c> until that flag is true. Runtime Normalize only maps the string value;
/// there is no second process-global switch (avoids dual-source drift).
/// </remarks>
public static class SkillDiscoveryModes
{
    public const string Progressive = "progressive";
    public const string Direct = "direct";

    /// <summary>
    /// Normalizes raw agent material: null/empty/unknown → progressive;
    /// ordinal-ignore-case <c>direct</c> → direct.
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Progressive;
        }

        if (raw.Trim().Equals(Direct, StringComparison.OrdinalIgnoreCase))
        {
            return Direct;
        }

        return Progressive;
    }

    /// <summary>Returns whether the normalized mode is direct discovery.</summary>
    public static bool IsDirect(string? raw) =>
        string.Equals(Normalize(raw), Direct, StringComparison.OrdinalIgnoreCase);
}
