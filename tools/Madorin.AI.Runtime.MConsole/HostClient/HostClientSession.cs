using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.MConsole.HostClient;

public sealed class HostClientSession : IAsyncDisposable
{
    private readonly List<ClientStateSnapshot> _states = [];
    private readonly object _stateSync = new();
    private readonly RuntimeStartPolicy _requestedPolicy;
    private int _disposed;

    internal HostClientSession(RuntimeClient client, RuntimeStartPolicy requestedPolicy)
    {
        Client = client;
        _requestedPolicy = requestedPolicy;
        RecordState("Connected and initialized.");
    }

    public RuntimeClient Client { get; }

    public IReadOnlyList<ClientStateSnapshot> StateTimeline
    {
        get
        {
            lock (_stateSync)
            {
                return _states.ToArray();
            }
        }
    }

    public async Task<HostBindingSnapshot> GetBindingSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var binding = Client.InstanceBinding;
        RuntimeStatusResult? status = null;
        try
        {
            status = await Client.GetRuntimeStatusAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is RuntimeClientException
            or IOException
            or InvalidOperationException
            or TimeoutException)
        {
            RecordState($"Runtime status unavailable: {ex.Message}");
        }

        return new HostBindingSnapshot(
            binding.HostInstanceId,
            binding.RuntimeInstanceId,
            binding.Workspace,
            binding.RuntimeProcessId,
            status?.ProtocolVersion,
            status?.ProgramVersion,
            Capabilities: null,
            binding.IsProcessOwned,
            ResolveActualBranch(binding.IsProcessOwned));
    }

    public async Task ObserveCompletionAsync(
        Func<long> lastConfirmedGsn,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await Client.Completion.WaitAsync(cancellationToken);
            RecordState("Client completion ended normally.", lastConfirmedGsn());
        }
        catch (RuntimeProcessExitedException ex)
        {
            RecordState(
                "Owned Runtime process exited unexpectedly.",
                ex.LastConfirmedGsn,
                ex.ProcessId,
                ex.ExitCode,
                ex.ExitedAt);
        }
        catch (Exception ex) when (ex is RuntimeClientException
            or IOException
            or InvalidDataException
            or TimeoutException)
        {
            RecordState($"Client completion faulted: {ex.Message}", lastConfirmedGsn());
        }
    }

    public void RecordState(string reason, long lastConfirmedGsn = 0) =>
        RecordState(reason, lastConfirmedGsn, null, null, null);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await Client.DisposeAsync();
        RecordState("Client disposed.");
    }

    private void RecordState(
        string reason,
        long lastConfirmedGsn,
        int? processId,
        int? exitCode,
        DateTimeOffset? exitedAt)
    {
        lock (_stateSync)
        {
            _states.Add(new ClientStateSnapshot(
                Client.State,
                DateTimeOffset.UtcNow,
                reason,
                processId,
                exitCode,
                exitedAt,
                lastConfirmedGsn));
        }
    }

    private string ResolveActualBranch(bool ownsProcess) => _requestedPolicy switch
    {
        RuntimeStartPolicy.AttachExisting => "attach",
        RuntimeStartPolicy.AlwaysStart => "start",
        RuntimeStartPolicy.StartIfMissing => ownsProcess ? "start" : "attach",
        _ => "unknown",
    };
}
