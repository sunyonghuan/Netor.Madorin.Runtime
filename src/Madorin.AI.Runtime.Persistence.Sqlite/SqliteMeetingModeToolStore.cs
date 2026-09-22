using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Persistence.Sqlite;

/// <summary>Adapts <see cref="SqliteMeetingRepository"/> for Meeting builtin tools.</summary>
public sealed class SqliteMeetingModeToolStore(SqliteMeetingRepository meetingRepository) : IMeetingModeToolStore
{
    private readonly SqliteMeetingRepository _meetingRepository = meetingRepository
        ?? throw new ArgumentNullException(nameof(meetingRepository));

    public Task MarkEndConfirmedAsync(
        string sessionId,
        string? finalSummaryMessageId,
        CancellationToken ct = default) =>
        _meetingRepository.MarkEndConfirmedAsync(sessionId, finalSummaryMessageId, ct);

    public Task SaveFinalSummaryMessageIdAsync(
        string sessionId,
        string summaryMessageId,
        CancellationToken ct = default) =>
        _meetingRepository.SaveFinalSummaryMessageIdAsync(sessionId, summaryMessageId, ct);

    public async Task<(bool EndConfirmed, string? FinalSummaryMessageId)> GetEndConfirmationStateAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        var snapshot = await _meetingRepository.GetMeetingSnapshotAsync(sessionId, ct)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Meeting session '{sessionId}' not found.");
        return (snapshot.Session.EndConfirmed, snapshot.Session.FinalSummaryMessageId);
    }

    public async Task<IReadOnlyList<MeetingPendingInputPeek>> PeekPendingInputsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        var rows = await _meetingRepository.ListPendingInputsAsync(sessionId, ct).ConfigureAwait(false);
        return rows
            .Select(row => new MeetingPendingInputPeek(row.RequestId, row.ContentJson, row.EnqueuedAt))
            .ToArray();
    }

    public Task EnqueuePendingInputAsync(
        string sessionId,
        string requestId,
        string contentJson,
        string? runId,
        CancellationToken ct = default) =>
        _meetingRepository.EnqueuePendingInputAsync(sessionId, requestId, contentJson, runId, ct);
}
