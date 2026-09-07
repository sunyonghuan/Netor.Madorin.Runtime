namespace Madorin.AI.Runtime.Contracts;

public sealed record SkillsCapabilities(
    bool Enabled = true,
    string[]? SourceKinds = null,
    bool ResourcesSupported = true,
    bool ScriptsSupported = true,
    bool HotRefreshSupported = true,
    /// <summary>
    /// When true, this Runtime accepts <c>AgentRef.skillDiscoveryMode=direct</c>.
    /// Hosts must not disable progressive skill discovery until this flag is true.
    /// </summary>
    bool DiscoveryDirect = false);
