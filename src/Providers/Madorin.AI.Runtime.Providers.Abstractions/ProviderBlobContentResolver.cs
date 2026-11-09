using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>在 Provider 边界校验 Blob 引用与实际字节，随后创建原生附件内容。</summary>
public static class ProviderBlobContentResolver
{
    /// <summary>Resolve only media with an implemented wire codec; model support is decided by the endpoint.</summary>
    public static ValueTask<DataContent> ResolveAsync(
        IProviderBlobResolver? resolver,
        BlobReference blob,
        ProviderAttachmentRoute route,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var mime = blob.ContentType;
        var supported = mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || mime.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
            || (route is ProviderAttachmentRoute.OpenAIChatCompletions or ProviderAttachmentRoute.AliyunChatCompletions
                && (mime.Equals("audio/wav", StringComparison.OrdinalIgnoreCase)
                    || mime.Equals("audio/mpeg", StringComparison.OrdinalIgnoreCase)))
            || (route is ProviderAttachmentRoute.AliyunChatCompletions
                && mime.Equals("video/mp4", StringComparison.OrdinalIgnoreCase));
        if (!supported)
        {
            throw new RuntimeProviderException(new RuntimeError(
                RuntimeErrorCodes.ProviderProtocolError, "protocol",
                $"No attachment codec is configured for media type '{mime}' on route '{route}'.",
                false, null, Guid.NewGuid().ToString("N")));
        }

        // DashScope's inline video data URI must remain below 10 MB after Base64 encoding.
        if (route is ProviderAttachmentRoute.AliyunChatCompletions
            && mime.Equals("video/mp4", StringComparison.OrdinalIgnoreCase)
            && (blob.Length < 0 || ((blob.Length + 2) / 3) * 4 + "data:video/mp4;base64,".Length >= 10_000_000))
        {
            throw new RuntimeProviderException(new RuntimeError(
                RuntimeErrorCodes.ProviderProtocolError, "protocol",
                "The Aliyun inline video exceeds the 10 MB encoded data URI limit.",
                false, null, Guid.NewGuid().ToString("N")));
        }

        return ResolveAsync(resolver, blob, ct);
    }

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
