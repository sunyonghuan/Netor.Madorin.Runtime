using System.Security.Cryptography;
using Microsoft.Extensions.AI;

namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>Converts SDK binary output to confirmed references, with bounded buffering for streamed audio.</summary>
internal sealed class ProviderOutputContentMapper(RuntimeProviderRequest request) : IDisposable
{
    private readonly MemoryStream _audio = new();
    private readonly List<AIContent> _deferred = [];
    private string? _audioMime;
    private string? _audioName;
    private long _totalBytes;

    public async Task<AIContent[]> MapAsync(AIContent[] source, bool terminal, CancellationToken ct)
    {
        var output = new List<AIContent>();
        foreach (var content in source)
        {
            if (content is DataContent data && data.HasTopLevelMediaType("audio"))
            {
                Reserve(data.Data.Length);
                if (_audioMime is not null && !string.Equals(_audioMime, data.MediaType, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Provider audio fragments have inconsistent MIME types.");
                }
                _audioMime = data.MediaType;
                _audioName ??= data.Name;
                await _audio.WriteAsync(data.Data, ct).ConfigureAwait(false);
            }
            else if (_audioMime is not null)
            {
                ReserveDeferred(content);
                _deferred.Add(content);
            }
            else
            {
                await AddAsync(content, output, ct).ConfigureAwait(false);
            }
        }
        if (terminal && _audioMime is not null)
        {
            var data = new DataContent(_audio.GetBuffer().AsMemory(0, checked((int)_audio.Length)), _audioMime)
            {
                Name = _audioName
            };
            output.Add(await StoreAsync(data, ct).ConfigureAwait(false));
            foreach (var content in _deferred)
            {
                await AddAsync(content, output, ct, reserve: false).ConfigureAwait(false);
            }
            _deferred.Clear();
            _audioMime = null;
            _audio.SetLength(0);
        }
        return [.. output];
    }

    private async Task AddAsync(AIContent content, List<AIContent> output, CancellationToken ct, bool reserve = true)
    {
        switch (content)
        {
            case ProviderOutputFileContent file:
                await AddAsync(file.File, output, ct, reserve).ConfigureAwait(false);
                break;
            case DataContent data:
                if (reserve)
                {
                    Reserve(data.Data.Length);
                }
                output.Add(await StoreAsync(data, ct).ConfigureAwait(false));
                break;
            case ImageGenerationToolResultContent image:
                // Partial previews do not represent a completed attachment.
                foreach (var part in image.Outputs ?? [])
                {
                    if (part.AdditionalProperties?.ContainsKey("PartialImageIndex") is not true)
                    {
                        await AddAsync(part, output, ct, reserve).ConfigureAwait(false);
                    }
                }
                break;
            case HostedFileContent or UriContent:
                throw new NotSupportedException("Provider output file references require a route-specific download codec.");
            default:
                output.Add(content);
                break;
        }
    }

    private void ReserveDeferred(AIContent content)
    {
        switch (content)
        {
            case ProviderOutputFileContent file:
                Reserve(file.File.Data.Length);
                break;
            case TextContent text:
                Reserve(System.Text.Encoding.UTF8.GetByteCount(text.Text));
                break;
            case DataContent data:
                Reserve(data.Data.Length);
                break;
            case ImageGenerationToolResultContent image:
                foreach (var part in image.Outputs ?? [])
                {
                    if (part.AdditionalProperties?.ContainsKey("PartialImageIndex") is not true)
                    {
                        ReserveDeferred(part);
                    }
                }
                break;
        }
    }

    private void Reserve(int bytes)
    {
        if (bytes > request.MaximumBufferedBlobBytes - _totalBytes)
        {
            throw new InvalidDataException("Provider output attachment exceeds the invocation buffer quota.");
        }
        _totalBytes += bytes;
    }

    private async Task<ProviderOutputAttachmentContent> StoreAsync(DataContent data, CancellationToken ct)
    {
        if (request.StoreOutputBlob is null)
        {
            throw new InvalidOperationException("No Provider output Blob writer is configured.");
        }
        using var source = new ReadOnlyMemoryStream(data.Data);
        var hash = Convert.ToHexString(SHA256.HashData(data.Data.Span));
        var blob = await request.StoreOutputBlob(source, data.MediaType, data.Data.Length, hash, ct)
            .ConfigureAwait(false);
        if (blob.Length != data.Data.Length || !blob.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase)
            || !blob.ContentType.Equals(data.MediaType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Provider output writer returned an invalid Blob descriptor.");
        }
        return new ProviderOutputAttachmentContent(blob, data.Name?.Replace('\\', '/').Split('/').LastOrDefault());
    }

    public void EnsureCompleted()
    {
        if (_audioMime is not null)
        {
            throw new InvalidDataException("Provider audio stream ended without a completion marker.");
        }
    }

    public void Dispose() => _audio.Dispose();
}
