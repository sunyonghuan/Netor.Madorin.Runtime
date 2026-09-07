namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>Bound skill source root used for host-mode lazy path resolution by scope.</summary>
public sealed record SkillScopeSourceRoot(
    string ScopeId,
    string DirectoryPath);
