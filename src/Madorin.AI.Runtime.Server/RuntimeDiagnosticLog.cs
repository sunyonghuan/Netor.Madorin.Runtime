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
    private readonly string _logDirectory;
    private readonly RuntimeDiagnosticLogLevel _minimumLevel;
    private readonly TimeProvider _timeProvider;
    private FileStream? _errorStream;
    private string? _errorHourKey;
    private int _disposed;

    private RuntimeDiagnosticLog(
        FileStream stream,
        string logDirectory,
        string runtimeInstanceId,
        string workspaceRoot,
        RuntimeDiagnosticLogLevel minimumLevel,
        TimeProvider timeProvider)
    {
        _stream = stream;
        _logDirectory = logDirectory;
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
        // 一次运行只写当天一个文件。跨分钟时不再换文件，外部可以持续读取。
        var fileName = "runtime.jsonl";
        Directory.CreateDirectory(normalizedDirectory);
        var stream = new FileStream(
            Path.Combine(normalizedDirectory, fileName),
            new FileStreamOptions
            {
                Mode = FileMode.Append,
                Access = FileAccess.Write,
                // 运行中允许其他进程读取，长任务可以边跑边看日志。
                Share = FileShare.ReadWrite,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
        return new RuntimeDiagnosticLog(
            stream,
            Path.GetFullPath(logDirectory),
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
        Action<Utf8JsonWriter>? writeDetails = null,
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

            // ProviderDetails 是 Provider 层生成的脱敏 JSON（状态码、请求标识、异常类型链、失败阶段等），
            // 原样写出，便于在没有 HTTP 状态码的协议类失败中定位原因。
            if (error?.ProviderDetails is { ValueKind: not JsonValueKind.Undefined } providerDetails)
            {
                writer.WritePropertyName("providerDetails");
                providerDetails.WriteTo(writer);
            }

            WriteOptionalString(writer, "exceptionType", exception?.GetType().FullName);
            WriteOptionalString(writer, "exceptionMessage", exception?.Message);
            WriteOptionalString(writer, "stackTrace", exception?.ToString());
            writeDetails?.Invoke(writer);
            writer.WriteEndObject();
        }

        buffer.GetSpan(1)[0] = (byte)'\n';
        buffer.Advance(1);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(buffer.WrittenMemory, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
            if (level == RuntimeDiagnosticLogLevel.Error)
            {
                await WriteErrorLineAsync(buffer.WrittenMemory, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// 错误单独写入日志目录下的 yyyyMMddHH.error.log，按本地小时切换文件。
    /// 内容与 runtime.jsonl 的同一行一致，包含错误码、消息和 ProviderDetails。
    /// </summary>
    private async ValueTask WriteErrorLineAsync(ReadOnlyMemory<byte> line, CancellationToken ct)
    {
        var hourKey = _timeProvider.GetLocalNow().ToString("yyyyMMddHH", CultureInfo.InvariantCulture);
        if (_errorStream is null || !string.Equals(_errorHourKey, hourKey, StringComparison.Ordinal))
        {
            if (_errorStream is not null)
            {
                await _errorStream.DisposeAsync().ConfigureAwait(false);
                _errorStream = null;
            }

            Directory.CreateDirectory(_logDirectory);
            _errorStream = new FileStream(
                Path.Combine(_logDirectory, hourKey + ".error.log"),
                new FileStreamOptions
                {
                    Mode = FileMode.Append,
                    Access = FileAccess.Write,
                    Share = FileShare.ReadWrite,
                    BufferSize = 4096,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
            _errorHourKey = hourKey;
        }

        var errorStream = _errorStream
            ?? throw new InvalidOperationException("The error log was not opened.");
        await errorStream.WriteAsync(line, ct).ConfigureAwait(false);
        await errorStream.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask TryWriteAsync(
        RuntimeDiagnosticLogLevel level,
        string eventName,
        string? runId = null,
        string? sessionId = null,
        string? diagnosticId = null,
        Exception? exception = null,
        RuntimeError? error = null,
        bool alwaysWrite = false,
        Action<Utf8JsonWriter>? writeDetails = null)
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
                    writeDetails,
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
                if (_errorStream is not null)
                {
                    await _errorStream.FlushAsync().ConfigureAwait(false);
                    await _errorStream.DisposeAsync().ConfigureAwait(false);
                    _errorStream = null;
                }

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
