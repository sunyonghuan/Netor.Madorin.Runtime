using System.Diagnostics;

using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.MConsole.Configuration;
using Madorin.AI.Runtime.MConsole.HostClient;
using Madorin.AI.Runtime.MConsole.Hosting;

namespace Madorin.AI.Runtime.MConsole.Diagnostics;

public sealed class P1SmokeRunner(MConsoleOptions options, string repositoryRoot)
{
    public async Task<P1SmokeReport> RunAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.DefaultWorkspace))
        {
            throw new InvalidOperationException("P1 smoke requires --workspace or defaultWorkspace.");
        }

        var workspace = MConsolePaths.NormalizePath(options.DefaultWorkspace);
        var executable = await new MadorinLocator(new ProcessRunner()).LocateAsync(
            options.RuntimeExecutablePath,
            repositoryRoot,
            TimeSpan.FromSeconds(options.CommandTimeoutSeconds),
            cancellationToken);
        var prefix = $"madorin.mconsole.p1.{Guid.NewGuid():N}";
        var attachedInstance = $"attach-{Guid.NewGuid():N}";
        var pipeName = BuildPipeName(prefix, attachedInstance);
        var attachedData = Path.Combine(workspace, "runtime-attach-data");

        HostBindingSnapshot attachBinding;
        HostBindingSnapshot startIfMissingAttachBinding;
        await using (var host = new ServeHost(new ServeHostOptions(
            executable.Path,
            workspace,
            attachedInstance,
            prefix,
            attachedData,
            StartupTimeout: TimeSpan.FromSeconds(options.StartupTimeoutSeconds))))
        {
            await host.StartAsync(cancellationToken: cancellationToken);
            var secret = host.RuntimeSecret
                ?? throw new InvalidOperationException("ServeHost secret is unavailable.");

            await using (var attached = await HostClientFactory.ConnectAsync(
                CreateOptions(
                    executable.Path,
                    workspace,
                    RuntimeStartPolicy.AttachExisting,
                    attachedInstance,
                    prefix,
                    attachedData,
                    pipeName,
                    secret),
                cancellationToken: cancellationToken))
            {
                attachBinding = await attached.GetBindingSnapshotAsync(cancellationToken);
            }

            await using (var existing = await HostClientFactory.ConnectAsync(
                CreateOptions(
                    executable.Path,
                    workspace,
                    RuntimeStartPolicy.StartIfMissing,
                    attachedInstance,
                    prefix,
                    attachedData,
                    pipeName,
                    secret),
                cancellationToken: cancellationToken))
            {
                startIfMissingAttachBinding = await existing.GetBindingSnapshotAsync(cancellationToken);
            }

            if (!host.GetSnapshot().IsRunning)
            {
                throw new InvalidOperationException("Attach client unexpectedly stopped non-owned Runtime.");
            }

            await host.StopAsync(cancellationToken);
        }

        HostBindingSnapshot startIfMissingStartBinding;
        int? startIfMissingProcessId;
        var startInstance = $"start-{Guid.NewGuid():N}";
        await using (var started = await HostClientFactory.ConnectAsync(
            CreateOptions(
                executable.Path,
                workspace,
                RuntimeStartPolicy.StartIfMissing,
                startInstance,
                prefix,
                Path.Combine(workspace, "runtime-start-if-missing-data")),
            cancellationToken: cancellationToken))
        {
            startIfMissingStartBinding = await started.GetBindingSnapshotAsync(cancellationToken);
            startIfMissingProcessId = startIfMissingStartBinding.ServerProcessId;
        }

        HostBindingSnapshot alwaysStartBinding;
        int? alwaysStartProcessId;
        await using (var started = await HostClientFactory.ConnectAsync(
            CreateOptions(
                executable.Path,
                workspace,
                RuntimeStartPolicy.AlwaysStart,
                $"always-{Guid.NewGuid():N}",
                prefix,
                Path.Combine(workspace, "runtime-always-start-data")),
            cancellationToken: cancellationToken))
        {
            alwaysStartBinding = await started.GetBindingSnapshotAsync(cancellationToken);
            alwaysStartProcessId = alwaysStartBinding.ServerProcessId;
        }

        var ownedProcessesStopped = await WaitForStoppedAsync(
            [startIfMissingProcessId, alwaysStartProcessId],
            cancellationToken);
        return new P1SmokeReport(
            attachBinding,
            startIfMissingAttachBinding,
            startIfMissingStartBinding,
            alwaysStartBinding,
            ownedProcessesStopped);
    }

    private static HostConnectionOptions CreateOptions(
        string executable,
        string workspace,
        RuntimeStartPolicy policy,
        string instanceId,
        string prefix,
        string dataDirectory,
        string? pipeName = null,
        string? secret = null) => new(
        $"mconsole-p1-{Guid.NewGuid():N}",
        workspace,
        executable,
        policy,
        OwnedProcessClosePolicy.Shutdown,
        instanceId,
        prefix,
        pipeName,
        dataDirectory,
        HandshakeSecret: secret,
        ExpectedRuntimeInstanceId: instanceId,
        StartupTimeout: TimeSpan.FromSeconds(30),
        ReconnectWindow: TimeSpan.FromSeconds(15),
        ReconnectDelay: TimeSpan.FromMilliseconds(100));

    private static string BuildPipeName(string prefix, string instanceId) =>
        $"{prefix}.{instanceId[..Math.Min(8, instanceId.Length)]}";

    private static async Task<bool> WaitForStoppedAsync(
        IEnumerable<int?> processIds,
        CancellationToken cancellationToken)
    {
        var ids = processIds.Where(static id => id is > 0).Select(static id => id!.Value).ToArray();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (ids.All(static id => !IsProcessRunning(id)))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        return ids.All(static id => !IsProcessRunning(id));
    }

    private static bool IsProcessRunning(int processId)
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
}
