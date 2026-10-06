using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Tools.Abstractions;

public interface IToolResultBlobStore
{
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
