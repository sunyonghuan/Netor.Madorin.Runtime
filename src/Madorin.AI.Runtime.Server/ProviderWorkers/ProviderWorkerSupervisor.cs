using System.Collections.Concurrent;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.WorkerHost;

namespace Madorin.AI.Runtime.Server.ProviderWorkers;

public sealed class ProviderWorkerSupervisor : IProviderWorkerSupervisor
{
    private readonly string _directory;
    private readonly string _runtimeVersion;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _workerLog;
    private readonly ProviderWorkerProcessGroup _processGroup = new();
    private readonly ConcurrentDictionary<string, ProviderWorkerProcess> _processes =
        new(StringComparer.Ordinal);

    public ProviderWorkerSupervisor(
        string? directory = null,
        string? runtimeVersion = null,
        TimeProvider? timeProvider = null,
        Action<string>? workerLog = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory)
            ? ProviderWorkerPaths.ResolveDirectory()
            : Path.GetFullPath(directory);
        _runtimeVersion = string.IsNullOrWhiteSpace(runtimeVersion)
            ? "0.1.46"
            : runtimeVersion;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _workerLog = workerLog;
    }

    public IRuntimeProviderAdapter CreateAdapter(string protocol, ProviderConnectionConfig connection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        ArgumentNullException.ThrowIfNull(connection);
        if (!ProviderProtocolSlugs.TryMap(protocol, out var slug))
        {
            throw new InvalidOperationException(
                $"Provider '{connection.ProviderId}' uses unsupported protocol '{protocol}'.");
        }

        var process = _processes.GetOrAdd(
            slug,
            static (key, state) => new ProviderWorkerProcess(
                state.Directory,
                key,
                state.Protocol,
                state.Version,
                state.Group,
                state.Clock,
                state.Log),
            (Directory: _directory, Protocol: protocol, Version: _runtimeVersion,
                Group: _processGroup, Clock: _timeProvider, Log: _workerLog));
        return new ProviderWorkerAdapter(process, connection);
    }

    internal bool TryGetProcess(string slug, out ProviderWorkerProcess process) =>
        _processes.TryGetValue(slug, out process!);

    public async ValueTask DisposeAsync()
    {
        foreach (var process in _processes.Values)
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }

        _processes.Clear();
        _processGroup.Dispose();
    }
}
