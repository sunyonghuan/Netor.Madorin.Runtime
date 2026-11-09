using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin;

public sealed partial class BuiltinFsToolExecutor
{
    private FsPreparedExecution PrepareAttach(ToolInvocation invocation, JsonElement arguments,
        BuiltinFsPathPolicy policy)
    {
        if (_blobStore is null)
        {
            throw new InvalidDataException("A Blob store is required to attach a file.");
        }

        var file = policy.PinReadFile(GetRequiredString(arguments, "path"));
        return new FsPreparedExecution(policy.GetTargetSummary(file.Path),
            ct => AttachAsync(invocation, arguments, file, ct), [file]);
    }

    private async Task<ToolResult> AttachAsync(ToolInvocation invocation, JsonElement arguments,
        PinnedPath file, CancellationToken ct)
    {
        var length = RandomAccess.GetLength(file.Handle);
        var header = new byte[(int)Math.Min(length, 32)];
        var count = 0;
        while (count < header.Length)
        {
            var read = await RandomAccess.ReadAsync(file.Handle, header.AsMemory(count), count, ct)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The attachment changed while reading its header.");
            }

            count += read;
        }

        var contentType = DetectAttachmentType(header, Path.GetExtension(file.Path));
        // 读取已经固定的句柄；不按原路径再次打开，避免检查与读取之间发生路径替换。
        await using var source = new PinnedAttachmentStream(file.Handle);
        var blob = await _blobStore!.StoreAsync(invocation.RunId, source, contentType, "run",
            DateTimeOffset.UtcNow.AddDays(365), length, GetOptionalString(arguments, "expectedHash"), ct)
            .ConfigureAwait(false);
        var metadata = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("fileName", Path.GetFileName(file.Path));
            writer.WriteString("contentType", blob.ContentType);
            writer.WriteNumber("byteLength", blob.Length);
            writer.WriteString("sha256", blob.Sha256);
            writer.WriteEndObject();
        });
        return new ToolResult(invocation.CallId, invocation.ToolId, true, metadata, ResultBlob: blob);
    }

    private static string DetectAttachmentType(ReadOnlySpan<byte> header, string extension)
    {
        if (header.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            return "image/png";
        }

        if (header.StartsWith(new byte[] { 255, 216, 255 }))
        {
            return "image/jpeg";
        }

        if (header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }

        if (header.StartsWith("%PDF-"u8))
        {
            return "application/pdf";
        }

        if (header.Length >= 12 && header.StartsWith("RIFF"u8))
        {
            if (header[8..12].SequenceEqual("WEBP"u8))
            {
                return "image/webp";
            }

            if (header[8..12].SequenceEqual("WAVE"u8))
            {
                return "audio/wav";
            }
        }

        // ISO BMFF 容器必须同时匹配签名与扩展名，不能把任意 .mp4 文件认作视频。
        if (header.Length >= 12 && header[4..8].SequenceEqual("ftyp"u8)
            && string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return "video/mp4";
        }

        if (header.StartsWith("ID3"u8))
        {
            return "audio/mpeg";
        }

        return "application/octet-stream";
    }

    /// <summary>借用固定句柄的顺序读取视图；所有权仍属于 PreparedExecution。</summary>
    private sealed class PinnedAttachmentStream(SafeFileHandle handle) : Stream
    {
        private long _offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => RandomAccess.GetLength(handle);
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = RandomAccess.Read(handle, buffer.AsSpan(offset, count), _offset);
            _offset += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await RandomAccess.ReadAsync(handle, buffer, _offset, ct).ConfigureAwait(false);
            _offset += read;
            return read;
        }
    }
}
