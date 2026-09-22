namespace Madorin.AI.Runtime.Tools.Abstractions;

/// <summary>Meeting-mode tool backing store used by builtin meeting tools.</summary>
public interface IMeetingModeToolStore
{
    public Task MarkEndConfirmedAsync(
        string sessionId,
        string? finalSummaryMessageId,
        CancellationToken ct = default);

    public Task SaveFinalSummaryMessageIdAsync(
        string sessionId,
        string summaryMessageId,
        CancellationToken ct = default);

    public Task<(bool EndConfirmed, string? FinalSummaryMessageId)> GetEndConfirmationStateAsync(
        string sessionId,
        CancellationToken ct = default);

    public Task<IReadOnlyList<MeetingPendingInputPeek>> PeekPendingInputsAsync(
        string sessionId,
        CancellationToken ct = default);

    public Task EnqueuePendingInputAsync(
        string sessionId,
        string requestId,
        string contentJson,
        string? runId,
        CancellationToken ct = default);
}

/// <summary>Lightweight pending-input peek for Host tools.</summary>
public sealed record MeetingPendingInputPeek(
    string RequestId,
    string ContentJson,
    DateTimeOffset EnqueuedAt);
