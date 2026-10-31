using System.Text;
using System.Text.Json;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Protocol;

namespace Madorin.AI.Grok.Video.Provider;

internal static class GrokVideoResponseReader
{
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<string> ReadBodyAsync(HttpResponseMessage response, SkillInvocation invocation)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new SkillException("provider_error", "Grok JSON 响应超过 4 MiB 本地上限。");
        }

        await using var source = await response.Content.ReadAsStreamAsync(invocation.CancellationToken);
        using var content = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(buffer, invocation.CancellationToken)) != 0)
        {
            if (content.Length + count > MaxResponseBytes)
            {
                throw new SkillException("provider_error", "Grok JSON 响应超过 4 MiB 本地上限。");
            }

            content.Write(buffer, 0, count);
        }

        try
        {
            return StrictUtf8.GetString(content.GetBuffer(), 0, (int)content.Length);
        }
        catch (DecoderFallbackException)
        {
            throw new SkillException("provider_error", "Grok 响应不是有效 UTF-8。");
        }
    }

    public static JsonDocument Parse(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new SkillException("provider_error", "Grok 响应不是有效 JSON。");
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object || JsonProtocol.HasDuplicateProperties(document.RootElement))
        {
            document.Dispose();
            throw new SkillException("provider_error", "Grok 响应必须为无重复字段的 JSON 对象。");
        }

        return document;
    }
}
