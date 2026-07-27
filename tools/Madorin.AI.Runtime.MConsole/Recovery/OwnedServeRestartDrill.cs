using Madorin.AI.Runtime.MConsole.Hosting;

namespace Madorin.AI.Runtime.MConsole.Recovery;

public sealed record OwnedServeRestartResult(
    ServeHost RestartedHost,
    ServeHostSnapshot Stopped,
    ServeHostSnapshot Started) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => RestartedHost.DisposeAsync();
}

public static class OwnedServeRestartDrill
{
    public static async Task<OwnedServeRestartResult> RestartAsync(
        ServeHost ownedHost,
        ServeHostOptions originalOptions,
        Func<CancellationToken, Task<bool>>? readinessProbe = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownedHost);
        ArgumentNullException.ThrowIfNull(originalOptions);
        var before = ownedHost.GetSnapshot();
        if (!before.IsRunning)
        {
            throw new InvalidOperationException("The owned Runtime is not running.");
        }

        var stopped = await ownedHost.StopAsync(cancellationToken);
        await ownedHost.DisposeAsync();
        var restarted = new ServeHost(originalOptions);
        try
        {
            var started = await restarted.StartAsync(readinessProbe, cancellationToken);
            return new OwnedServeRestartResult(restarted, stopped, started);
        }
        catch
        {
            await restarted.DisposeAsync();
            throw;
        }
    }
}
