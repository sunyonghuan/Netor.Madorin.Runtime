using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Providers.WorkerHost;

/// <summary>每个 Invoke 独享的拉取式附件读取器；只缓冲有配额的 SDK 输入。</summary>
internal sealed class WorkerInputBlobResolver(
    string invocationId,
    string generation,
    int maximumBytes,
    Func<ProviderWorkerFrame, CancellationToken, Task> write) : IProviderBlobResolver, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PendingRead? _pending;
    private long _bufferedBytes;
    public string Generation => generation;

    public void Accept(BlobChunkFrame chunk)
    {
        var pending = Volatile.Read(ref _pending);
        if (chunk.InvocationId == invocationId && chunk.Generation == generation
            && pending is not null && chunk.TransferId == pending.TransferId)
        {
            pending.Completion.TrySetResult(chunk);
        }
    }

    public async ValueTask<ResolvedProviderBlob> ResolveAsync(BlobReference blob, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (maximumBytes <= 0 || maximumBytes > ProviderWorkerIpc.MaxBufferedBlobBytes
                || blob.Length < 0 || blob.Length > maximumBytes - _bufferedBytes
                || blob.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                throw new InvalidDataException("The attachment exceeds its Worker buffer quota or lease.");
            }
            _bufferedBytes += blob.Length;
            var bytes = new byte[checked((int)blob.Length)];
            var offset = 0;
            var transferId = Guid.NewGuid().ToString("N");
            do
            {
                ct.ThrowIfCancellationRequested();
                var count = (int)Math.Min(ProviderWorkerIpc.MaxBlobChunkBytes, blob.Length - offset);
                var pending = new TaskCompletionSource<BlobChunkFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _pending, new PendingRead(transferId, pending));
                await write(new BlobReadFrame(invocationId, generation, transferId, blob, offset, count), ct)
                    .ConfigureAwait(false);
                var chunk = await pending.Task.WaitAsync(ct).ConfigureAwait(false);
                if (chunk.Error is not null)
                {
                    throw new InvalidDataException(chunk.Error);
                }
                if (chunk.Offset != offset || chunk.Data.Length != count
                    || chunk.EndOfBlob != (offset + count == blob.Length))
                {
                    throw new InvalidDataException("Invalid Worker attachment chunk sequence or length.");
                }
                chunk.Data.CopyTo(bytes, offset);
                offset += count;
            }
            while (offset < bytes.Length);

            return new ResolvedProviderBlob(bytes, blob.ContentType);
        }
        finally
        {
            Volatile.Write(ref _pending, null);
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private sealed record PendingRead(string TransferId, TaskCompletionSource<BlobChunkFrame> Completion);
}
