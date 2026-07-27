using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.MConsole.HostClient;

public sealed class EventStreamConsumer(
    IRuntimeClient client,
    RuntimeCursorStore cursorStore,
    IHostEventProjector projector)
{
    public async Task<EventConsumptionResult> ConsumeUntilRunTerminalAsync(
        string runtimeInstanceId,
        string runId,
        EventConsumptionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        options ??= new EventConsumptionOptions();

        var initial = await cursorStore.GetAsync(runtimeInstanceId, cancellationToken);
        var lastConfirmedGsn = initial.LastConfirmedGsn;
        var processed = 0;
        string? terminalMessageType = null;

        await foreach (var envelope in client.ReadEventsAsync(
            lastConfirmedGsn,
            cancellationToken))
        {
            ValidateEnvelope(envelope, runtimeInstanceId, lastConfirmedGsn);

            if (options.EnableEarlyAcknowledgementDangerous)
            {
                await client.AcknowledgeEventsAsync(envelope.Gsn, cancellationToken);
            }

            await projector.ProjectAsync(envelope, cancellationToken);
            if (options.ConfirmationDelay > TimeSpan.Zero)
            {
                await Task.Delay(options.ConfirmationDelay, cancellationToken);
            }

            if (!options.EnableEarlyAcknowledgementDangerous)
            {
                await client.AcknowledgeEventsAsync(envelope.Gsn, cancellationToken);
            }

            var cursor = await cursorStore.AdvanceAsync(
                runtimeInstanceId,
                envelope.Gsn,
                cancellationToken);
            lastConfirmedGsn = cursor.LastConfirmedGsn;
            processed++;

            if (string.Equals(envelope.RunId, runId, StringComparison.Ordinal)
                && IsTerminal(envelope.MessageType))
            {
                terminalMessageType = envelope.MessageType;
                break;
            }
        }

        return new EventConsumptionResult(
            runtimeInstanceId,
            initial.LastConfirmedGsn,
            lastConfirmedGsn,
            processed,
            terminalMessageType);
    }

    private static void ValidateEnvelope(
        RuntimeEventEnvelope envelope,
        string runtimeInstanceId,
        long lastConfirmedGsn)
    {
        if (!string.Equals(
                envelope.RuntimeInstanceId,
                runtimeInstanceId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Event belongs to Runtime '{envelope.RuntimeInstanceId}', expected '{runtimeInstanceId}'.");
        }

        if (envelope.Gsn <= lastConfirmedGsn)
        {
            throw new InvalidDataException(
                $"Runtime '{runtimeInstanceId}' emitted non-monotonic GSN {envelope.Gsn}.");
        }
    }

    private static bool IsTerminal(string messageType) => messageType is
        MessageTypes.RunCompleted or MessageTypes.RunFailed or MessageTypes.RunCancelled;
}
