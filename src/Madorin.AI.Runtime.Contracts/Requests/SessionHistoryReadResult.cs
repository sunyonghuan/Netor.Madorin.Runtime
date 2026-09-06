namespace Madorin.AI.Runtime.Contracts;

public sealed record SessionHistoryMessage(
    string MessageId,
    long Sequence,
    string InvocationId,
    string AgentId,
    string Role,
    DateTimeOffset Timestamp,
    ContentBlock[] Content);

public sealed record SessionHistoryReadResult(
    SessionHistoryMessage[] Messages,
    long? NextCursor);
