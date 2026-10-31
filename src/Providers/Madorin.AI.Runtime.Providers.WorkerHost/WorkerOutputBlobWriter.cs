using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Providers.WorkerHost;

internal sealed class WorkerOutputBlobWriter(string invocationId, string generation, int maximumBytes,
    Func<ProviderWorkerFrame, CancellationToken, Task> write) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TaskCompletionSource<BlobWriteAckFrame>? _pending;
    private BlobWriteFrame? _expected;
    private long _total;

    public void Dispose() => _gate.Dispose();

    public void Accept(BlobWriteAckFrame ack)
    {
        var expected = Volatile.Read(ref _expected);
        if (expected is not null && ack.InvocationId == invocationId && ack.Generation == generation
            && ack.TransferId == expected.TransferId && ack.Operation == expected.Operation
            && ack.Offset == expected.Offset)
        {
            Volatile.Read(ref _pending)?.TrySetResult(ack);
        }
    }

    public async Task<BlobReference> StoreAsync(Stream source, string mime, long length, string hash,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var transfer = Guid.NewGuid().ToString("N");
        try
        {
            if (length < 0 || length > maximumBytes - _total)
            {
                throw new InvalidDataException("Provider output exceeds the invocation quota.");
            }
            _total += length;
            await ExchangeAsync(new(invocationId, generation, transfer, "begin", 0, [], mime, length, hash), ct)
                .ConfigureAwait(false);
            long offset = 0;
            while (offset < length)
            {
                var bytes = new byte[(int)Math.Min(ProviderWorkerIpc.MaxBlobChunkBytes, length - offset)];
                await source.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
                await ExchangeAsync(new(invocationId, generation, transfer, "chunk", offset, bytes), ct)
                    .ConfigureAwait(false);
                offset += bytes.Length;
            }
            if (await source.ReadAsync(new byte[1], ct).ConfigureAwait(false) != 0)
            {
                throw new InvalidDataException("Provider output length changed.");
            }
            var ack = await ExchangeAsync(new(invocationId, generation, transfer, "complete", offset, []), ct)
                .ConfigureAwait(false);
            return ack.Blob ?? throw new InvalidDataException("Provider output completion has no Blob reference.");
        }
        finally
        {
            Volatile.Write(ref _expected, null);
            Volatile.Write(ref _pending, null);
            _gate.Release();
        }
    }

    private async Task<BlobWriteAckFrame> ExchangeAsync(BlobWriteFrame frame, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<BlobWriteAckFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _pending, completion);
        Volatile.Write(ref _expected, frame);
        await write(frame, ct).ConfigureAwait(false);
        var ack = await completion.Task.WaitAsync(ct).ConfigureAwait(false);
        if (ack.Error is not null)
        {
            throw new InvalidDataException(ack.Error);
        }
        return ack;
    }
}
