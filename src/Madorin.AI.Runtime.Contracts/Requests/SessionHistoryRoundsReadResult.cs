namespace Madorin.AI.Runtime.Contracts;

/// <summary>One display page of history records grouped by user-anchored rounds.</summary>
public sealed record SessionHistoryRoundsReadResult(
    SessionHistoryMessage[] Messages,
    long? OldestUserSequence,
    bool HasOlder);
