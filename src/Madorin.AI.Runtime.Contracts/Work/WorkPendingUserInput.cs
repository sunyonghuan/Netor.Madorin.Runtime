namespace Madorin.AI.Runtime.Contracts;

/// <summary>Work 模式用户插话在持久化队列中的状态。</summary>
public enum WorkPendingUserInputState
{
    Pending,
    Consumed
}

/// <summary>Work 模式 FIFO 用户插话记录（SQLite 账本，非完整 Run Control）。</summary>
public sealed record WorkPendingUserInput(
    string InputId,
    string SessionId,
    int Ordinal,
    string ContentText,
    string ContentHash,
    WorkPendingUserInputState State,
    DateTimeOffset EnqueuedAt,
    string Source = "user",
    DateTimeOffset? ConsumedAt = null,
    string? ConsumedByRunId = null);
