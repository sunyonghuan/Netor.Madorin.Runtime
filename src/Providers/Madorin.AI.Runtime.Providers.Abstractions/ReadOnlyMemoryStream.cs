namespace Madorin.AI.Runtime.Providers.Abstractions;

internal sealed class ReadOnlyMemoryStream(ReadOnlyMemory<byte> data) : Stream
{
    private int _offset;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position { get => _offset; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var length = Math.Min(count, data.Length - _offset);
        data.Span.Slice(_offset, length).CopyTo(buffer.AsSpan(offset, length));
        _offset += length;
        return length;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var length = Math.Min(buffer.Length, data.Length - _offset);
        data.Slice(_offset, length).CopyTo(buffer);
        _offset += length;
        return ValueTask.FromResult(length);
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
