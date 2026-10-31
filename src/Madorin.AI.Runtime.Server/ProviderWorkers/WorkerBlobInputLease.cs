using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.WorkerHost;

namespace Madorin.AI.Runtime.Server.ProviderWorkers;

/// <summary>绑定一次请求代次的附件白名单及顺序读取租约，不接受 Worker 提供的磁盘路径。</summary>
internal sealed class WorkerBlobInputLease(RuntimeProviderRequest request, string generation,
    CancellationToken cancellationToken) : IAsyncDisposable
{
    private readonly HashSet<BlobReference> _allowed = Collect(request.Messages.SelectMany(message => message.Content));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    private Stream? _stream;
    private string? _transferId;
    private BlobReference? _blob;
    private long _offset;
    private long _buffered;
    private int _disposed;

    public async Task<BlobChunkFrame> ReadAsync(BlobReadFrame read)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (read.Generation != generation || read.InvocationId != request.InvocationId
            || !_allowed.Contains(read.Blob) || string.IsNullOrWhiteSpace(read.TransferId)
            || read.Count < 0 || read.Count > ProviderWorkerIpc.MaxBlobChunkBytes
            || read.Offset < 0 || read.Offset > read.Blob.Length
            || read.Count != Math.Min(ProviderWorkerIpc.MaxBlobChunkBytes, read.Blob.Length - read.Offset)
            || read.Blob.ExpiresAt <= DateTimeOffset.UtcNow || request.OpenBlobRead is null)
        {
            throw new InvalidDataException("The Worker attachment read is not authorized or is outside its bounds.");
        }
        if (!await _gate.WaitAsync(0, _stop.Token).ConfigureAwait(false))
        {
            throw new InvalidDataException("Only one Worker attachment read may be outstanding.");
        }
        try
        {
            _stop.Token.ThrowIfCancellationRequested();
            if (_stream is null)
            {
                if (read.Offset != 0 || read.Blob.Length < 0
                    || read.Blob.Length > Math.Min(request.MaximumBufferedBlobBytes,
                        ProviderWorkerIpc.MaxBufferedBlobBytes) - _buffered)
                {
                    throw new InvalidDataException("Worker attachment buffer quota exceeded or invalid initial offset.");
                }
                _buffered += read.Blob.Length;
                _stream = await request.OpenBlobRead(read.Blob, _stop.Token).ConfigureAwait(false);
                _transferId = read.TransferId;
                _blob = read.Blob;
                _offset = 0;
            }
            if (_transferId != read.TransferId || _blob != read.Blob || _offset != read.Offset)
            {
                throw new InvalidDataException("Worker attachment transfer sequence mismatch.");
            }
            var data = new byte[read.Count];
            await _stream.ReadExactlyAsync(data, _stop.Token).ConfigureAwait(false);
            _offset += data.Length;
            var end = _offset == read.Blob.Length;
            if (end)
            {
                var extra = new byte[1];
                if (await _stream.ReadAsync(extra, _stop.Token).ConfigureAwait(false) != 0)
                {
                    throw new InvalidDataException("Worker attachment source length changed.");
                }
                await _stream.DisposeAsync().ConfigureAwait(false);
                _stream = null;
            }
            return new BlobChunkFrame(read.InvocationId, generation, read.TransferId, read.Offset, data, end);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static HashSet<BlobReference> Collect(IEnumerable<ContentBlock> blocks)
    {
        var result = new HashSet<BlobReference>();
        foreach (var block in blocks)
        {
            if (block is BlobRefContentBlock blob)
            {
                result.Add(blob.Blob);
            }
            else if (block is ToolResultContentBlock tool)
            {
                result.UnionWith(Collect(tool.Content));
            }
        }
        return result;
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
            if (_stream is not null)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
                _stream = null;
            }
        }
        finally
        {
            _gate.Release();
            _stop.Dispose();
        }
        // gate 没有创建 OS 等待句柄；迟到读取可能仍引用它，取消后只允许失败，不重新开流。
    }
}
