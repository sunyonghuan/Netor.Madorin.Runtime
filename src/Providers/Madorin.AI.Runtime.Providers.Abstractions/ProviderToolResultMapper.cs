using System.Text.Json;
using Microsoft.Extensions.AI;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;

namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>Maps tool results without silently presenting Blob references as file contents.</summary>
public static class ProviderToolResultMapper
{
    /// <summary>将工具附件解析为 SDK 原生内容块，不把附件序列化到工具结果文本。</summary>
    public static async ValueTask<object> ToContentAsync(
        ToolResultContentBlock toolResult,
        Func<BlobReference, CancellationToken, ValueTask<DataContent>> resolveBlob,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(toolResult);
        ArgumentNullException.ThrowIfNull(resolveBlob);
        ct.ThrowIfCancellationRequested();
        if (!ContainsBlob(toolResult.Content))
        {
            return ToTextOrJson(toolResult);
        }

        var content = new List<AIContent>();
        await AddContentAsync(toolResult.Content, content, resolveBlob, ct).ConfigureAwait(false);
        return content;
    }

    private static async ValueTask AddContentAsync(
        ContentBlock[] blocks,
        List<AIContent> content,
        Func<BlobReference, CancellationToken, ValueTask<DataContent>> resolveBlob,
        CancellationToken ct)
    {
        foreach (var block in blocks)
        {
            ct.ThrowIfCancellationRequested();
            switch (block)
            {
                case TextContentBlock text:
                    content.Add(new TextContent(text.Text));
                    break;
                case BlobRefContentBlock blob:
                    // P0 只开放已核验的图片 codec；其他媒体在后续步骤逐类接入。
                    if (!blob.Blob.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new RuntimeProviderException(new RuntimeError(
                            RuntimeErrorCodes.ProviderProtocolError, "protocol",
                            "No tool-result attachment codec is configured for this media type.",
                            false, null, Guid.NewGuid().ToString("N")));
                    }

                    content.Add(await resolveBlob(blob.Blob, ct).ConfigureAwait(false));
                    break;
                case ToolResultContentBlock nested:
                    await AddContentAsync(nested.Content, content, resolveBlob, ct).ConfigureAwait(false);
                    break;
                default:
                    throw new NotSupportedException("Unsupported content inside a tool attachment result.");
            }
        }
    }

    /// <summary>Chat 协议的工具回执保留原 CallId，图片延后到整组工具回执之后的用户消息。</summary>
    public static ChatMessage[] ProjectForChatCompletions(ChatMessage[] messages)
    {
        var output = new List<ChatMessage>(messages.Length);
        var attachments = new List<AIContent>();
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.Tool && attachments.Count > 0)
            {
                output.Add(new ChatMessage(ChatRole.User, attachments));
                attachments = [];
            }

            if (message.Role != ChatRole.Tool)
            {
                output.Add(message);
                continue;
            }

            var projected = new List<AIContent>(message.Contents.Count);
            foreach (var content in message.Contents)
            {
                if (content is FunctionResultContent { Result: IEnumerable<AIContent> parts } result)
                {
                    var values = parts.ToArray();
                    var text = string.Join("\n", values.OfType<TextContent>().Select(static part => part.Text));
                    projected.Add(new FunctionResultContent(result.CallId,
                        string.IsNullOrEmpty(text) ? "Attachments follow in the next user message." : text));
                    attachments.Add(new TextContent($"Attachments for tool call '{result.CallId}':"));
                    attachments.AddRange(values.OfType<DataContent>());
                }
                else
                {
                    projected.Add(content);
                }
            }

            output.Add(new ChatMessage(message.Role, projected));
        }

        if (attachments.Count > 0)
        {
            output.Add(new ChatMessage(ChatRole.User, attachments));
        }

        return [.. output];
    }

    public static object ToTextOrJson(ToolResultContentBlock toolResult)
    {
        ArgumentNullException.ThrowIfNull(toolResult);
        if (ContainsBlob(toolResult.Content))
        {
            throw new NotSupportedException(
                "This Provider route does not yet support Blob attachments inside tool results.");
        }

        if (toolResult.Content is [TextContentBlock text])
        {
            return text.Text;
        }

        if (toolResult.Content.All(static block => block is TextContentBlock))
        {
            return string.Join("\n", toolResult.Content.Cast<TextContentBlock>().Select(static block => block.Text));
        }

        return JsonSerializer.SerializeToElement(
            toolResult.Content,
            RuntimeJsonContext.Default.ContentBlockArray);
    }

    private static bool ContainsBlob(IEnumerable<ContentBlock> content)
    {
        foreach (var block in content)
        {
            if (block is BlobRefContentBlock
                || block is ToolResultContentBlock nested && ContainsBlob(nested.Content))
            {
                return true;
            }
        }

        return false;
    }
}
