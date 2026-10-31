using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Madorin.AI.Runtime.Providers.Abstractions;
using Netor.Anthropic.Minimal;

namespace Madorin.AI.Runtime.Providers.Anthropic.Protocol;

/// <summary>在 SDK 丢弃文件结果前，按内容块顺序取回 Anthropic 生成文件。</summary>
internal sealed class AnthropicFileOutputChatClient(IChatClient inner, HttpClient? filesClient,
    Uri? baseUri, int maximumBytes) : DelegatingChatClient(inner)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var blocks = new Dictionary<int, JsonElement>();
        var serverTools = new HashSet<int>();
        long total = 0;
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false))
        {
            if (update.RawRepresentation is MinimalStreamEvent item && item.Index is { } index)
            {
                if (item.Type == "content_block_start" && item.Data.TryGetProperty("content_block", out var block))
                {
                    var type = block.GetProperty("type").GetString();
                    if (type == "server_tool_use")
                    {
                        serverTools.Add(index);
                    }
                    if (type is "container_upload" or "code_execution_tool_result" or "bash_code_execution_tool_result"
                        or "text_editor_code_execution_tool_result")
                    {
                        blocks.Add(index, block.Clone());
                    }
                }
                // 服务端工具已在 Anthropic 执行，不能交给本地 ToolGateway 再执行。
                if (serverTools.Contains(index))
                {
                    for (var i = update.Contents.Count - 1; i >= 0; i--)
                    {
                        if (update.Contents[i] is FunctionCallContent)
                        {
                            update.Contents.RemoveAt(i);
                        }
                    }
                }
                if (item.Type == "content_block_stop")
                {
                    serverTools.Remove(index);
                    if (blocks.Remove(index, out var completed))
                    {
                        foreach (var fileId in GetFileIds(completed))
                        {
                            var data = await DownloadAsync(fileId, maximumBytes - total, cancellationToken)
                                .ConfigureAwait(false);
                            total += data.Data.Length;
                            update.Contents.Add(new ProviderOutputFileContent(data));
                        }
                    }
                }
            }
            yield return update;
        }
        if (blocks.Count != 0)
        {
            throw new InvalidDataException("Anthropic file result ended before content_block_stop.");
        }
    }

    private static IEnumerable<string> GetFileIds(JsonElement block)
    {
        var type = block.GetProperty("type").GetString();
        if (type is "container_upload" or "code_execution_output" or "bash_code_execution_output")
        {
            yield return block.GetProperty("file_id").GetString()
                ?? throw new InvalidDataException("Anthropic file result has no file_id.");
        }
        else if (block.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.Object)
            {
                foreach (var id in GetFileIds(content))
                {
                    yield return id;
                }
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                {
                    foreach (var id in GetFileIds(part))
                    {
                        yield return id;
                    }
                }
            }
        }
    }

    private async Task<DataContent> DownloadAsync(string fileId, long remaining, CancellationToken ct)
    {
        if (filesClient is null || baseUri is null)
        {
            throw new NotSupportedException("Anthropic file download is unavailable for the injected chat client.");
        }
        if (fileId.Length is 0 or > 256 || fileId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
        {
            throw new InvalidDataException("Invalid Anthropic file identifier.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        using var metadata = await GetAsync($"v1/files/{fileId}", token).ConfigureAwait(false);
        var metadataBytes = await ReadBoundedAsync(metadata.Content, 64 * 1024, null, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(metadataBytes);
        var root = document.RootElement;
        if (root.GetProperty("id").GetString() != fileId
            || !root.TryGetProperty("downloadable", out var downloadable) || downloadable.ValueKind != JsonValueKind.True)
        {
            throw new InvalidDataException("Anthropic file is not downloadable.");
        }
        var length = root.GetProperty("size_bytes").GetInt64();
        if (length < 0 || length > remaining)
        {
            throw new InvalidDataException("Anthropic generated files exceed the invocation quota.");
        }
        var mime = root.GetProperty("mime_type").GetString();
        if (string.IsNullOrWhiteSpace(mime) || mime.Length > 128
            || !System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(mime, out var parsed)
            || parsed.Parameters.Count != 0)
        {
            throw new InvalidDataException("Invalid Anthropic file MIME type.");
        }
        using var response = await GetAsync($"v1/files/{fileId}/content", token).ConfigureAwait(false);
        var responseMime = response.Content.Headers.ContentType?.MediaType;
        if (responseMime is not null && responseMime != "application/octet-stream"
            && !responseMime.Equals(mime, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Anthropic file MIME type mismatch.");
        }
        var bytes = await ReadBoundedAsync(response.Content, remaining, length, token).ConfigureAwait(false);
        return new DataContent(bytes, mime) { Name = root.GetProperty("filename").GetString() };
    }

    private async Task<HttpResponseMessage> GetAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri!, path));
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Add("anthropic-beta", "files-api-2025-04-14");
        var response = await filesClient!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new HttpRequestException("Anthropic file request failed.", null, status);
        }
        return response;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, long limit, long? expected,
        CancellationToken ct)
    {
        if (content.Headers.ContentLength is { } declared && (declared > limit || expected is { } size && declared != size))
        {
            throw new InvalidDataException("Anthropic file length exceeds quota or metadata length.");
        }
        await using var source = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var target = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (count > limit - target.Length || expected is { } length && count > length - target.Length)
            {
                throw new InvalidDataException("Anthropic file length exceeds quota or metadata length.");
            }
            await target.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        if (expected is { } actual && target.Length != actual)
        {
            throw new InvalidDataException("Anthropic file length mismatch.");
        }
        return target.ToArray();
    }
}
