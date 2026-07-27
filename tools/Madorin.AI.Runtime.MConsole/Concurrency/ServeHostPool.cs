using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.MConsole.HostClient;
using Madorin.AI.Runtime.MConsole.Hosting;

namespace Madorin.AI.Runtime.MConsole.Concurrency;

public sealed record ServeHostDefinition(
    string Id,
    string Workspace,
    string DataDirectory,
    string LogDirectory,
    string InstanceId,
    string PipePrefix,
    int MaxRuns = 1);

public sealed record ServeHostPoolSnapshot(
    string Id,
    string Workspace,
    string DataDirectory,
    string LogDirectory,
    string InstanceId,
    string PipePrefix,
    int MaxRuns,
    ServeHostSnapshot Process,
    string? BoundRuntimeInstanceId);

public sealed class ServeHostPoolStartException(
    string id,
    ServeHostSnapshot? processSnapshot,
    Exception innerException)
    : InvalidOperationException($"Serve instance '{id}' failed to start: {innerException.Message}", innerException)
{
    public string Id { get; } = id;

    public ServeHostSnapshot? ProcessSnapshot { get; } = processSnapshot;
}

public sealed class ServeHostPool(string executablePath) : IAsyncDisposable
{
    private readonly string _executablePath = Path.GetFullPath(executablePath);
    private readonly Dictionary<string, PooledHost> _hosts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _startingIds = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public async Task<ServeHostPoolSnapshot> StartAsync(
        ServeHostDefinition definition,
        CancellationToken cancellationToken = default)
    {
        Validate(definition);
        lock (_sync)
        {
            if (_hosts.ContainsKey(definition.Id) || !_startingIds.Add(definition.Id))
            {
                throw new InvalidOperationException($"Serve instance '{definition.Id}' is already registered.");
            }
        }

        var host = new ServeHost(new ServeHostOptions(
            _executablePath,
            definition.Workspace,
            definition.InstanceId,
            definition.PipePrefix,
            definition.DataDirectory,
            definition.LogDirectory,
            definition.MaxRuns));
        ServeHostSnapshot? processSnapshot = null;
        try
        {
            processSnapshot = await host.StartAsync(cancellationToken: cancellationToken);
            var secret = host.RuntimeSecret
                ?? throw new InvalidOperationException("Started serve did not retain its runtime secret.");
            var session = await HostClientFactory.ConnectAsync(
                new HostConnectionOptions(
                    $"mconsole-pool-{definition.Id}-{Guid.NewGuid():N}",
                    definition.Workspace,
                    _executablePath,
                    RuntimeStartPolicy.AttachExisting,
                    OwnedProcessClosePolicy.KeepAlive,
                    definition.InstanceId,
                    definition.PipePrefix,
                    DataDirectory: definition.DataDirectory,
                    LogDirectory: definition.LogDirectory,
                    HandshakeSecret: secret,
                    ExpectedRuntimeInstanceId: definition.InstanceId,
                    ExpectedServerProcessId: processSnapshot.ProcessId,
                    StartupTimeout: TimeSpan.FromSeconds(15)),
                cancellationToken: cancellationToken);
            var pooled = new PooledHost(
                definition,
                host,
                session,
                session.Client.InstanceBinding.RuntimeInstanceId);
            lock (_sync)
            {
                _hosts.Add(definition.Id, pooled);
            }

            return CreateSnapshot(pooled);
        }
        catch (Exception ex)
        {
            try
            {
                processSnapshot = host.GetSnapshot();
            }
            catch (InvalidOperationException)
            {
            }

            await host.DisposeAsync();
            if (ex is OperationCanceledException)
            {
                throw;
            }

            throw new ServeHostPoolStartException(definition.Id, processSnapshot, ex);
        }
        finally
        {
            lock (_sync)
            {
                _startingIds.Remove(definition.Id);
            }
        }
    }

    public RuntimeClient GetClient(string id)
    {
        lock (_sync)
        {
            return GetHost(id).Session.Client;
        }
    }

    public ServeHostPoolSnapshot GetSnapshot(string id)
    {
        lock (_sync)
        {
            return CreateSnapshot(GetHost(id));
        }
    }

    public IReadOnlyList<ServeHostPoolSnapshot> GetSnapshots()
    {
        lock (_sync)
        {
            return _hosts.Values.Select(static item => CreateSnapshot(item))
                .OrderBy(static item => item.Id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public async Task<ServeHostPoolSnapshot> StopAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        PooledHost pooled;
        lock (_sync)
        {
            pooled = GetHost(id);
            _hosts.Remove(id);
        }

        await DisposeSessionAsync(pooled.Session);
        var process = await pooled.Host.StopAsync(cancellationToken);
        await pooled.Host.DisposeAsync();
        return CreateSnapshot(pooled, process);
    }

    public async ValueTask DisposeAsync()
    {
        PooledHost[] hosts;
        lock (_sync)
        {
            hosts = _hosts.Values.Reverse().ToArray();
            _hosts.Clear();
        }

        foreach (var pooled in hosts)
        {
            await DisposeSessionAsync(pooled.Session);
            await pooled.Host.DisposeAsync();
        }
    }

    private static async Task DisposeSessionAsync(HostClientSession session)
    {
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception ex) when (ex is RuntimeClientException
            or IOException
            or InvalidOperationException)
        {
            // The Runtime may close the SDK channel before pool convergence.
        }
    }

    private PooledHost GetHost(string id) => _hosts.TryGetValue(id, out var host)
        ? host
        : throw new KeyNotFoundException($"Serve instance '{id}' is not registered.");

    private static ServeHostPoolSnapshot CreateSnapshot(
        PooledHost pooled,
        ServeHostSnapshot? process = null) => new(
        pooled.Definition.Id,
        pooled.Definition.Workspace,
        pooled.Definition.DataDirectory,
        pooled.Definition.LogDirectory,
        pooled.Definition.InstanceId,
        pooled.Definition.PipePrefix,
        pooled.Definition.MaxRuns,
        process ?? pooled.Host.GetSnapshot(),
        pooled.BoundRuntimeInstanceId);

    private static void Validate(ServeHostDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.InstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.PipePrefix);
        if (definition.MaxRuns <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(definition), "MaxRuns must be positive.");
        }
    }

    private sealed record PooledHost(
        ServeHostDefinition Definition,
        ServeHost Host,
        HostClientSession Session,
        string? BoundRuntimeInstanceId);
}
