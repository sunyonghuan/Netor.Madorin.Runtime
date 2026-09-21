namespace Madorin.AI.Runtime.Contracts;

/// <summary>Wire-safe result returned by Runtime Session compaction.</summary>
public sealed record SessionCompactionResultDto(
    string SessionId,
    string Strategy,
    string ProviderId,
    string ModelId,
    int? KeepLastTokens,
    int SourceMessageCount,
    int ProjectedMessageCount,
    int DroppedMessageCount,
    int BeforeEstimatedTokens,
    int? AfterEstimatedTokens,
    string EstimateSource,
    bool DryRun,
    bool CacheHit,
    bool CacheWritten,
    string? CacheMissReason = null,
    string SelectionSource = "session-default",
    bool SummaryGenerated = false);