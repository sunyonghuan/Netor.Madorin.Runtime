using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.MConsole.HostClient;

public sealed class RunConductor(IRuntimeClient client, string runtimeInstanceId)
{
    public async Task<RunSubmission> StartNewSessionAsync(
        RunRequestDraft draft,
        CancellationToken cancellationToken = default)
    {
        var request = RunRequestFactory.CreateNewSessionRequest(draft);
        var runId = await client.StartNewSessionRunAsync(request, cancellationToken);
        return new RunSubmission(
            runId,
            null,
            draft.Mode,
            runtimeInstanceId,
            IsExistingSession: false,
            DateTimeOffset.UtcNow);
    }

    public async Task<RunSubmission> StartExistingSessionAsync(
        string sessionId,
        RunRequestDraft draft,
        int? expectedSelectionVersion = null,
        CancellationToken cancellationToken = default)
    {
        var request = RunRequestFactory.CreateExistingSessionRequest(
            sessionId,
            draft,
            expectedSelectionVersion);
        var runId = await client.StartExistingSessionRunAsync(request, cancellationToken);
        return new RunSubmission(
            runId,
            sessionId,
            draft.Mode,
            runtimeInstanceId,
            IsExistingSession: true,
            DateTimeOffset.UtcNow);
    }

    public async Task<RunCancellationOutcome> CancelAsync(
        string runId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var accepted = await client.CancelRunAsync(runId, reason, cancellationToken);
        if (!accepted)
        {
            return new RunCancellationOutcome(runId, false, null, false);
        }

        var current = await client.QueryRunAsync(runId, cancellationToken);
        return new RunCancellationOutcome(
            runId,
            true,
            current.Status,
            IsTerminal(current.Status));
    }

    public ValueTask<RunQueryResult> QueryAsync(
        string runId,
        CancellationToken cancellationToken = default) =>
        client.QueryRunAsync(runId, cancellationToken);

    public ValueTask<RunListResult> ListAsync(
        string? sessionId = null,
        CancellationToken cancellationToken = default) =>
        client.ListRunsAsync(new RunListParameters(sessionId), cancellationToken);

    private static bool IsTerminal(RunStatus status) => status is
        RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled or RunStatus.Interrupted;
}
