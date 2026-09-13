namespace Madorin.AI.Runtime.Entities;

public sealed record InvocationSnapshot(
    string InvocationId,
    string AgentId,
    string ProviderId,
    string ModelId,
    string PromptHash,
    string? GlobalMemoryHash,
    string? ProjectMemoryHash,
    string ToolCatalogVersion,
    string ProjectionVersion,
    DateTimeOffset StartedAt,
    string? ParentInvocationId = null,
    string? WorkStepId = null,
    string? WorkPlanVersion = null,
    string? Title = null,
    string? ParentStepId = null,
    int Depth = 0,
    string? DisplayName = null,
    string? Role = null,
    string? ParticipantId = null,
    int? RoundIndex = null,
    ContextProjectionSnapshot? ContextProjection = null,
    string? SkillCatalogVersion = null,
    string[]? SkillNames = null,
    string[]? SkillContentHashes = null,
    string[]? SkillSourceIds = null,
    string[]? SkillMaterializedPaths = null);

/// <summary>Immutable context-projection decision captured for one Invocation.</summary>
public sealed record ContextProjectionSnapshot(
    string Strategy,
    string[] RetainedItems,
    int DroppedMessageCount,
    int IncludedMessageCount,
    int EstimatedTokens,
    string EstimateSource,
    int TokenLimit,
    int SummarizedUnitCount = 0);
