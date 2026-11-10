using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.Zhipu.Video.Metadata;
using Madorin.AI.Zhipu.Video.Requests;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Zhipu.Video.Provider;

internal sealed record VideoJob(string TaskId, string Status, string? Url, string? Model);

internal sealed class ZhipuVideoClient(HttpClient client, TimeSpan? pollInterval = null)
{
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    private const int MaxRedirects = 3;

    public static ZhipuVideoRequest Map(VideoGenerationOptions options, string? imageUrl) => new(
        options.Model, options.Prompt, imageUrl, options.Quality, options.WithAudio, options.Size,
        options.Fps, options.Duration, options.WatermarkEnabled, options.RequestId, options.UserId);

    public static JsonObject Preview(ZhipuVideoRequest request)
    {
        var preview = JsonSerializer.SerializeToNode(request, ZhipuVideoJsonContext.Default.ZhipuVideoRequest)!.AsObject();
        if (preview["image_url"] is JsonValue value && value.TryGetValue<string>(out var text) &&
            Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Query.Length > 0 || uri.UserInfo.Length > 0))
        {
            preview["image_url"] = "[redacted]";
        }

        return preview;
    }

    public async Task<JsonObject> SubmitAsync(VideoGenerationOptions options, ZhipuVideoRequest request, SkillInvocation invocation)
    {
        invocation.ThrowIfCancellationRequested();
        using var response = await SendAsync(HttpMethod.Post, ZhipuEndpoints.Build(options.Endpoint, "videos/generations"), options.ApiKey, request, invocation);
        var body = await response.Content.ReadAsStringAsync(invocation.CancellationToken);
        if (!response.IsSuccessStatusCode) throw ZhipuErrors.Map(response.StatusCode, body, options.ApiKey);
        using var document = ZhipuErrors.Parse(body);
        var root = document.RootElement;
        var taskId = JsonProtocol.GetString(root, "id");
        var status = JsonProtocol.GetString(root, "task_status");
        if (!ZhipuVideoTaskIds.IsValid(taskId) || taskId!.Contains(options.ApiKey, StringComparison.Ordinal) || status is not ("PROCESSING" or "SUCCESS" or "FAIL"))
        {
            throw new SkillException("provider_error", "智谱视频提交响应缺少有效任务信息。");
        }

        invocation.TaskId = taskId;
        invocation.RemoteStatus = status;
        invocation.RemoteCancellation = "unsupported";
        invocation.TaskContext = ZhipuVideoMetadata.TaskContext(taskId, options.Task, options.Model);
        RememberProviderRequest(root, invocation, options.ApiKey);
        if (status == "FAIL") throw new SkillException("provider_error", "智谱视频任务提交后失败。");
        return TaskView(taskId, "submitted", null, invocation);
    }

    public async Task<VideoJob> QueryOnceAsync(Uri endpoint, string taskId, string apiKey, SkillInvocation invocation)
    {
        invocation.ThrowIfCancellationRequested();
        using var response = await SendAsync(HttpMethod.Get, ZhipuEndpoints.Build(endpoint, "async-result/" + Uri.EscapeDataString(taskId)), apiKey, null, invocation);
        var body = await response.Content.ReadAsStringAsync(invocation.CancellationToken);
        if (!response.IsSuccessStatusCode) throw ZhipuErrors.Map(response.StatusCode, body, apiKey);
        using var document = ZhipuErrors.Parse(body);
        var root = document.RootElement;
        var status = JsonProtocol.GetString(root, "task_status");
        if (status is not ("PROCESSING" or "SUCCESS" or "FAIL")) throw new SkillException("provider_error", "智谱返回了未知的视频状态。");
        invocation.RemoteStatus = status;
        RememberProviderRequest(root, invocation, apiKey);
        var model = JsonProtocol.GetString(root, "model");
        if (model is not null && invocation.Model is not null && model != invocation.Model)
        {
            throw new SkillException("provider_error", "远端任务模型与原任务上下文不一致。");
        }

        string? url = null;
        if (status == "SUCCESS")
        {
            url = SingleVideoUrl(root);
            EnsureHttps(new Uri(url, UriKind.Absolute));
        }

        return new VideoJob(taskId, status, url, model);
    }

    public async Task<VideoJob> WaitAsync(Uri endpoint, string taskId, string apiKey, SkillInvocation invocation)
    {
        while (true)
        {
            var job = await QueryOnceAsync(endpoint, taskId, apiKey, invocation);
            if (job.Status is "SUCCESS" or "FAIL") return job;
            await Task.Delay(_pollInterval, invocation.CancellationToken);
        }
    }

    public async Task<JsonArray> DownloadAsync(VideoJob job, string outputDirectory, SkillInvocation invocation, VideoArtifactStore artifacts)
    {
        if (job.Status != "SUCCESS" || job.Url is null) throw new SkillException(job.Status == "PROCESSING" ? "task_not_ready" : "provider_error", "视频尚未完成，不能下载。");
        VideoValidator.EnsureTools();
        using var response = await SendDownloadAsync(job.Url, invocation.CancellationToken);
        if (!response.IsSuccessStatusCode) throw new SkillException("provider_error", "智谱视频下载失败。");
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null and not ("video/mp4" or "application/octet-stream"))
        {
            throw new SkillException("provider_error", "智谱视频下载响应不是 MP4。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(invocation.CancellationToken);
        var artifact = await artifacts.WriteAsync(stream, outputDirectory, invocation.CancellationToken);
        artifact["index"] = N(1);
        return new JsonArray((JsonNode)artifact);
    }

    public static JsonObject TaskView(string taskId, string status, JsonObject? error, SkillInvocation invocation) => Obj(
        ("schema_version", S(SkillProtocol.ContractVersion)), ("response_id", S(invocation.ResponseId)), ("status", S(status)),
        ("task_id", S(taskId)), ("task_context", invocation.TaskContext?.DeepClone()),
        ("provider_metadata", SkillResponses.ProviderMetadata("zhipu", invocation)), ("error", error),
        ("ignored_parameters", invocation.IgnoredParameters.DeepClone()), ("converted_parameters", invocation.ConvertedParameters.DeepClone()));

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri endpoint, string apiKey, ZhipuVideoRequest? request, SkillInvocation invocation)
    {
        using var message = new HttpRequestMessage(method, endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (request is not null) message.Content = JsonContent.Create(request, ZhipuVideoJsonContext.Default.ZhipuVideoRequest);
        return await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, invocation.CancellationToken);
    }

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
            if (location is null || redirect >= MaxRedirects) throw new SkillException("provider_error", "智谱视频 URL 重定向无效或超过限制。");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
    }

    private static string SingleVideoUrl(JsonElement root)
    {
        if (!root.TryGetProperty("video_result", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() != 1)
        {
            throw new SkillException("provider_error", "智谱完成响应没有唯一视频地址。");
        }

        var url = JsonProtocol.GetString(results[0], "url");
        if (string.IsNullOrWhiteSpace(url)) throw new SkillException("provider_error", "智谱完成响应缺少视频地址。");
        return url;
    }

    private static void EnsureHttps(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new SkillException("provider_error", "智谱视频 URL 必须为不含用户信息的 HTTPS 地址。");
        }
    }

    private static void RememberProviderRequest(JsonElement root, SkillInvocation invocation, string apiKey)
    {
        var requestId = JsonProtocol.GetString(root, "request_id");
        if (ZhipuErrors.IsSafe(requestId, apiKey, 256)) invocation.ProviderRequestId = requestId;
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
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { throw new SkillException("provider_error", "智谱响应不是有效 JSON。"); }
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
        catch (SkillException) { }

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
