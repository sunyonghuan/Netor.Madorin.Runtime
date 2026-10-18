namespace Madorin.AI.Runtime.Contracts;

/// <summary>Metadata for the effective history projection and a recent compaction operation.</summary>
public sealed record SessionContextStatusDto(
    string SessionId,
    string? OperationId,
    string OperationState,
    string? ProjectionRevision,
    long? CoveredThroughSequence,
    int? HistoryEstimatedTokens,
    int? EffectiveContextEstimatedTokens,
    int? ContextWindow,
    string EstimateSource,
    bool IsStale,
    string? ReasonCode,
    DateTimeOffset UpdatedAt,
    SessionCompactionResultDto? Result = null,
    string? RequestFingerprint = null);
