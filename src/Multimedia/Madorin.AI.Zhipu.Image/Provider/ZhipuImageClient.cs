using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.Zhipu.Image.Requests;

namespace Madorin.AI.Zhipu.Image.Provider;

internal sealed class ZhipuImageClient(HttpClient client)
{
    private const int MaxImageBytes = 50 * 1024 * 1024;
    private const int MaxRedirects = 3;

    public static ZhipuImageRequest Map(ImageGenerationOptions options) => new(
        options.Model, options.Prompt, options.Size, options.Quality, options.WatermarkEnabled, options.UserId);

    public static JsonObject Preview(ZhipuImageRequest request) =>
        JsonSerializer.SerializeToNode(request, ZhipuImageJsonContext.Default.ZhipuImageRequest)!.AsObject();

    public async Task<JsonArray> GenerateAsync(ImageGenerationOptions options, ZhipuImageRequest request, SkillInvocation invocation, ArtifactStore artifacts)
    {
        invocation.ThrowIfCancellationRequested();
        using var message = new HttpRequestMessage(HttpMethod.Post, ZhipuEndpoints.Build(options.Endpoint, "images/generations"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        message.Content = JsonContent.Create(request, ZhipuImageJsonContext.Default.ZhipuImageRequest);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, invocation.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(invocation.CancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw ZhipuErrors.Map(response.StatusCode, body, options.ApiKey);
        }

        ZhipuErrors.CaptureTrace(response, invocation, options.ApiKey);
        using var document = ZhipuErrors.Parse(body);
        var urls = ExtractUrls(document.RootElement);
        if (urls.Count != 1)
        {
            throw new SkillException("provider_error", "智谱图片响应数量与当前单图契约不一致。");
        }

        var bytes = await DownloadAsync(urls[0], invocation.CancellationToken);
        var format = DetectFormat(bytes);
        var artifact = await artifacts.WriteImageAsync(bytes, format, options.OutputDirectory!, 1, invocation.CancellationToken);
        return new JsonArray((JsonNode)artifact);
    }

    private async Task<byte[]> DownloadAsync(string address, CancellationToken cancellationToken)
    {
        using var response = await SendDownloadAsync(address, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new SkillException("provider_error", "智谱图片下载失败。");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null and not ("image/png" or "image/jpeg" or "image/webp"))
        {
            throw new SkillException("provider_error", "智谱图片下载响应不是支持的图片类型。");
        }

        if (response.Content.Headers.ContentLength is > MaxImageBytes)
        {
            throw new SkillException("provider_error", "智谱图片超过 50 MiB 下载上限。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (destination.Length + count > MaxImageBytes)
            {
                throw new SkillException("provider_error", "智谱图片超过 50 MiB 下载上限。");
            }

            destination.Write(buffer, 0, count);
        }

        return destination.ToArray();
    }

    // 官方未公布结果主机白名单，因此不发明主机列表；只限制协议、用户信息和重定向次数。
    private async Task<HttpResponseMessage> SendDownloadAsync(string address, CancellationToken cancellationToken)
    {
        var current = new Uri(address, UriKind.Absolute);
        for (var redirect = 0; ; redirect++)
        {
            EnsureHttps(current);
            using var message = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirect >= MaxRedirects)
            {
                throw new SkillException("provider_error", "智谱图片 URL 重定向无效或超过限制。");
            }

            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
    }

    private static void EnsureHttps(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new SkillException("provider_error", "智谱图片 URL 必须为不含用户信息的 HTTPS 地址。");
        }
    }

    private static List<string> ExtractUrls(JsonElement root)
    {
        if (root.TryGetProperty("error", out _))
        {
            throw new SkillException("provider_error", "智谱图片响应包含错误对象。");
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new SkillException("provider_error", "智谱图片响应缺少 data。");
        }

        var urls = new List<string>();
        foreach (var item in data.EnumerateArray())
        {
            var url = item.ValueKind == JsonValueKind.Object ? JsonProtocol.GetString(item, "url") : null;
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new SkillException("provider_error", "智谱图片响应缺少 URL。");
            }

            urls.Add(url);
        }

        return urls;
    }

    private static string DetectFormat(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            return "png";
        }

        if (bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255)
        {
            return "jpeg";
        }

        if (bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            return "webp";
        }

        throw new SkillException("provider_error", "智谱图片容器无法识别。");
    }
}

internal static class ZhipuEndpoints
{
    public static Uri Build(Uri root, string resource)
    {
        var path = root.AbsolutePath.TrimEnd('/');
        var prefix = path.EndsWith("/paas/v4", StringComparison.OrdinalIgnoreCase) ? "/" : "/paas/v4/";
        return new Uri(root.GetLeftPart(UriPartial.Authority) + path + prefix + resource);
    }
}

internal static class ZhipuErrors
{
    public static JsonDocument Parse(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new SkillException("provider_error", "智谱响应不是有效 JSON。");
        }
    }

    public static void CaptureTrace(HttpResponseMessage response, SkillInvocation invocation, string apiKey)
    {
        if (response.Headers.TryGetValues("x-request-id", out var values) && IsSafe(values.FirstOrDefault(), apiKey, 256))
        {
            invocation.ProviderRequestId = values.First();
        }
    }

    public static SkillException Map(HttpStatusCode status, string body, string apiKey)
    {
        string? code = null;
        try
        {
            using var document = Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                code = SafeCode(JsonProtocol.GetString(error, "code"), apiKey);
            }
        }
        catch (SkillException)
        {
        }

        var normalized = code switch
        {
            "1000" or "1001" or "1003" or "1005" => "authentication_failed",
            "1113" => "insufficient_balance",
            "1301" => "content_policy_violation",
            "1302" or "1305" or "1308" or "1309" or "1310" or "1311" or "1313" or "1314" or "1315" or "1316" or "1317" or "1318" or "1319" or "1320" or "1321" => "rate_limit_exceeded",
            _ => status switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "authentication_failed",
                HttpStatusCode.PaymentRequired => "insufficient_balance",
                HttpStatusCode.TooManyRequests => "rate_limit_exceeded",
                HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => "timeout",
                _ => "provider_error"
            }
        };
        return new SkillException(normalized, "智谱请求未能完成。", code);
    }

    public static bool IsSafe(string? value, string? apiKey, int maxLength) =>
        value is { Length: > 0 } && value.Length <= maxLength && !value.Any(char.IsControl) &&
        (string.IsNullOrEmpty(apiKey) || !value.Contains(apiKey, StringComparison.Ordinal));

    private static string? SafeCode(string? code, string apiKey) =>
        code is not null && IsSafe(code, apiKey, 32) && code.All(char.IsAsciiDigit) ? code : null;
}
