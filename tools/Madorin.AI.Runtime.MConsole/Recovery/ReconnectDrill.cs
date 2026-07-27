using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.MConsole.HostClient;

namespace Madorin.AI.Runtime.MConsole.Recovery;

public sealed class ReconnectDrill(
    RuntimeClient client,
    RuntimeCursorStore cursorStore,
    TimeProvider? timeProvider = null)
{
    private readonly RuntimeClient _client = client;
    private readonly RuntimeCursorStore _cursorStore = cursorStore;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ReconnectDrillResult> RunAsync(
        string expectedRuntimeInstanceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRuntimeInstanceId);
        var timeline = new List<RecoveryTimelineEntry>();
        var beforeBinding = _client.InstanceBinding;
        ValidateRuntime(beforeBinding.RuntimeInstanceId, expectedRuntimeInstanceId);
        var beforeStatus = await _client.GetRuntimeStatusAsync(cancellationToken);
        var cursor = await _cursorStore.GetAsync(expectedRuntimeInstanceId, cancellationToken);
        timeline.Add(CreateEntry(
            "captured",
            $"pid={beforeBinding.RuntimeProcessId}; protocol={beforeStatus.ProtocolVersion}; " +
            $"gsn={cursor.LastConfirmedGsn}"));

        timeline.Add(CreateEntry("reconnecting", "RuntimeClient.ReconnectAsync started."));
        await _client.ReconnectAsync(cancellationToken);
        var afterBinding = _client.InstanceBinding;
        ValidateRuntime(afterBinding.RuntimeInstanceId, expectedRuntimeInstanceId);
        if (!string.Equals(
            beforeBinding.HostInstanceId,
            afterBinding.HostInstanceId,
            StringComparison.Ordinal))
        {
            throw new InvalidDataException("Reconnect changed the Host instance binding.");
        }

        var afterStatus = await _client.GetRuntimeStatusAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(afterStatus.ProtocolVersion))
        {
            throw new InvalidDataException("Reconnect returned an empty protocol version.");
        }

        var unchangedCursor = await _cursorStore.GetAsync(
            expectedRuntimeInstanceId,
            cancellationToken);
        if (unchangedCursor.LastConfirmedGsn != cursor.LastConfirmedGsn)
        {
            throw new InvalidDataException("Reconnect changed the persisted event cursor.");
        }

        timeline.Add(CreateEntry(
            "connected",
            $"pid={afterBinding.RuntimeProcessId}; protocol={afterStatus.ProtocolVersion}; " +
            "capabilities=unavailable-in-current-sdk"));
        return new ReconnectDrillResult(
            expectedRuntimeInstanceId,
            beforeBinding.RuntimeProcessId,
            afterBinding.RuntimeProcessId,
            beforeStatus.ProtocolVersion,
            afterStatus.ProtocolVersion,
            unchangedCursor.LastConfirmedGsn,
            timeline);
    }

    private RecoveryTimelineEntry CreateEntry(string step, string detail) =>
        new(step, _timeProvider.GetUtcNow(), detail);

    private static void ValidateRuntime(string? actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Connected Runtime '{actual}' does not match expected '{expected}'.");
        }
    }
}
