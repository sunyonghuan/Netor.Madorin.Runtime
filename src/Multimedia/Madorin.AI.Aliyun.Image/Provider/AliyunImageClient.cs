using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http.Json;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;
using Madorin.AI.Multimedia.Core.CommandLine;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.Aliyun.Image.Requests;

namespace Madorin.AI.Aliyun.Image.Provider;

internal sealed class AliyunImageClient(HttpClient client)
{
    public static AliyunImageRequest Map(ImageGenerationOptions options) => new(
        options.Model,
        new AliyunInput([new AliyunMessage("user", [new AliyunMessagePart(options.Prompt)])]),
        new AliyunParameters(options.NegativePrompt, options.Size, options.ImageCount, options.PromptExtend, options.Watermark, options.Seed));

    public static JsonObject Preview(AliyunImageRequest request) => JsonSerializer.SerializeToNode(request, AliyunJsonContext.Default.AliyunImageRequest)!.AsObject();

    public async Task<JsonObject> ListModelsAsync(ModelListOptions options, SkillInvocation invocation)
    {
        invocation.ThrowIfCancellationRequested();
        var endpoint = new Uri(options.Endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/models");
        using var message = new HttpRequestMessage(HttpMethod.Get, endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, invocation.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(invocation.CancellationToken);
        if (!response.IsSuccessStatusCode) throw MapError(response.StatusCode, body);
        invocation.ProviderRequestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
        return ParseModels(body, invocation);
    }

    public async Task<JsonArray> GenerateAsync(ImageGenerationOptions options, AliyunImageRequest request, SkillInvocation invocation, ArtifactStore artifacts)
    {
        invocation.ThrowIfCancellationRequested();
        var endpoint = new Uri(options.Endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/services/aigc/multimodal-generation/generation");
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        message.Content = JsonContent.Create(request, AliyunJsonContext.Default.AliyunImageRequest);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, invocation.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(invocation.CancellationToken);
        if (!response.IsSuccessStatusCode) throw MapError(response.StatusCode, body);
        invocation.ProviderRequestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
        using var document = ParseJson(body);
        var root = document.RootElement;
        if (TryGetBusinessError(root, out var businessCode)) throw new SkillException("provider_error", "阿里云百炼返回业务错误。", businessCode);
        var urls = ExtractImageUrls(root);
        if (urls.Count != options.ImageCount) throw new SkillException("provider_error", "阿里云响应图片数量与请求不匹配。");
        var result = new JsonArray();
        for (var index = 0; index < urls.Count; index++)
        {
            using var download = await SendImageDownloadAsync(urls[index], invocation.CancellationToken);
            if (!download.IsSuccessStatusCode) throw MapError(download.StatusCode, string.Empty);
            var mediaType = download.Content.Headers.ContentType?.MediaType;
            if (mediaType is not ("image/png" or "image/jpeg"))
                throw new SkillException("provider_error", "阿里云图片下载响应不是支持的图片类型。");
            if (download.Content.Headers.ContentLength is > MaxImageBytes)
                throw new SkillException("provider_error", "阿里云图片超过 50 MiB 下载上限。");
            await using var stream = await download.Content.ReadAsStreamAsync(invocation.CancellationToken);
            var bytes = await ReadBoundedAsync(stream, invocation.CancellationToken);
            var format = mediaType == "image/jpeg" ? "jpeg" : "png";
            var artifact = await artifacts.WriteImageAsync(bytes, format, options.OutputDirectory!, index + 1, invocation.CancellationToken);
            result.Add((JsonNode)artifact);
        }
        return result;
    }

    private const int MaxImageBytes = 50 * 1024 * 1024;
    private const int MaxRedirects = 3;

    private async Task<HttpResponseMessage> SendImageDownloadAsync(string address, CancellationToken cancellationToken)
    {
        var current = new Uri(address, UriKind.Absolute);
        for (var redirect = 0; ; redirect++)
        {
            EnsureHttps(current);
            using var message = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirect >= MaxRedirects) throw new SkillException("provider_error", "阿里云图片 URL 重定向无效或超过限制。");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
    }

    private static void EnsureHttps(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new SkillException("provider_error", "阿里云图片 URL 必须为 HTTPS 且不得包含用户信息。");
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, CancellationToken cancellationToken)
    {
        await using var destination = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (destination.Length + count > MaxImageBytes) throw new SkillException("provider_error", "阿里云图片超过 50 MiB 下载上限。");
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        return destination.ToArray();
    }


    private static JsonDocument ParseJson(string body)
    {
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { throw new SkillException("provider_error", "阿里云响应不是有效 JSON。"); }
    }

    private static List<string> ExtractImageUrls(JsonElement root)
    {
        var urls = new List<string>();
        if (!root.TryGetProperty("output", out var output) || !output.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) throw new SkillException("provider_error", "阿里云响应缺少 output.choices。");
        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in content.EnumerateArray())
            {
                if (JsonProtocol.GetString(item, "type") != "image") continue;
                var url = JsonProtocol.GetString(item, "image");
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps) urls.Add(uri.ToString());
            }
        }
        if (urls.Count == 0) throw new SkillException("provider_error", "阿里云响应未包含有效 HTTPS 图片 URL。");
        return urls;
    }

    private static JsonObject ParseModels(string body, SkillInvocation invocation)
    {
        using var document = ParseJson(body);
        if (!document.RootElement.TryGetProperty("models", out var entries) || entries.ValueKind != JsonValueKind.Array) throw new SkillException("provider_error", "阿里云模型目录缺少 models 数组。");
        var models = new JsonArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            var id = JsonProtocol.GetString(entry, "id") ?? JsonProtocol.GetString(entry, "name");
            if (!ModelIds.IsValid(id) || !ids.Add(id!)) throw new SkillException("provider_error", "阿里云模型目录包含无效或重复 ID。");
            models.Add(Obj(("id", S(id)), ("name", S(id)), ("tasks", null)));
        }
        return Obj(("schema_version", S(SkillProtocol.ContractVersion)), ("response_id", S(invocation.ResponseId)), ("provider", S("aliyun")), ("model_source", S("api")), ("models", models));
    }

    private static SkillException MapError(HttpStatusCode status, string body)
    {
        var code = TryGetErrorCode(body);
        var normalized = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "authentication_failed",
            HttpStatusCode.TooManyRequests => "rate_limit_exceeded",
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => "timeout",
            _ => "provider_error"
        };
        return new SkillException(normalized, "阿里云百炼请求未能完成。", code);
    }

    private static string? TryGetErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return GetErrorCode(document.RootElement);
        }
        catch (JsonException) { return null; }
    }

    private static bool TryGetBusinessError(JsonElement root, out string? code)
    {
        code = GetErrorCode(root);
        if (code is not null) return true;
        if (root.TryGetProperty("output", out var output))
        {
            code = GetErrorCode(output);
            if (code is not null) return true;
        }
        return false;
    }

    private static string? GetErrorCode(JsonElement element)
    {
        var code = JsonProtocol.GetString(element, "code");
        return !string.IsNullOrWhiteSpace(code) && (element.TryGetProperty("message", out _) || element.TryGetProperty("request_id", out _) || element.TryGetProperty("requestId", out _)) ? code : null;
    }
}
