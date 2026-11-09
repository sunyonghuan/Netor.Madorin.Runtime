using System.Buffers.Binary;
using System.Text.Json;

namespace Madorin.AI.Runtime.Providers.WorkerHost;

public enum ProviderWorkerFrameReadStatus
{
    Frame,
    EndOfStream,
    Oversize
}

public readonly record struct ProviderWorkerFrameReadResult(
    ProviderWorkerFrameReadStatus Status,
    ProviderWorkerFrame? Frame,
    int DeclaredLength);

/// <summary>Length-prefixed big-endian JSON frames. Does not kill the process on oversize.</summary>
public static class ProviderWorkerFrameCodec
{
    public static async Task WriteAsync(
        Stream stream,
        ProviderWorkerFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(frame);

        var json = JsonSerializer.SerializeToUtf8Bytes(
            frame,
            ProviderWorkerJsonContext.Default.ProviderWorkerFrame);
        if ((frame is BlobChunkFrame chunkFrame && chunkFrame.Data.Length > ProviderWorkerIpc.MaxBlobChunkBytes)
            || (frame is BlobWriteFrame writeFrame && writeFrame.Data.Length > ProviderWorkerIpc.MaxBlobChunkBytes))
        {
            throw new InvalidDataException("Worker Blob chunk exceeds the data quota.");
        }
        if (json.Length > ProviderWorkerIpc.MaxFrameLength
            || frame is BlobChunkFrame or BlobWriteFrame && json.Length > ProviderWorkerIpc.MaxBlobChunkFrameBytes)
        {
            throw new ProviderWorkerFrameTooLargeException(json.Length);
        }

        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)json.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ProviderWorkerFrameReadResult> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[4];
        var headerRead = await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (headerRead == 0)
        {
            return new ProviderWorkerFrameReadResult(ProviderWorkerFrameReadStatus.EndOfStream, null, 0);
        }

        if (headerRead < 4)
        {
            return new ProviderWorkerFrameReadResult(ProviderWorkerFrameReadStatus.EndOfStream, null, 0);
        }

        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length <= 0 || length > ProviderWorkerIpc.MaxFrameLength)
        {
            return new ProviderWorkerFrameReadResult(ProviderWorkerFrameReadStatus.Oversize, null, length);
        }

        var body = new byte[length];
        var bodyRead = await ReadExactAsync(stream, body, cancellationToken).ConfigureAwait(false);
        if (bodyRead < length)
        {
            return new ProviderWorkerFrameReadResult(ProviderWorkerFrameReadStatus.EndOfStream, null, length);
        }

        var frame = JsonSerializer.Deserialize(body, ProviderWorkerJsonContext.Default.ProviderWorkerFrame)
            ?? throw new JsonException("Worker frame JSON deserialized to null.");
        if (frame is BlobChunkFrame chunk && (length > ProviderWorkerIpc.MaxBlobChunkFrameBytes
            || chunk.Data.Length > ProviderWorkerIpc.MaxBlobChunkBytes))
        {
            throw new InvalidDataException("Worker Blob chunk exceeds the frame quota.");
        }
        if (frame is BlobWriteFrame write && (length > ProviderWorkerIpc.MaxBlobChunkFrameBytes
            || write.Data.Length > ProviderWorkerIpc.MaxBlobChunkBytes))
        {
            throw new InvalidDataException("Worker Blob write exceeds the frame quota.");
        }
        return new ProviderWorkerFrameReadResult(ProviderWorkerFrameReadStatus.Frame, frame, length);
    }

    private static async Task<int> ReadExactAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);
            if (n == 0)
            {
                return offset;
            }

            offset += n;
        }

        return offset;
    }
}
