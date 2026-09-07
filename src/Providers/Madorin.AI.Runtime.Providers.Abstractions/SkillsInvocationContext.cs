using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Providers.Abstractions;

public sealed record RuntimeSkillScriptInvocation(
    string SkillName,
    string Script,
    string CatalogVersion,
    string[] Arguments,
    string RunId,
    string SessionId,
    string AgentId,
    string InvocationId,
    string? ParentAgentId = null,
    string? ParentInvocationId = null,
    string? WorkStepId = null,
    string? PlanVersion = null);

public sealed record RuntimeSkillScriptResult(
    bool Success,
    string OutputJson,
    string? Error = null);

/// <summary>Framework-neutral Skills snapshot passed to a Provider implementation.</summary>
public sealed record SkillsInvocationContext(
    string CatalogVersion,
    string[] SkillNames,
    string[] SkillContentHashes,
    string[] SkillSourceIds,
    string[]? SkillMaterializedPaths = null,
    [property: JsonIgnore]
    Func<RuntimeSkillScriptInvocation, CancellationToken, Task<RuntimeSkillScriptResult>>? ScriptInvoker = null,
    [property: JsonIgnore] string? RunId = null,
    [property: JsonIgnore] string? SessionId = null,
    [property: JsonIgnore] string? AgentId = null,
    [property: JsonIgnore] string? InvocationId = null,
    [property: JsonIgnore] string? ParentAgentId = null,
    [property: JsonIgnore] string? ParentInvocationId = null,
    [property: JsonIgnore] string? WorkStepId = null,
    [property: JsonIgnore] string? PlanVersion = null,
    /// <summary>When true, Advertise uses Loadable only and Provider is always created.</summary>
    [property: JsonIgnore] bool IsHostMode = false,
    /// <summary>Run-scoped activation table (host mode only).</summary>
    [property: JsonIgnore] SkillActivationStore? ActivationStore = null,
    /// <summary>Agent allowlist (SkillIds). null=undeclared/all; []=none; non-empty=names.</summary>
    [property: JsonIgnore] string[]? AllowlistSkillIds = null,
    /// <summary>Allowlisted catalog name → materialized path for host load resolution.</summary>
    [property: JsonIgnore] IReadOnlyDictionary<string, string>? CatalogMaterializedPathsByName = null,
    /// <summary>Allowlisted catalog name → description (used for short Advertise text).</summary>
    [property: JsonIgnore] IReadOnlyDictionary<string, string>? CatalogDescriptionsByName = null,
    /// <summary>Bound source roots (workspace / user-global / host-global) for lazy path resolve.</summary>
    [property: JsonIgnore] IReadOnlyList<SkillScopeSourceRoot>? ScopeSourceRoots = null);
