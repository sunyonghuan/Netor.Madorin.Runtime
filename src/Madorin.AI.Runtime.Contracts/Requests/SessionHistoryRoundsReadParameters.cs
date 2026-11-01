namespace Madorin.AI.Runtime.Contracts;

/// <summary>Reads a bounded page of complete chat rounds for display replay.</summary>
public sealed record SessionHistoryRoundsReadParameters(
    string SessionId,
    int RoundCount = 30,
    long? BeforeSequence = null,
    bool IncludeReasoning = true,
    bool IncludeToolCalls = true,
    string BlobMode = "metadata");
