using System.Text;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Services.Tools;

/// <summary>
/// 把超过内联上限的工具正文收成模型一定能看见的预览。
/// 完整正文留在 Blob，预览写明总长度和截断位置。
/// </summary>
public static class ToolResultPreview
{
    /// <summary>
    /// 按 Provider 能力决定模型可见正文。文本类 Blob 不依赖 Files 能力。
    /// </summary>
    public static async Task<ToolResultContentBlock> MaterializeAsync(
        ToolResultContentBlock result,
        bool supportsFileBlobs,
        IToolResultBlobStore? blobStore,
        int maxInlineBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxInlineBytes, 1);

        var content = new List<ContentBlock>(result.Content.Length);
        foreach (var block in result.Content)
        {
            switch (block)
            {
                case TextContentBlock text when Encoding.UTF8.GetByteCount(text.Text) > maxInlineBytes:
                    content.Add(new TextContentBlock(FitUtf8(text.Text, maxInlineBytes, text.Text.Length)));
                    break;
                case BlobRefContentBlock blob when supportsFileBlobs && !IsTextual(blob.Blob.ContentType):
                    content.Add(blob);
                    break;
                case BlobRefContentBlock blob when IsTextual(blob.Blob.ContentType):
                    content.Add(new TextContentBlock(
                        await ReadTextPreviewAsync(blob.Blob, blobStore, maxInlineBytes, ct)
                            .ConfigureAwait(false)));
                    break;
                case BlobRefContentBlock blob:
                    content.Add(new TextContentBlock(DescribeUntypedBlob(blob.Blob)));
                    break;
                default:
                    content.Add(block);
                    break;
            }
        }

        return result with { Content = [.. content] };
    }

    private static async Task<string> ReadTextPreviewAsync(
        BlobReference reference,
        IToolResultBlobStore? blobStore,
        int maxInlineBytes,
        CancellationToken ct)
    {
        if (blobStore is null)
        {
            return DescribeUntypedBlob(reference);
        }

        ReadOnlyMemory<byte> bytes;
        try
        {
            bytes = await blobStore.ReadAsync(reference, maxInlineBytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return $"[Tool result is stored as blob {reference.BlobId} ({reference.Length} bytes) but could not be read: {ex.Message}]";
        }

        var text = Encoding.UTF8.GetString(bytes.Span);
        return FitUtf8(text, maxInlineBytes, reference.Length);
    }

    private static string FitUtf8(string text, int maxInlineBytes, long totalBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxInlineBytes && totalBytes <= maxInlineBytes)
        {
            return text;
        }

        var notice = $"\n[truncated: showing the first {maxInlineBytes} bytes of {totalBytes} bytes. The complete tool result is stored as a blob.]";
        var noticeBytes = Encoding.UTF8.GetByteCount(notice);
        var budget = Math.Max(0, maxInlineBytes - noticeBytes);
        var kept = TrimToUtf8Bytes(text, budget);
        return kept + notice;
    }

    private static string TrimToUtf8Bytes(string text, int budget)
    {
        if (budget == 0 || text.Length == 0)
        {
            return string.Empty;
        }

        var count = 0;
        var index = 0;
        while (index < text.Length)
        {
            var character = text[index];
            int size;
            if (char.IsHighSurrogate(character)
                && index + 1 < text.Length
                && char.IsLowSurrogate(text[index + 1]))
            {
                size = Encoding.UTF8.GetByteCount(text.AsSpan(index, 2));
                if (count + size > budget)
                {
                    break;
                }

                count += size;
                index += 2;
                continue;
            }

            size = Encoding.UTF8.GetByteCount(text.AsSpan(index, 1));
            if (count + size > budget)
            {
                break;
            }

            count += size;
            index++;
        }

        return text[..index];
    }

    private static string DescribeUntypedBlob(BlobReference reference) =>
        $"[Tool result stored as blob {reference.BlobId}, content type {reference.ContentType}, {reference.Length} bytes. This provider cannot read the blob.]";

    private static bool IsTextual(string contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var separator = contentType.IndexOf(';');
        var mediaType = (separator < 0 ? contentType : contentType[..separator]).Trim();
        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase);
    }
}
