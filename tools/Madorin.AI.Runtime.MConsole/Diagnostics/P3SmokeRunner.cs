using System.Diagnostics;

using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.MConsole.Configuration;
using Madorin.AI.Runtime.MConsole.HostClient;
using Madorin.AI.Runtime.MConsole.Hosting;
using Madorin.AI.Runtime.MConsole.Recovery;

namespace Madorin.AI.Runtime.MConsole.Diagnostics;

public sealed class P3SmokeRunner(
    MConsoleOptions options,
    MConsolePaths paths,
    string repositoryRoot)
{
    public async Task<P3SmokeReport> RunAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.DefaultWorkspace))
        {
            throw new InvalidOperationException("P3 smoke requires --workspace or defaultWorkspace.");
        }

        var workspace = MConsolePaths.NormalizePath(options.DefaultWorkspace);
        var executable = await new MadorinLocator(new ProcessRunner()).LocateAsync(
            options.RuntimeExecutablePath,
            repositoryRoot,
            TimeSpan.FromSeconds(options.CommandTimeoutSeconds),
            cancellationToken);
        var instanceId = $"p3-{Guid.NewGuid():N}";
        var stateDirectory = Path.Combine(paths.StateDirectory, instanceId);
        Directory.CreateDirectory(stateDirectory);
        int? processId = null;
        ReconnectDrillResult reconnect;
        var persistedRecoveryCount = 0;
        var identityPreserved = false;
        var cursorPreserved = false;
        try
        {
            using var cursorStore = new RuntimeCursorStore(stateDirectory);
            using var recoveryStore = new HostRecoveryStateStore(stateDirectory);
            await using (var session = await HostClientFactory.ConnectAsync(
                new HostConnectionOptions(
                    $"mconsole-p3-{Guid.NewGuid():N}",
                    workspace,
                    executable.Path,
                    RuntimeStartPolicy.AlwaysStart,
                    OwnedProcessClosePolicy.Shutdown,
                    instanceId,
                    $"madorin.mconsole.p3.{Guid.NewGuid():N}",
                    DataDirectory: Path.Combine(workspace, "runtime-p3-data"),
                    ExpectedRuntimeInstanceId: instanceId,
                    StartupTimeout: TimeSpan.FromSeconds(options.StartupTimeoutSeconds),
                    ReconnectWindow: TimeSpan.FromSeconds(15),
                    ReconnectDelay: TimeSpan.FromMilliseconds(100)),
                cancellationToken: cancellationToken))
            {
                var before = session.Client.InstanceBinding;
                processId = before.RuntimeProcessId;
                var runtimeId = before.RuntimeInstanceId
                    ?? throw new InvalidDataException("P3 smoke Runtime binding has no instance ID.");
                var status = await session.Client.GetRuntimeStatusAsync(cancellationToken);
                await recoveryStore.SaveAsync(
                    new HostRecoveryState(
                        runtimeId,
                        before.HostInstanceId,
                        before.Workspace ?? workspace,
                        before.RuntimeProcessId,
                        status.ProtocolVersion,
                        0,
                        ActiveRunId: null,
                        ActiveSessionId: null,
                        DateTimeOffset.UtcNow),
                    cancellationToken);

                reconnect = await new ReconnectDrill(session.Client, cursorStore)
                    .RunAsync(runtimeId, cancellationToken);
                var after = session.Client.InstanceBinding;
                identityPreserved = string.Equals(
                    before.RuntimeInstanceId,
                    after.RuntimeInstanceId,
                    StringComparison.Ordinal);
                cursorPreserved = reconnect.LastConfirmedGsn == 0;
                persistedRecoveryCount = (await recoveryStore.ListAsync(cancellationToken)).Count;
            }

            var stopped = await WaitForStoppedAsync(processId, cancellationToken);
            return new P3SmokeReport(
                reconnect,
                persistedRecoveryCount,
                identityPreserved,
                cursorPreserved,
                stopped);
        }
        finally
        {
            DeleteOwnedStateDirectory(paths.StateDirectory, stateDirectory);
        }
    }

    private static async Task<bool> WaitForStoppedAsync(
        int? processId,
        CancellationToken cancellationToken)
    {
        if (processId is not > 0)
        {
            return false;
        }

        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (!IsRunning(processId.Value))
            {
                return true;
            }

            await Task.Delay(100, cancellationToken);
        }

        return !IsRunning(processId.Value);
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void DeleteOwnedStateDirectory(string stateRoot, string path)
    {
        var normalizedRoot = Path.GetFullPath(stateRoot)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullPath).StartsWith("p3-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to remove an unowned P3 state directory.");
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }
}
