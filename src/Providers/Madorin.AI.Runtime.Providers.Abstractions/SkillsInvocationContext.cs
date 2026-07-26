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
    [property: JsonIgnore] string? PlanVersion = null);
