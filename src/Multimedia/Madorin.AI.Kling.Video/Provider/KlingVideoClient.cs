using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.Kling.Video.Metadata;
using Madorin.AI.Kling.Video.Requests;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Kling.Video.Provider;

internal sealed record VideoJob(string TaskId, string Status, string? Url);

internal sealed class KlingVideoClient(HttpClient client, TimeSpan? pollInterval = null)
{
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    private const int MaxRedirects = 3;

    public static KlingTextVideoRequest MapText(VideoGenerationOptions options) => new(
        options.Prompt!, new KlingVideoSettings(options.Duration, options.Resolution, options.AspectRatio), Options(options));

    public static KlingImageVideoRequest MapImage(VideoGenerationOptions options, string? imageUrl)
    {
        var contents = new List<KlingContent>();
        if (options.Prompt is not null) contents.Add(new KlingContent("prompt", options.Prompt, null));
        contents.Add(new KlingContent("first_frame", null, imageUrl));
        return new(contents.ToArray(), new KlingVideoSettings(options.Duration, options.Resolution, null), Options(options));
    }

    public static JsonObject PreviewText(KlingTextVideoRequest request) =>
        JsonSerializer.SerializeToNode(request, KlingVideoJsonContext.Default.KlingTextVideoRequest)!.AsObject();

    public static JsonObject PreviewImage(KlingImageVideoRequest request)
    {
        var preview = JsonSerializer.SerializeToNode(request, KlingVideoJsonContext.Default.KlingImageVideoRequest)!.AsObject();
        if (preview["contents"] is JsonArray contents)
        {
            foreach (var item in contents)
            {
                if (item is JsonObject content && content["url"] is JsonValue value && value.TryGetValue<string>(out var text) &&
                    (text.Length > 256 || Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Query.Length > 0 || uri.UserInfo.Length > 0)))
                {
                    content["url"] = "[redacted]";
                }
            }
        }

        return preview;
    }

    public async Task<JsonObject> SubmitAsync(VideoGenerationOptions options, object request, SkillInvocation invocation)
    {
        invocation.ThrowIfCancellationRequested();
        var resource = (options.Task == "image-to-video" ? "image-to-video/" : "text-to-video/") + Uri.EscapeDataString(options.Model);
        using var response = await SendAsync(HttpMethod.Post, KlingEndpoints.Build(options.Endpoint, resource), options.ApiKey, request, invocation);
        var root = await ReadSuccessAsync(response, options.ApiKey, invocation);
        var data = Object(root, "data");
        var taskId = JsonProtocol.GetString(data, "id");
        var status = JsonProtocol.GetString(data, "status");
        if (!KlingTaskIds.IsValid(taskId) || taskId!.Contains(options.ApiKey, StringComparison.Ordinal) || status is not ("submitted" or "processing" or "succeeded" or "failed"))
        {
            throw new SkillException("provider_error", "可灵视频提交响应缺少有效任务信息。");
        }

        invocation.TaskId = taskId;
        invocation.RemoteStatus = status;
        invocation.RemoteCancellation = "unsupported";
        invocation.TaskContext = KlingVideoMetadata.TaskContext(taskId, options.Task, options.Model);
        RememberRequest(root, invocation, options.ApiKey);
        if (status == "failed") throw new SkillException("provider_error", "可灵视频任务提交后失败。");
        return TaskView(taskId, "submitted", null, invocation);
    }

    public async Task<VideoJob> QueryOnceAsync(Uri endpoint, string taskId, string apiKey, SkillInvocation invocation)
    {
        invocation.ThrowIfCancellationRequested();
        using var response = await SendAsync(HttpMethod.Get, KlingEndpoints.Build(endpoint, "tasks?task_ids=" + Uri.EscapeDataString(taskId)), apiKey, null, invocation);
        var root = await ReadSuccessAsync(response, apiKey, invocation);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new SkillException("provider_error", "可灵视频查询响应缺少 data 数组。");
        JsonElement? matched = null;
        foreach (var item in data.EnumerateArray())
        {
            if (JsonProtocol.GetString(item, "id") == taskId)
            {
                matched = item;
                break;
            }
        }

        if (matched is null) throw new SkillException("provider_error", "可灵视频查询结果没有匹配的任务。");
        var status = JsonProtocol.GetString(matched.Value, "status");
        if (status is not ("submitted" or "processing" or "succeeded" or "failed")) throw new SkillException("provider_error", "可灵返回了未知的视频状态。");
        invocation.RemoteStatus = status;
        RememberRequest(root, invocation, apiKey);
        string? url = status == "succeeded" ? SingleVideoUrl(matched.Value) : null;
        if (url is not null) EnsureHttps(new Uri(url, UriKind.Absolute));
        return new VideoJob(taskId, status, url);
    }

    public async Task<VideoJob> WaitAsync(Uri endpoint, string taskId, string apiKey, SkillInvocation invocation)
    {
        while (true)
        {
            var job = await QueryOnceAsync(endpoint, taskId, apiKey, invocation);
            if (job.Status is "succeeded" or "failed") return job;
            await Task.Delay(_pollInterval, invocation.CancellationToken);
        }
    }

    public async Task<JsonArray> DownloadAsync(VideoJob job, string outputDirectory, SkillInvocation invocation, VideoArtifactStore artifacts)
    {
        if (job.Status != "succeeded" || job.Url is null) throw new SkillException(job.Status is "submitted" or "processing" ? "task_not_ready" : "provider_error", "视频尚未完成，不能下载。");
        VideoValidator.EnsureTools();
        using var response = await SendDownloadAsync(job.Url, invocation.CancellationToken);
        if (!response.IsSuccessStatusCode) throw new SkillException("provider_error", "可灵视频下载失败。");
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null and not ("video/mp4" or "application/octet-stream")) throw new SkillException("provider_error", "可灵视频下载响应不是 MP4。");
        await using var stream = await response.Content.ReadAsStreamAsync(invocation.CancellationToken);
        var artifact = await artifacts.WriteAsync(stream, outputDirectory, invocation.CancellationToken);
        artifact["index"] = N(1);
        return new JsonArray((JsonNode)artifact);
    }

    public static JsonObject TaskView(string taskId, string status, JsonObject? error, SkillInvocation invocation) => Obj(
        ("schema_version", S(SkillProtocol.ContractVersion)), ("response_id", S(invocation.ResponseId)), ("status", S(status)),
        ("task_id", S(taskId)), ("task_context", invocation.TaskContext?.DeepClone()),
        ("provider_metadata", SkillResponses.ProviderMetadata("kling", invocation)), ("error", error),
        ("ignored_parameters", invocation.IgnoredParameters.DeepClone()), ("converted_parameters", invocation.ConvertedParameters.DeepClone()));

    private static KlingVideoOptions? Options(VideoGenerationOptions options) =>
        options.WatermarkEnabled is null ? null : new KlingVideoOptions(new KlingWatermark(options.WatermarkEnabled.Value));

    private async Task<JsonElement> ReadSuccessAsync(HttpResponseMessage response, string apiKey, SkillInvocation invocation)
    {
        var body = await response.Content.ReadAsStringAsync(invocation.CancellationToken);
        if (!response.IsSuccessStatusCode) throw KlingErrors.Map(response.StatusCode, body, apiKey);
        using var document = KlingErrors.Parse(body);
        var code = KlingErrors.ReadCode(document.RootElement);
        if (code is not null and not "0") throw KlingErrors.Map(response.StatusCode, body, apiKey);
        RememberRequest(document.RootElement, invocation, apiKey);
        return document.RootElement.Clone();
    }

    private static JsonElement Object(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object) throw new SkillException("provider_error", "可灵视频响应缺少 " + name + "。");
        return value;
    }

    private static string SingleVideoUrl(JsonElement item)
    {
        if (!item.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Array) throw new SkillException("provider_error", "可灵完成响应缺少 outputs。");
        string? url = null;
        foreach (var output in outputs.EnumerateArray())
        {
            if (JsonProtocol.GetString(output, "type") != "video") continue;
            if (url is not null) throw new SkillException("provider_error", "可灵完成响应没有唯一视频地址。");
            url = JsonProtocol.GetString(output, "url");
        }

        if (string.IsNullOrWhiteSpace(url)) throw new SkillException("provider_error", "可灵完成响应缺少视频地址。");
        return url;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri endpoint, string apiKey, object? request, SkillInvocation invocation)
    {
        using var message = new HttpRequestMessage(method, endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (request is KlingTextVideoRequest text) message.Content = JsonContent.Create(text, KlingVideoJsonContext.Default.KlingTextVideoRequest);
        else if (request is KlingImageVideoRequest image) message.Content = JsonContent.Create(image, KlingVideoJsonContext.Default.KlingImageVideoRequest);
        return await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, invocation.CancellationToken);
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
            if (location is null || redirect >= MaxRedirects) throw new SkillException("provider_error", "可灵视频 URL 重定向无效或超过限制。");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
    }

    private static void EnsureHttps(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new SkillException("provider_error", "可灵视频 URL 必须为不含用户信息的 HTTPS 地址。");
        }
    }

    private static void RememberRequest(JsonElement root, SkillInvocation invocation, string apiKey)
    {
        var requestId = JsonProtocol.GetString(root, "request_id");
        if (KlingErrors.IsSafe(requestId, apiKey, 256)) invocation.ProviderRequestId = requestId;
    }
}

internal static class KlingEndpoints
{
    public static Uri Build(Uri root, string resource)
    {
        var path = root.AbsolutePath.TrimEnd('/');
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
