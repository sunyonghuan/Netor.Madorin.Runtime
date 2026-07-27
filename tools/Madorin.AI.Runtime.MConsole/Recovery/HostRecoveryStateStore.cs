using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Madorin.AI.Runtime.MConsole.Serialization;

namespace Madorin.AI.Runtime.MConsole.Recovery;

public sealed class HostRecoveryStateStore(string stateDirectory) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _stateDirectory = Path.GetFullPath(stateDirectory);

    public async Task SaveAsync(
        HostRecoveryState state,
        CancellationToken cancellationToken = default)
    {
        Validate(state);
        Directory.CreateDirectory(_stateDirectory);
        var path = GetPath(state.RuntimeInstanceId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(path))
            {
                var current = await ReadAsync(path, cancellationToken);
                if (state.LastConfirmedGsn < current.LastConfirmedGsn)
                {
                    throw new InvalidOperationException(
                        $"Recovery GSN cannot move backwards from {current.LastConfirmedGsn} " +
                        $"to {state.LastConfirmedGsn}.");
                }
            }

            await WriteAtomicAsync(path, state, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HostRecoveryState?> GetAsync(
        string runtimeInstanceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
        var path = GetPath(runtimeInstanceId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var state = await ReadAsync(path, cancellationToken);
            ValidateOwnership(state, runtimeInstanceId, path);
            return state;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<HostRecoveryState>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_stateDirectory))
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var result = new List<HostRecoveryState>();
            foreach (var path in Directory.EnumerateFiles(
                _stateDirectory,
                "recovery-*.json",
                SearchOption.TopDirectoryOnly))
            {
                result.Add(await ReadAsync(path, cancellationToken));
            }

            return result
                .OrderByDescending(static item => item.UpdatedAt)
                .ThenBy(static item => item.RuntimeInstanceId, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static void Validate(HostRecoveryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.RuntimeInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.HostInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.Workspace);
        ArgumentOutOfRangeException.ThrowIfNegative(state.LastConfirmedGsn);
    }

    private static void ValidateOwnership(
        HostRecoveryState state,
        string runtimeInstanceId,
        string path)
    {
        Validate(state);
        if (!string.Equals(state.RuntimeInstanceId, runtimeInstanceId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Recovery state '{path}' belongs to another Runtime instance.");
        }
    }

    private static async Task<HostRecoveryState> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });
        return await JsonSerializer.DeserializeAsync(
                stream,
                MConsoleJsonContext.Default.HostRecoveryState,
                cancellationToken)
            ?? throw new InvalidDataException($"Recovery state '{path}' is empty.");
    }

    private static async Task WriteAtomicAsync(
        string path,
        HostRecoveryState state,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            }))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    state,
                    MConsoleJsonContext.Default.HostRecoveryState,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string GetPath(string runtimeInstanceId)
    {
        var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(runtimeInstanceId)))
            .ToLowerInvariant();
        return Path.Combine(_stateDirectory, $"recovery-{hash}.json");
    }
}
