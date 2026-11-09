using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Tools.Abstractions;

public interface IToolResultBlobStore
{
    /// <summary>流式保存附件；仅在长度和预期哈希验证成功后发布引用，调用方保留输入流所有权。</summary>
    public Task<BlobReference> StoreAsync(
        string runId,
        Stream content,
        string contentType,
        string accessScope,
        DateTimeOffset expiresAt,
        long? expectedLength = null,
        string? expectedSha256 = null,
        CancellationToken ct = default) =>
        throw new NotSupportedException("This Blob store does not support streaming writes.");

    /// <summary>打开已验证的附件读取流；调用方负责释放，不将正文缓冲为完整 byte 数组。</summary>
    public Task<Stream> OpenReadAsync(BlobReference reference, CancellationToken ct = default) =>
        throw new NotSupportedException("This Blob store does not support streaming reads.");

    public Task<BlobReference> StoreAsync(
        string runId,
        ReadOnlyMemory<byte> content,
        string contentType,
        string accessScope,
        DateTimeOffset expiresAt,
        CancellationToken ct = default);

    public Task ValidateReferenceAsync(
        BlobReference reference,
        CancellationToken ct = default);

    /// <summary>
    /// 读取已保存的工具结果正文。模型不支持文件引用时，用它生成可见预览。
    /// </summary>
    public Task<ReadOnlyMemory<byte>> ReadAsync(
        BlobReference reference,
        int maxBytes,
        CancellationToken ct = default);
}
