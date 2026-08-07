using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Server;

public enum RuntimeDiagnosticLogLevel
{
    Debug,
    Information,
    Warning,
    Error
}

internal sealed class RuntimeDiagnosticLog : IAsyncDisposable
{
    private readonly FileStream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _runtimeInstanceId;
    private readonly string _workspaceRoot;
    private readonly RuntimeDiagnosticLogLevel _minimumLevel;
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    private RuntimeDiagnosticLog(
        FileStream stream,
        string runtimeInstanceId,
        string workspaceRoot,
        RuntimeDiagnosticLogLevel minimumLevel,
        TimeProvider timeProvider)
    {
        _stream = stream;
        _runtimeInstanceId = runtimeInstanceId;
        _workspaceRoot = workspaceRoot;
        _minimumLevel = minimumLevel;
        _timeProvider = timeProvider;
    }

    public static RuntimeDiagnosticLog Create(
        string logDirectory,
        string runtimeInstanceId,
        string workspaceRoot,
        RuntimeDiagnosticLogLevel minimumLevel,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (!Enum.IsDefined(minimumLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(minimumLevel));
        }

        var localNow = timeProvider.GetLocalNow();
        var normalizedDirectory = Path.Combine(
            Path.GetFullPath(logDirectory),
            localNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        var fileName = $"{localNow.ToString("HHmm", CultureInfo.InvariantCulture)}.jsonl";
        Directory.CreateDirectory(normalizedDirectory);
        var stream = new FileStream(
            Path.Combine(normalizedDirectory, fileName),
            new FileStreamOptions
            {
                Mode = FileMode.Append,
                Access = FileAccess.Write,
                Share = FileShare.ReadWrite,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
        return new RuntimeDiagnosticLog(
            stream,
            runtimeInstanceId,
            workspaceRoot,
            minimumLevel,
            timeProvider);
    }

    public async ValueTask WriteAsync(
        RuntimeDiagnosticLogLevel level,
        string eventName,
        string? runId = null,
        string? sessionId = null,
        string? diagnosticId = null,
        Exception? exception = null,
        RuntimeError? error = null,
        bool alwaysWrite = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        if (!alwaysWrite && level < _minimumLevel)
        {
            return;
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }))
        {
            writer.WriteStartObject();
            writer.WriteString("timestamp", _timeProvider.GetUtcNow());
            writer.WriteString("level", level.ToString());
            writer.WriteString("event", eventName);
            writer.WriteString("runtimeInstanceId", _runtimeInstanceId);
            writer.WriteString("workspace", _workspaceRoot);
            WriteOptionalString(writer, "runId", runId);
            WriteOptionalString(writer, "sessionId", sessionId);
            WriteOptionalString(writer, "diagnosticId", diagnosticId ?? error?.DiagnosticId);
            WriteOptionalString(writer, "errorCode", error?.Code);
            WriteOptionalString(writer, "errorCategory", error?.Category);
            WriteOptionalString(writer, "message", error?.Message ?? exception?.Message);
            if (error is not null)
            {
                writer.WriteBoolean("isRetryable", error.IsRetryable);
            }

            WriteOptionalString(writer, "exceptionType", exception?.GetType().FullName);
            WriteOptionalString(writer, "exceptionMessage", exception?.Message);
            WriteOptionalString(writer, "stackTrace", exception?.ToString());
            writer.WriteEndObject();
        }

        buffer.GetSpan(1)[0] = (byte)'\n';
        buffer.Advance(1);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(buffer.WrittenMemory, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask TryWriteAsync(
        RuntimeDiagnosticLogLevel level,
        string eventName,
        string? runId = null,
        string? sessionId = null,
        string? diagnosticId = null,
        Exception? exception = null,
        RuntimeError? error = null,
        bool alwaysWrite = false)
    {
        try
        {
            await WriteAsync(
                    level,
                    eventName,
                    runId,
                    sessionId,
                    diagnosticId,
                    exception,
                    error,
                    alwaysWrite,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException
            or InvalidOperationException
            or ObjectDisposedException
            or UnauthorizedAccessException)
        {
            // Diagnostics must not replace the Runtime operation's observable result.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                await _stream.FlushAsync().ConfigureAwait(false);
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException
                or ObjectDisposedException
                or UnauthorizedAccessException)
            {
                try
                {
                    _stream.Dispose();
                }
                catch (Exception disposeException) when (disposeException is IOException
                    or ObjectDisposedException
                    or UnauthorizedAccessException)
                {
                    // Runtime shutdown must not be replaced by a diagnostic cleanup failure.
                }
            }
        }
        finally
        {
            _writeGate.Release();
            _writeGate.Dispose();
        }
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            writer.WriteString(name, value);
        }
    }
}
