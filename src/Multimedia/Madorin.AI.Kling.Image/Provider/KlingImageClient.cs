using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.Kling.Image.Metadata;
using Madorin.AI.Kling.Image.Requests;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Kling.Image.Provider;

internal sealed record ImageJob(string TaskId, string Status, IReadOnlyList<ImageResult> Images);

internal sealed record ImageResult(int Index, string Url);

internal sealed class KlingImageClient(HttpClient client, TimeSpan? pollInterval = null)
{
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    private const int MaxImageBytes = 50 * 1024 * 1024;
    private const int MaxRedirects = 3;

    public static KlingImageRequest Map(ImageGenerationOptions options) => new(
        options.Model, options.Prompt, options.NegativePrompt, options.Resolution, options.Count, options.AspectRatio,
        options.WatermarkEnabled is null ? null : new KlingWatermark(options.WatermarkEnabled.Value));

    public static JsonObject Preview(KlingImageRequest request) =>
        JsonSerializer.SerializeToNode(request, KlingImageJsonContext.Default.KlingImageRequest)!.AsObject();

    public async Task<JsonObject> SubmitAsync(ImageGenerationOptions options, KlingImageRequest request, SkillInvocation invocation)
    {
        invocation.ThrowIfCancellationRequested();
        using var response = await SendAsync(HttpMethod.Post, KlingEndpoints.Build(options.Endpoint, "v1/images/generations"), options.ApiKey, request, invocation);
        var root = await ReadSuccessAsync(response, options.ApiKey, invocation);
        var data = Object(root, "data");
        var taskId = JsonProtocol.GetString(data, "task_id");
        var status = JsonProtocol.GetString(data, "task_status");
        if (!KlingTaskIds.IsValid(taskId) || taskId!.Contains(options.ApiKey, StringComparison.Ordinal) || status is not ("submitted" or "processing" or "succeed" or "failed"))
        {
            throw new SkillException("provider_error", "可灵图片提交响应缺少有效任务信息。");
        }

        invocation.TaskId = taskId;
        invocation.RemoteStatus = status;
        invocation.RemoteCancellation = "unsupported";
        invocation.TaskContext = KlingImageMetadata.TaskContext(taskId, options.Model, options.Count);
        RememberRequest(root, invocation, options.ApiKey);
        if (status == "failed") throw new SkillException("provider_error", "可灵图片任务提交后失败。");
        return TaskView(taskId, "submitted", null, invocation);
    }

    public async Task<ImageJob> QueryOnceAsync(Uri endpoint, string taskId, string apiKey, int expectedCount, SkillInvocation invocation)
    {
        invocation.ThrowIfCancellationRequested();
        using var response = await SendAsync(HttpMethod.Get, KlingEndpoints.Build(endpoint, "v1/images/generations/" + Uri.EscapeDataString(taskId)), apiKey, null, invocation);
        var root = await ReadSuccessAsync(response, apiKey, invocation);
        var data = Object(root, "data");
        var status = JsonProtocol.GetString(data, "task_status");
        if (status is not ("submitted" or "processing" or "succeed" or "failed")) throw new SkillException("provider_error", "可灵返回了未知的图片状态。");
        invocation.RemoteStatus = status;
        RememberRequest(root, invocation, apiKey);
        var images = status == "succeed" ? ReadImages(data, expectedCount) : [];
        return new ImageJob(taskId, status, images);
    }

    public async Task<ImageJob> WaitAsync(Uri endpoint, string taskId, string apiKey, int expectedCount, SkillInvocation invocation)
    {
        while (true)
        {
            var job = await QueryOnceAsync(endpoint, taskId, apiKey, expectedCount, invocation);
            if (job.Status is "succeed" or "failed") return job;
            await Task.Delay(_pollInterval, invocation.CancellationToken);
        }
    }

    public async Task<JsonArray> DownloadAsync(ImageJob job, string outputDirectory, SkillInvocation invocation, ArtifactStore artifacts)
    {
        if (job.Status != "succeed") throw new SkillException(job.Status is "submitted" or "processing" ? "task_not_ready" : "provider_error", "图片尚未完成，不能下载。");
        var results = new JsonArray();
        foreach (var image in job.Images.OrderBy(item => item.Index))
        {
            var bytes = await DownloadBytesAsync(image.Url, invocation.CancellationToken);
            var format = DetectFormat(bytes);
            results.Add((JsonNode)await artifacts.WriteImageAsync(bytes, format, outputDirectory, image.Index, invocation.CancellationToken));
        }

        return results;
    }

    public static JsonObject TaskView(string taskId, string status, JsonObject? error, SkillInvocation invocation) => Obj(
        ("schema_version", S(SkillProtocol.ContractVersion)), ("response_id", S(invocation.ResponseId)), ("status", S(status)),
        ("task_id", S(taskId)), ("task_context", invocation.TaskContext?.DeepClone()),
        ("provider_metadata", SkillResponses.ProviderMetadata("kling", invocation)), ("error", error),
        ("ignored_parameters", invocation.IgnoredParameters.DeepClone()), ("converted_parameters", invocation.ConvertedParameters.DeepClone()));

    private async Task<JsonElement> ReadSuccessAsync(HttpResponseMessage response, string apiKey, SkillInvocation invocation)
    {
        var body = await response.Content.ReadAsStringAsync(invocation.CancellationToken);
        if (!response.IsSuccessStatusCode) throw KlingErrors.Map(response.StatusCode, body, apiKey);
        var document = KlingErrors.Parse(body);
        var root = document.RootElement;
        var code = KlingErrors.ReadCode(root);
        if (code is not null and not "0") throw KlingErrors.Map(response.StatusCode, body, apiKey);
        RememberRequest(root, invocation, apiKey);
        return root.Clone();
    }

    private static JsonElement Object(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object) throw new SkillException("provider_error", "可灵图片响应缺少 " + name + "。");
        return value;
    }

    private static List<ImageResult> ReadImages(JsonElement data, int expectedCount)
    {
        if (!data.TryGetProperty("task_result", out var result) || result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array || images.GetArrayLength() != expectedCount)
        {
            throw new SkillException("provider_error", "可灵完成响应的图片数量与预期槽位不一致。");
        }

        var parsed = new List<ImageResult>();
        var seen = new HashSet<int>();
        foreach (var image in images.EnumerateArray())
        {
            if (!image.TryGetProperty("index", out var indexValue) || indexValue.ValueKind != JsonValueKind.Number || !indexValue.TryGetInt32(out var index))
            {
                throw new SkillException("provider_error", "可灵图片结果缺少稳定槽位。");
            }

            var url = JsonProtocol.GetString(image, "url");
            if (string.IsNullOrWhiteSpace(url) || !seen.Add(index) || index < 0 || index >= expectedCount)
            {
                throw new SkillException("provider_error", "可灵图片结果槽位或地址无效。");
            }

            parsed.Add(new ImageResult(index + 1, url));
        }

        return parsed;
    }

    private async Task<byte[]> DownloadBytesAsync(string address, CancellationToken cancellationToken)
    {
        using var response = await SendDownloadAsync(address, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new SkillException("provider_error", "可灵图片下载失败。");
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null and not ("image/png" or "image/jpeg")) throw new SkillException("provider_error", "可灵图片下载响应不是 PNG 或 JPEG。");
        if (response.Content.Headers.ContentLength is > MaxImageBytes) throw new SkillException("provider_error", "可灵图片超过 50 MiB 下载上限。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (destination.Length + count > MaxImageBytes) throw new SkillException("provider_error", "可灵图片超过 50 MiB 下载上限。");
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
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirect >= MaxRedirects) throw new SkillException("provider_error", "可灵图片 URL 重定向无效或超过限制。");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri endpoint, string apiKey, KlingImageRequest? request, SkillInvocation invocation)
    {
        using var message = new HttpRequestMessage(method, endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (request is not null) message.Content = JsonContent.Create(request, KlingImageJsonContext.Default.KlingImageRequest);
        return await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, invocation.CancellationToken);
    }

    private static void EnsureHttps(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new SkillException("provider_error", "可灵图片 URL 必须为不含用户信息的 HTTPS 地址。");
        }
    }

    private static void RememberRequest(JsonElement root, SkillInvocation invocation, string apiKey)
    {
        var requestId = JsonProtocol.GetString(root, "request_id");
        if (KlingErrors.IsSafe(requestId, apiKey, 256)) invocation.ProviderRequestId = requestId;
    }

    private static string DetectFormat(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "png";
        if (bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255) return "jpeg";
        throw new SkillException("provider_error", "可灵图片容器无法识别。");
    }
}

internal static class KlingEndpoints
{
    public static Uri Build(Uri root, string resource)
    {
        var path = root.AbsolutePath.TrimEnd('/');
        if (resource.StartsWith("v1/", StringComparison.Ordinal) && path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            resource = resource[3..];
        }

        return new Uri(root.GetLeftPart(UriPartial.Authority) + path + "/" + resource.TrimStart('/'));
    }
}

internal static class KlingErrors
{
    public static JsonDocument Parse(string body)
    {
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { throw new SkillException("provider_error", "可灵响应不是有效 JSON。"); }
    }

    public static string? ReadCode(JsonElement root)
    {
        if (!root.TryGetProperty("code", out var code)) return null;
        if (code.ValueKind == JsonValueKind.Number && code.TryGetInt64(out var number)) return number.ToString(CultureInfo.InvariantCulture);
        return code.ValueKind == JsonValueKind.String ? code.GetString() : null;
    }

    public static SkillException Map(HttpStatusCode status, string body, string apiKey)
    {
        string? code = null;
        try
        {
            using var document = Parse(body);
            code = SafeCode(ReadCode(document.RootElement), apiKey);
            if (code is null && document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                code = SafeCode(JsonProtocol.GetString(error, "code"), apiKey);
            }
        }
        catch (SkillException)
        {
        }

        var normalized = code switch
        {
            "1000" or "1001" or "1002" or "1003" or "1004" => "authentication_failed",
            "1101" or "1102" => "insufficient_balance",
            "1301" => "content_policy_violation",
            "1100" or "1302" or "1303" => "rate_limit_exceeded",
            "5002" => "timeout",
            _ => status switch
            {
                HttpStatusCode.Unauthorized => "authentication_failed",
                HttpStatusCode.PaymentRequired => "insufficient_balance",
                HttpStatusCode.TooManyRequests => "rate_limit_exceeded",
                HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => "timeout",
                _ => "provider_error"
            }
        };
        return new SkillException(normalized, "可灵请求未能完成。", code);
    }

    public static bool IsSafe(string? value, string? apiKey, int maxLength) =>
        value is { Length: > 0 } && value.Length <= maxLength && !value.Any(char.IsControl) &&
        (string.IsNullOrEmpty(apiKey) || !value.Contains(apiKey, StringComparison.Ordinal));

    private static string? SafeCode(string? code, string apiKey) =>
        code is not null && IsSafe(code, apiKey, 32) && code.All(char.IsAsciiDigit) ? code : null;
}
