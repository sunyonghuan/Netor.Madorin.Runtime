using System.Text.Json;

namespace Madorin.AI.Runtime.Persistence.Files;

/// <summary>Contains one consistent canonical-history snapshot for projection.</summary>
public sealed record ConversationCompactionSource(
    IReadOnlyList<ConversationRecordV1> Records,
    string HistorySha256,
    IReadOnlyDictionary<long, string>? PrefixHistoryHashes = null);

/// <summary>Stores one provider-independent projected message.</summary>
public sealed record ConversationCompactMessageV1(
    string Role,
    JsonElement Content);

/// <summary>Defines the rebuildable V1 ContextProjection cache.</summary>
public sealed record ConversationCompactCacheV1(
    string Schema,
    string SessionId,
    string SourceHistorySha256,
    string Strategy,
    string ProviderId,
    string ModelId,
    int? KeepLastTokens,
    int SourceMessageCount,
    int DroppedMessageCount,
    int BeforeEstimatedTokens,
    int AfterEstimatedTokens,
    string EstimateSource,
    ConversationCompactMessageV1[] ProjectionMessages,
    DateTimeOffset CreatedAt,
    string RuntimeMode = "Expert",
    string ContextPolicyHash = "",
    string AgentPromptHash = "",
    string ToolCatalogVersion = "0",
    string ProjectionSchemaVersion = "madorin.context-projection.v1",
    string SummaryModelVersion = "",
    string? CacheMissReason = null,
    long CoveredThroughSequence = 0,
    string PrefixHistorySha256 = "",
    string ProjectionRevision = "",
    string? OperationId = null,
    bool SummaryValidated = false)
{
    public const string CurrentSchema = "madorin.conversation.compact.v3";
}
