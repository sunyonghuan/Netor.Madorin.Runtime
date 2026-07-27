using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.MConsole.Monitoring;

public sealed record RuntimeMonitorSnapshot(
    RuntimeInstanceBinding Binding,
    RuntimeStatusResult Status,
    SessionListResult Sessions,
    RunListResult Runs,
    DateTimeOffset CapturedAt);

public sealed class RuntimeMonitorService(IRuntimeClient client, TimeProvider? timeProvider = null)
{
    private readonly IRuntimeClient _client = client;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<RuntimeMonitorSnapshot> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        var statusTask = _client.GetRuntimeStatusAsync(cancellationToken).AsTask();
        var sessionsTask = _client.ListSessionsAsync(
            new SessionListParameters(Limit: 100),
            cancellationToken).AsTask();
        var runsTask = _client.ListRunsAsync(
            new RunListParameters(),
            cancellationToken).AsTask();
        await Task.WhenAll(statusTask, sessionsTask, runsTask);
        return new RuntimeMonitorSnapshot(
            _client.InstanceBinding,
            await statusTask,
            await sessionsTask,
            await runsTask,
            _timeProvider.GetUtcNow());
    }

    public async IAsyncEnumerable<RuntimeMonitorSnapshot> WatchAsync(
        TimeSpan refreshInterval,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        if (refreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(refreshInterval),
                "Refresh interval must be positive.");
        }

        var delay = refreshInterval;
        while (!cancellationToken.IsCancellationRequested)
        {
            RuntimeMonitorSnapshot? snapshot = null;
            try
            {
                snapshot = await CaptureAsync(cancellationToken);
                delay = refreshInterval;
            }
            catch (Exception ex) when (ex is RuntimeClientException
                or IOException
                or InvalidDataException
                or InvalidOperationException
                or TimeoutException)
            {
                delay = TimeSpan.FromMilliseconds(Math.Min(
                    Math.Max(refreshInterval.TotalMilliseconds * 2, 1000),
                    30_000));
            }

            if (snapshot is not null)
            {
                yield return snapshot;
            }

            await Task.Delay(delay, cancellationToken);
        }
    }

    public ValueTask<bool> CancelRunAsync(
        string runId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return _client.CancelRunAsync(runId, reason, cancellationToken);
    }
}
