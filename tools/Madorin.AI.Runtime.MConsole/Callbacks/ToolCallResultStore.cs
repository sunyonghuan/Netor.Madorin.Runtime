using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.MConsole.Serialization;

namespace Madorin.AI.Runtime.MConsole.Callbacks;

public sealed class ToolCallResultStore(string stateDirectory) : IDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks =
        new(StringComparer.Ordinal);
    private readonly string _stateDirectory = Path.GetFullPath(stateDirectory);
    private int _disposed;

    public async Task<StoredToolCallResult?> GetAsync(
        string runtimeInstanceId,
        string callId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateKey(runtimeInstanceId, callId);
        var path = GetPath(runtimeInstanceId, callId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = OpenRead(path);
        var stored = await JsonSerializer.DeserializeAsync(
                stream,
                MConsoleJsonContext.Default.StoredToolCallResult,
                cancellationToken)
            ?? throw new InvalidDataException($"Tool result '{path}' is empty.");
        ValidateStoredKey(stored, runtimeInstanceId, callId, path);
        return stored;
    }

    public async Task<StoredToolCallResult> GetOrExecuteAsync(
        string runtimeInstanceId,
        ToolCallRequest request,
        IToolCallExecutor executor,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(executor);
        ValidateKey(runtimeInstanceId, request.CallId);

        var key = runtimeInstanceId + "\n" + request.CallId;
        var gate = _keyLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await GetAsync(runtimeInstanceId, request.CallId, cancellationToken);
            if (existing is not null)
            {
                return existing;
            }

            ToolCallResponse response;
            try
            {
                response = await executor.ExecuteAsync(request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                response = new ToolCallResponse(
                    request.CorrelationId,
                    request.CallId,
                    ToolCallStatus.Cancelled,
                    ErrorCode: "HostToolCancelled",
                    ErrorMessage: "The host tool call was cancelled.");
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or IOException
                or TimeoutException)
            {
                response = new ToolCallResponse(
                    request.CorrelationId,
                    request.CallId,
                    ToolCallStatus.Failed,
                    ErrorCode: "HostToolFailed",
                    ErrorMessage: ex.Message,
                    DiagnosticId: $"diag_mconsole_tool_{Guid.NewGuid():N}");
            }

            if (!string.Equals(response.CallId, request.CallId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Tool executor returned a response for another callId.");
            }

            var stored = StoredToolCallResult.FromResponse(runtimeInstanceId, response);
            await WriteAtomicAsync(GetPath(runtimeInstanceId, request.CallId), stored, cancellationToken);
            return stored;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var gate in _keyLocks.Values)
        {
            gate.Dispose();
        }

        _keyLocks.Clear();
    }

    private async Task WriteAtomicAsync(
        string path,
        StoredToolCallResult value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_stateDirectory);
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
                    value,
                    MConsoleJsonContext.Default.StoredToolCallResult,
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

    private string GetPath(string runtimeInstanceId, string callId)
    {
        var bytes = Encoding.UTF8.GetBytes(runtimeInstanceId + "\n" + callId);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return Path.Combine(_stateDirectory, $"tool-{hash}.json");
    }

    private static void ValidateKey(string runtimeInstanceId, string callId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
    }

    private static void ValidateStoredKey(
        StoredToolCallResult stored,
        string runtimeInstanceId,
        string callId,
        string path)
    {
        if (!string.Equals(stored.RuntimeInstanceId, runtimeInstanceId, StringComparison.Ordinal)
            || !string.Equals(stored.CallId, callId, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Tool result '{path}' has a mismatched ownership key.");
        }
    }

    private static FileStream OpenRead(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.Read,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
    });

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);
}
