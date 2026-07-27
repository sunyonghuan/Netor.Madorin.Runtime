using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.MConsole.HostClient;

namespace Madorin.AI.Runtime.MConsole.Recovery;

public sealed class RecoveryEventConsumer(
    IRuntimeClient client,
    RuntimeCursorStore cursorStore,
    IHostEventProjector projector)
{
    public async Task<RecoveryEventResult> RecoverUntilRunTerminalAsync(
        string runtimeInstanceId,
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var initial = await cursorStore.GetAsync(runtimeInstanceId, cancellationToken);
        var lastConfirmed = initial.LastConfirmedGsn;
        var seen = new HashSet<long>();
        var projected = 0;
        var duplicates = 0;
        string? terminal = null;

        await foreach (var envelope in client.ReadEventsAsync(
            initial.LastConfirmedGsn,
            cancellationToken))
        {
            ValidateRuntime(envelope, runtimeInstanceId);
            if (envelope.Gsn <= lastConfirmed || !seen.Add(envelope.Gsn))
            {
                duplicates++;
                continue;
            }

            await projector.ProjectAsync(envelope, cancellationToken);
            await client.AcknowledgeEventsAsync(envelope.Gsn, cancellationToken);
            var cursor = await cursorStore.AdvanceAsync(
                runtimeInstanceId,
                envelope.Gsn,
                cancellationToken);
            lastConfirmed = cursor.LastConfirmedGsn;
            projected++;

            if (string.Equals(envelope.RunId, runId, StringComparison.Ordinal)
                && IsTerminal(envelope.MessageType))
            {
                terminal = envelope.MessageType;
                break;
            }
        }

        return new RecoveryEventResult(
            runtimeInstanceId,
            initial.LastConfirmedGsn,
            lastConfirmed,
            projected,
            duplicates,
            terminal);
    }

    public async Task DemonstrateEarlyAcknowledgementLossAsync(
        string runtimeInstanceId,
        CancellationToken cancellationToken = default)
    {
        var cursor = await cursorStore.GetAsync(runtimeInstanceId, cancellationToken);
        await foreach (var envelope in client.ReadEventsAsync(
            cursor.LastConfirmedGsn,
            cancellationToken))
        {
            ValidateRuntime(envelope, runtimeInstanceId);
            await client.AcknowledgeEventsAsync(envelope.Gsn, cancellationToken);
            throw new EarlyAcknowledgementDemonstratedException(
                runtimeInstanceId,
                envelope.Gsn,
                cursor.LastConfirmedGsn);
        }

        throw new InvalidOperationException("No event was available for the negative drill.");
    }

    private static void ValidateRuntime(
        RuntimeEventEnvelope envelope,
        string runtimeInstanceId)
    {
        if (!string.Equals(
            envelope.RuntimeInstanceId,
            runtimeInstanceId,
            StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Event belongs to Runtime '{envelope.RuntimeInstanceId}', " +
                $"expected '{runtimeInstanceId}'.");
        }
    }

    private static bool IsTerminal(string messageType) => messageType is
        MessageTypes.RunCompleted or MessageTypes.RunFailed or MessageTypes.RunCancelled;
}

public sealed class EarlyAcknowledgementDemonstratedException(
    string runtimeInstanceId,
    long acknowledgedGsn,
    long persistedGsn) : InvalidOperationException(
        $"Dangerous negative drill acknowledged GSN {acknowledgedGsn} before projection; " +
        $"the isolated local cursor remains {persistedGsn}.")
{
    public string RuntimeInstanceId { get; } = runtimeInstanceId;

    public long AcknowledgedGsn { get; } = acknowledgedGsn;

    public long PersistedGsn { get; } = persistedGsn;
}
