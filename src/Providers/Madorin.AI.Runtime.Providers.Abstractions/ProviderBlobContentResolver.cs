using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>在 Provider 边界校验 Blob 引用与实际字节，随后创建原生附件内容。</summary>
public static class ProviderBlobContentResolver
{
    public static async ValueTask<DataContent> ResolveAsync(
        IProviderBlobResolver? resolver,
        BlobReference blob,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (resolver is null)
        {
            throw new InvalidOperationException("No Provider Blob resolver is configured.");
        }

        if (blob.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidDataException("The Provider Blob reference has expired.");
        }

        var resolved = await resolver.ResolveAsync(blob, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (resolved.Data.Length != blob.Length
            || !string.Equals(resolved.ContentType, blob.ContentType, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Convert.ToHexString(SHA256.HashData(resolved.Data.Span)),
                blob.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Provider Blob length, SHA-256 or content type does not match its descriptor.");
        }

        var fileName = resolved.FileName;
        if (fileName is not null)
        {
            fileName = fileName[(Math.Max(fileName.LastIndexOf('/'), fileName.LastIndexOf('\\')) + 1)..];
        }

        return new DataContent(resolved.Data, resolved.ContentType) { Name = fileName };
    }
}
