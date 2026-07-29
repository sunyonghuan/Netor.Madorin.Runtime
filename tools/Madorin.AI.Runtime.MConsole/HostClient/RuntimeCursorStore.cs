using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Madorin.AI.Runtime.MConsole.Serialization;

namespace Madorin.AI.Runtime.MConsole.HostClient;

public sealed class RuntimeCursorStore(string stateDirectory) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _stateDirectory = Path.GetFullPath(stateDirectory);

    public async Task<RuntimeCursorState> GetAsync(
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
                return new RuntimeCursorState(runtimeInstanceId, 0, DateTimeOffset.MinValue);
            }

            await using var stream = OpenRead(path);
            var state = await JsonSerializer.DeserializeAsync(
                    stream,
                    MConsoleJsonContext.Default.RuntimeCursorState,
                    cancellationToken)
                ?? throw new InvalidDataException($"Cursor state '{path}' is empty.");
            if (!string.Equals(
                    state.RuntimeInstanceId,
                    runtimeInstanceId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Cursor state '{path}' belongs to another Runtime instance.");
            }

            return state;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeCursorState> AdvanceAsync(
        string runtimeInstanceId,
        long confirmedGsn,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
        ArgumentOutOfRangeException.ThrowIfNegative(confirmedGsn);
        Directory.CreateDirectory(_stateDirectory);
        var path = GetPath(runtimeInstanceId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadUnsafeAsync(path, runtimeInstanceId, cancellationToken);
            if (confirmedGsn < current.LastConfirmedGsn)
            {
                throw new InvalidOperationException(
                    $"GSN cannot move backwards from {current.LastConfirmedGsn} to {confirmedGsn}.");
            }

            if (confirmedGsn == current.LastConfirmedGsn)
            {
                return current;
            }

            var updated = new RuntimeCursorState(
                runtimeInstanceId,
                confirmedGsn,
                DateTimeOffset.UtcNow);
            await WriteAtomicAsync(path, updated, cancellationToken);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static async Task<RuntimeCursorState> ReadUnsafeAsync(
        string path,
        string runtimeInstanceId,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new RuntimeCursorState(runtimeInstanceId, 0, DateTimeOffset.MinValue);
        }

        await using var stream = OpenRead(path);
        var state = await JsonSerializer.DeserializeAsync(
                stream,
                MConsoleJsonContext.Default.RuntimeCursorState,
                cancellationToken)
            ?? throw new InvalidDataException($"Cursor state '{path}' is empty.");
        if (!string.Equals(state.RuntimeInstanceId, runtimeInstanceId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Cursor state '{path}' belongs to another Runtime instance.");
        }

        return state;
    }

    private static async Task WriteAtomicAsync(
        string path,
        RuntimeCursorState state,
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
                    MConsoleJsonContext.Persistence.RuntimeCursorState,
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
        return Path.Combine(_stateDirectory, $"cursor-{hash}.json");
    }

    private static FileStream OpenRead(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.Read,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
    });
}
