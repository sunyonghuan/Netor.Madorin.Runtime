namespace Madorin.AI.Runtime.Contracts;

public sealed record SessionHistoryReadParameters(
    string SessionId,
    long? Cursor = null,
    int PageSize = 100,
    bool IncludeReasoning = true,
    bool IncludeToolCalls = true,
    string BlobMode = "metadata");
