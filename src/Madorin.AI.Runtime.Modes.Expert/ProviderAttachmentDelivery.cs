using System.Security.Cryptography;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Modes.Expert;

/// <summary>Publishes confirmed Provider attachments as uniquely named local files and Markdown links.</summary>
public static class ProviderAttachmentDelivery
{
    public static async Task<string> SaveAsync(AttachmentCompletedProviderEvent attachment,
        IToolResultBlobStore store, string applicationDirectory, CancellationToken ct)
    {
        var directory = Path.Combine(Path.GetFullPath(applicationDirectory), ".attach");
        Directory.CreateDirectory(directory);
        var extension = attachment.Blob.ContentType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "audio/wav" => ".wav",
            "audio/mpeg" => ".mp3",
            "audio/pcm" => ".pcm",
            "video/mp4" => ".mp4",
            "application/pdf" => ".pdf",
            _ => ".bin"
        };
        var name = Guid.NewGuid().ToString("N") + extension;
        var path = Path.Combine(directory, name);
        var created = false;
        try
        {
            await using (var source = await store.OpenReadAsync(attachment.Blob, ct).ConfigureAwait(false))
            await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                created = true;
                await source.CopyToAsync(target, 128 * 1024, ct).ConfigureAwait(false);
                if (target.Length != attachment.Blob.Length)
                {
                    throw new InvalidDataException("Provider attachment delivery length mismatch.");
                }
            }
            await using var verification = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(verification, ct).ConfigureAwait(false));
            if (!hash.Equals(attachment.Blob.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Provider attachment delivery SHA-256 mismatch.");
            }
        }
        catch
        {
            if (created)
            {
                File.Delete(path);
            }
            throw;
        }
        var uri = new Uri(path).AbsoluteUri.Replace("(", "%28", StringComparison.Ordinal)
            .Replace(")", "%29", StringComparison.Ordinal);
        var prefix = attachment.Blob.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "!" : "";
        return $"\n\n{prefix}[{name}]({uri})\n\n";
    }
}
