namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>
/// Host-mode skill discovery strategy constants and parsing
/// (<c>AgentRef.skillDiscoveryMode</c> / <c>SkillsInvocationContext.SkillDiscoveryMode</c>).
/// </summary>
public static class SkillDiscoveryModes
{
    public const string Progressive = "progressive";
    public const string Direct = "direct";

    /// <summary>
    /// Process-level support bit aligned with <c>SkillsCapabilities.DiscoveryDirect</c>.
    /// Direct mode is only honored when this is true (same change-set as initialize).
    /// </summary>
    public static bool DiscoveryDirectSupported { get; set; } = true;

    /// <summary>
    /// Normalizes raw agent material: null/empty/unknown → progressive;
    /// ordinal-ignore-case <c>direct</c> → direct when supported, otherwise progressive.
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Progressive;
        }

        if (raw.Trim().Equals(Direct, StringComparison.OrdinalIgnoreCase))
        {
            return DiscoveryDirectSupported ? Direct : Progressive;
        }

        return Progressive;
    }

    /// <summary>Returns whether the normalized mode is direct discovery.</summary>
    public static bool IsDirect(string? raw) =>
        string.Equals(Normalize(raw), Direct, StringComparison.OrdinalIgnoreCase);
}
