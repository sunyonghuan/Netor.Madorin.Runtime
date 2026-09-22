namespace Madorin.AI.Runtime.Contracts;

/// <summary>Meeting 模式用户插话在持久化队列中的状态。</summary>
public enum MeetingPendingUserInputState
{
    Pending,
    Consumed
}

/// <summary>Meeting 模式 FIFO 用户插话记录（SQLite 账本；check_pending 默认 Peek）。</summary>
public sealed record MeetingPendingUserInput(
    string InputId,
    string SessionId,
    int Ordinal,
    string ContentText,
    string ContentHash,
    MeetingPendingUserInputState State,
    DateTimeOffset EnqueuedAt,
    string Source = "user",
    DateTimeOffset? ConsumedAt = null,
    string? ConsumedByRunId = null);
