using System.IO.Pipelines;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.WorkerHost;

namespace Madorin.AI.Runtime.Server.ProviderWorkers;

/// <summary>Invocation-owned bounded output stream; only Runtime chooses storage and authorization scope.</summary>
internal sealed class WorkerBlobOutputLease(RuntimeProviderRequest request, string generation,
    CancellationToken cancellationToken) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<BlobReference> _completed = [];
    private Pipe? _pipe;
    private Task<BlobReference>? _storage;
    private string? _transfer;
    private long _offset;
    private long _length;
    private long _total;
    private int _disposed;

    public string Generation => generation;

    public bool Contains(BlobReference blob)
    {
        lock (_completed)
        {
            return _completed.Contains(blob);
        }
    }

    public async Task<BlobWriteAckFrame> WriteAsync(BlobWriteFrame frame)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (frame.Generation != generation || frame.InvocationId != request.InvocationId
            || !Guid.TryParseExact(frame.TransferId, "N", out _)
            || frame.Data.Length > ProviderWorkerIpc.MaxBlobChunkBytes || request.StoreOutputBlob is null)
        {
            throw new InvalidDataException("Worker output write is not authorized.");
        }
        await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            _stop.Token.ThrowIfCancellationRequested();
            BlobReference? blob = null;
            switch (frame.Operation)
            {
                case "begin" when _pipe is null:
                    if (frame.Offset != 0 || frame.Data.Length != 0 || frame.Length < 0
                        || frame.Length > Math.Min(request.MaximumBufferedBlobBytes,
                            ProviderWorkerIpc.MaxBufferedBlobBytes) - _total
                        || string.IsNullOrWhiteSpace(frame.ContentType) || frame.ContentType.Length > 128
                        || frame.ContentType.Any(char.IsControl)
                        || !(frame.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                            || frame.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                            || frame.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                            || frame.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
                            || frame.ContentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
                        || frame.Sha256 is not { Length: 64 } || frame.Sha256.Any(c => !Uri.IsHexDigit(c)))
                    {
                        throw new InvalidDataException("Worker output metadata or quota is invalid.");
                    }
                    _total += frame.Length;
                    _length = frame.Length;
                    _offset = 0;
                    _transfer = frame.TransferId;
                    _pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 256 * 1024,
                        resumeWriterThreshold: 128 * 1024));
                    _storage = StoreAsync(_pipe, frame.ContentType, frame.Length, frame.Sha256);
                    break;
                case "chunk" when _pipe is not null && frame.TransferId == _transfer:
                    if (frame.Offset != _offset || frame.Data.Length == 0 || frame.Data.Length > _length - _offset)
                    {
                        throw new InvalidDataException("Worker output chunk sequence or length is invalid.");
                    }
                    if (_storage!.IsCompleted)
                    {
                        await _storage.ConfigureAwait(false);
                        throw new InvalidDataException("Worker output storage ended before all chunks.");
                    }
                    var flush = await _pipe.Writer.WriteAsync(frame.Data, _stop.Token).ConfigureAwait(false);
                    if (flush.IsCanceled || flush.IsCompleted)
                    {
                        await _storage.ConfigureAwait(false);
                        throw new InvalidDataException("Worker output storage closed early.");
                    }
                    _offset += frame.Data.Length;
                    break;
                case "complete" when _pipe is not null && frame.TransferId == _transfer:
                    if (frame.Offset != _offset || _offset != _length || frame.Data.Length != 0)
                    {
                        throw new InvalidDataException("Worker output completion length is invalid.");
                    }
                    await _pipe.Writer.CompleteAsync().ConfigureAwait(false);
                    blob = await _storage!.ConfigureAwait(false);
                    lock (_completed)
                    {
                        _completed.Add(blob);
                    }
                    _pipe = null;
                    _storage = null;
                    break;
                default:
                    throw new InvalidDataException("Invalid Worker output operation or transfer.");
            }
            return new(frame.InvocationId, generation, frame.TransferId, frame.Operation, frame.Offset, blob);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<BlobReference> StoreAsync(Pipe pipe, string mime, long length, string hash)
    {
        await using var source = pipe.Reader.AsStream();
        return await request.StoreOutputBlob!(source, mime, length, hash, _stop.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_pipe is not null)
            {
                await _pipe.Writer.CompleteAsync(new OperationCanceledException()).ConfigureAwait(false);
            }
            if (_storage is not null)
            {
                try { await _storage.ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException) { }
            }
        }
        finally
        {
            _gate.Release();
            _stop.Dispose();
        }
    }
}
