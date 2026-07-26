namespace Madorin.AI.Runtime.Contracts;

public sealed record SkillsCapabilities(
    bool Enabled = true,
    string[]? SourceKinds = null,
    bool ResourcesSupported = true,
    bool ScriptsSupported = true,
    bool HotRefreshSupported = true);
