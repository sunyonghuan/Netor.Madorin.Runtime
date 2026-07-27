using Madorin.AI.Runtime.Client;

namespace Madorin.AI.Runtime.MConsole.HostClient;

public static class HostClientFactory
{
    public static async Task<HostClientSession> ConnectAsync(
        HostConnectionOptions options,
        RuntimeHostCallbacks? hostCallbacks = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var runtimeOptions = new RuntimeClientOptions(
            PipeName: options.PipeName ?? BuildPipeName(options.PipePrefix, options.RuntimeInstanceId),
            HostInstanceId: options.HostInstanceId,
            ExpectedRuntimeInstanceId: options.ExpectedRuntimeInstanceId,
            HandshakeSecret: options.HandshakeSecret,
            ExpectedServerProcessId: options.ExpectedServerProcessId,
            ReconnectWindow: options.ReconnectWindow,
            ReconnectDelay: options.ReconnectDelay,
            RuntimeExecutablePath: Path.GetFullPath(options.RuntimeExecutablePath),
            ExpectedRuntimeVersion: options.ExpectedRuntimeVersion,
            WorkspaceDirectory: Path.GetFullPath(options.WorkspaceDirectory),
            DataDirectory: NormalizeOptionalPath(options.DataDirectory),
            LogDirectory: NormalizeOptionalPath(options.LogDirectory),
            RuntimeInstanceId: options.RuntimeInstanceId,
            PipePrefix: options.PipePrefix,
            StartPolicy: options.StartPolicy,
            StartupTimeout: options.StartupTimeout,
            OwnedProcessClosePolicy: options.OwnedProcessClosePolicy,
            HostCallbacks: hostCallbacks);

        var client = await RuntimeClient.CreateAsync(runtimeOptions, cancellationToken);
        return new HostClientSession(client, options.StartPolicy);
    }

    private static string BuildPipeName(string pipePrefix, string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return $"{pipePrefix}.pending";
        }

        return $"{pipePrefix}.{instanceId[..Math.Min(8, instanceId.Length)]}";
    }

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
}
