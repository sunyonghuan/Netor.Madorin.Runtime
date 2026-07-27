using System.Collections.Concurrent;
using System.Text.Json;

using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;

namespace Madorin.AI.Runtime.MConsole.HostClient;

public sealed class RunEventProjector : IHostEventProjector
{
    private readonly ConcurrentDictionary<(string RuntimeInstanceId, string RunId), RunProjection>
        _runs = new();
    private readonly ConcurrentQueue<HostEventProjection> _events = new();

    public IReadOnlyList<HostEventProjection> Events => _events.ToArray();

    public IReadOnlyList<RunProjection> Runs => _runs.Values
        .OrderBy(static run => run.RuntimeInstanceId, StringComparer.Ordinal)
        .ThenBy(static run => run.RunId, StringComparer.Ordinal)
        .ToArray();

    public ValueTask ProjectAsync(
        RuntimeEventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Enqueue(new HostEventProjection(
            envelope.RuntimeInstanceId,
            envelope.RunId,
            envelope.Gsn,
            envelope.RunSequence,
            envelope.MessageType,
            envelope.Timestamp,
            SummarizePayload(envelope.Payload)));

        var key = (envelope.RuntimeInstanceId, envelope.RunId);
        _runs.AddOrUpdate(
            key,
            _ => CreateProjection(envelope),
            (_, current) => UpdateProjection(current, envelope));
        return ValueTask.CompletedTask;
    }

    public bool TryGetRun(
        string runtimeInstanceId,
        string runId,
        out RunProjection? projection) =>
        _runs.TryGetValue((runtimeInstanceId, runId), out projection);

    private static RunProjection CreateProjection(RuntimeEventEnvelope envelope) =>
        UpdateProjection(
            new RunProjection(
                envelope.RuntimeInstanceId,
                envelope.RunId,
                null,
                null,
                null,
                0,
                null,
                envelope.Timestamp,
                envelope.RunSequence,
                null,
                null),
            envelope);

    private static RunProjection UpdateProjection(
        RunProjection current,
        RuntimeEventEnvelope envelope)
    {
        if (envelope.RunSequence <= current.LastRunSequence
            && current.LastRunSequence != envelope.RunSequence)
        {
            throw new InvalidDataException(
                $"Run '{envelope.RunId}' sequence moved backwards from " +
                $"{current.LastRunSequence} to {envelope.RunSequence}.");
        }

        if (envelope.MessageType == MessageTypes.RunStatusChanged)
        {
            var status = JsonSerializer.Deserialize(
                    envelope.Payload,
                    RuntimeJsonContext.Default.RunStatusChangedEvent)
                ?? throw new InvalidDataException("Run status event payload is empty.");
            var completedUnits = status.CompletedUnits ?? 0;
            if (completedUnits < 0
                || status.TotalUnits is { } totalUnits && completedUnits > totalUnits)
            {
                throw new InvalidDataException(
                    $"Run '{envelope.RunId}' reported invalid progress counters.");
            }

            return current with
            {
                Mode = status.Mode,
                Phase = status.Phase,
                CurrentStep = status.CurrentStep,
                CompletedUnits = completedUnits,
                TotalUnits = status.TotalUnits,
                UpdatedAt = status.UpdatedAt ?? envelope.Timestamp,
                LastRunSequence = envelope.RunSequence,
                Error = status.Error?.Message,
            };
        }

        var terminal = envelope.MessageType is MessageTypes.RunCompleted
            or MessageTypes.RunFailed
            or MessageTypes.RunCancelled;
        return current with
        {
            UpdatedAt = envelope.Timestamp,
            LastRunSequence = envelope.RunSequence,
            TerminalMessageType = terminal ? envelope.MessageType : current.TerminalMessageType,
        };
    }

    private static string SummarizePayload(JsonElement payload)
    {
        var raw = payload.GetRawText();
        return raw.Length <= 500 ? raw : raw[..500];
    }
}
