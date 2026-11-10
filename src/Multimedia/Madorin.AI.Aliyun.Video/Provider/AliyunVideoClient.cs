using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.Aliyun.Video.Metadata;
using Madorin.AI.Aliyun.Video.Requests;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Aliyun.Video.Provider;

internal sealed record VideoJob(string TaskId, string Status, string? Url, string? ProviderCode);

internal sealed class AliyunVideoClient(HttpClient client, TimeSpan? pollInterval = null)
{
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(15);
    private const int MaxRedirects = 3;

    public static AliyunVideoRequest Map(VideoGenerationOptions options, string? imageUrl)
    {
        AliyunMedia[]? media = null;
        string? audioUrl = null;
        if (options.Task == "image-to-video")
        {
            var items = new List<AliyunMedia>();
            if (imageUrl is not null) items.Add(new AliyunMedia("first_frame", imageUrl));
            if (options.AudioUrl is not null) items.Add(new AliyunMedia("driving_audio", options.AudioUrl));
            if (items.Count > 0) media = items.ToArray();
        }
        else
        {
            audioUrl = options.AudioUrl;
        }

        return new AliyunVideoRequest(options.Model, new AliyunVideoInput(options.Prompt, options.NegativePrompt, audioUrl, media),
            new AliyunVideoParameters(options.Resolution, options.Task == "text-to-video" ? options.AspectRatio : null, options.Duration, options.PromptExtend, options.Watermark, options.Seed));
    }

    public static JsonObject Preview(AliyunVideoRequest request)
    {
        var preview = JsonSerializer.SerializeToNode(request, AliyunVideoJsonContext.Default.AliyunVideoRequest)!.AsObject();
        Redact(preview);
        return preview;
    }

    public static JsonNode Redact(JsonNode node)
    {
        RedactInPlace(node);
        return node;
    }

    public async Task<JsonObject> SubmitAsync(VideoGenerationOptions options, AliyunVideoRequest request, SkillInvocation invocation)
    {
        invocation.ThrowIfCancellationRequested();
        using var response = await SendAsync(HttpMethod.Post, AliyunEndpoints.Submit(options.Endpoint), options.ApiKey, request, invocation, asyncHeader: true);
        var body = await response.Content.ReadAsStringAsync(invocation.CancellationToken);
        var job = ReadJob(response.StatusCode, body, options.ApiKey, invocation);
        invocation.TaskId = job.TaskId;
        invocation.RemoteStatus = job.Status;
        invocation.RemoteCancellation = "unsupported";
        invocation.TaskContext = AliyunVideoMetadata.TaskContext(job.TaskId, options.Task, options.Model);
        if (job.Status is not ("PENDING" or "RUNNING" or "SUCCEEDED"))
        {
            throw new SkillException("provider_error", FailureMessage(job.Status), job.ProviderCode);
        }

        return TaskView(job.TaskId, "submitted", null, invocation);
    }

    public async Task<VideoJob> QueryOnceAsync(Uri endpoint, string taskId, string apiKey, SkillInvocation invocation)
    {
        invocation.ThrowIfCancellationRequested();
        using var response = await SendAsync(HttpMethod.Get, AliyunEndpoints.Task(endpoint, taskId), apiKey, null, invocation, asyncHeader: false);
        var body = await response.Content.ReadAsStringAsync(invocation.CancellationToken);
        var job = ReadJob(response.StatusCode, body, apiKey, invocation);
        if (job.TaskId != taskId) throw new SkillException("provider_error", "查询响应的任务 ID 与请求不一致。");
        invocation.RemoteStatus = job.Status;
        if (job.Status == "SUCCEEDED")
        {
            if (!Uri.TryCreate(job.Url, UriKind.Absolute, out var videoUri)) throw new SkillException("provider_error", "阿里云完成响应缺少有效视频地址。");
            EnsureHttps(videoUri);
        }
        return job;
    }

    public async Task<VideoJob> WaitAsync(Uri endpoint, string taskId, string apiKey, SkillInvocation invocation)
    {
        while (true)
        {
            var job = await QueryOnceAsync(endpoint, taskId, apiKey, invocation);
            if (job.Status is not ("PENDING" or "RUNNING")) return job;
            await Task.Delay(_pollInterval, invocation.CancellationToken);
        }
    }

    public async Task<JsonArray> DownloadAsync(VideoJob job, string outputDirectory, SkillInvocation invocation, VideoArtifactStore artifacts)
    {
        if (job.Status is "PENDING" or "RUNNING") throw new SkillException("task_not_ready", "视频尚未完成。");
        if (job.Status != "SUCCEEDED" || job.Url is null) throw new SkillException("provider_error", FailureMessage(job.Status), job.ProviderCode);
        VideoValidator.EnsureTools();
        using var response = await SendDownloadAsync(job.Url, invocation.CancellationToken);
        if (!response.IsSuccessStatusCode) throw new SkillException("provider_error", "阿里云视频下载失败。");
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null and not ("video/mp4" or "application/octet-stream"))
        {
            throw new SkillException("provider_error", "阿里云视频下载响应不是 MP4。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(invocation.CancellationToken);
        var artifact = await artifacts.WriteAsync(stream, outputDirectory, invocation.CancellationToken);
        artifact["index"] = N(1);
        return new JsonArray((JsonNode)artifact);
    }

    public static JsonObject TaskView(string taskId, string status, JsonObject? error, SkillInvocation invocation) => Obj(
        ("schema_version", S(SkillProtocol.ContractVersion)), ("response_id", S(invocation.ResponseId)), ("status", S(status)),
        ("task_id", S(taskId)), ("task_context", invocation.TaskContext?.DeepClone()),
        ("provider_metadata", SkillResponses.ProviderMetadata("aliyun", invocation)), ("error", error),
        ("ignored_parameters", invocation.IgnoredParameters.DeepClone()), ("converted_parameters", invocation.ConvertedParameters.DeepClone()));

    public static string FailureMessage(string status) => status switch
    {
        "CANCELED" => "远端任务已取消。",
        "UNKNOWN" => "任务不存在或已超过 24 小时查询有效期。",
        _ => "阿里云视频生成失败。"
    };

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri endpoint, string apiKey, AliyunVideoRequest? request, SkillInvocation invocation, bool asyncHeader)
    {
        using var message = new HttpRequestMessage(method, endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (asyncHeader) message.Headers.TryAddWithoutValidation("X-DashScope-Async", "enable");
        if (request is not null) message.Content = JsonContent.Create(request, AliyunVideoJsonContext.Default.AliyunVideoRequest);
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
            if (location is null || redirect >= MaxRedirects) throw new SkillException("provider_error", "阿里云视频 URL 重定向无效或超过限制。");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
    }

    private static VideoJob ReadJob(HttpStatusCode status, string body, string apiKey, SkillInvocation invocation)
    {
        if (!status.IsSuccessStatusCode()) throw AliyunErrors.Map(status, body, apiKey);
        using var document = AliyunErrors.Parse(body);
        var root = document.RootElement;
        RememberProviderRequest(root, invocation, apiKey);
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Object)
        {
            var rootCode = AliyunErrors.SafeCode(JsonProtocol.GetString(root, "code"), apiKey);
            if (rootCode is not null) throw AliyunErrors.Map(status, body, apiKey);
            throw new SkillException("provider_error", "阿里云响应缺少 output。");
        }

        var taskStatus = JsonProtocol.GetString(output, "task_status");
        var taskId = JsonProtocol.GetString(output, "task_id");
        var providerCode = AliyunErrors.SafeCode(JsonProtocol.GetString(output, "code") ?? JsonProtocol.GetString(root, "code"), apiKey);
        if (taskStatus is not ("PENDING" or "RUNNING" or "SUCCEEDED" or "FAILED" or "CANCELED" or "UNKNOWN") || !AliyunVideoTaskIds.IsValid(taskId) || taskId!.Contains(apiKey, StringComparison.Ordinal))
        {
            throw new SkillException("provider_error", "阿里云响应缺少有效任务信息。", providerCode);
        }

        string? url = null;
        if (taskStatus == "SUCCEEDED")
        {
            url = JsonProtocol.GetString(output, "video_url");
            if (string.IsNullOrWhiteSpace(url)) throw new SkillException("provider_error", "阿里云完成响应缺少视频地址。");
        }

        return new VideoJob(taskId, taskStatus, url, providerCode);
    }

    private static void EnsureHttps(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new SkillException("provider_error", "阿里云视频 URL 必须为不含用户信息的 HTTPS 地址。");
        }
    }

    private static void RememberProviderRequest(JsonElement root, SkillInvocation invocation, string apiKey)
    {
        var requestId = JsonProtocol.GetString(root, "request_id");
        if (AliyunErrors.IsSafe(requestId, apiKey)) invocation.ProviderRequestId = requestId;
    }

    private static void RedactInPlace(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(pair => pair.Key).ToArray())
                {
                    if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && ShouldRedact(text)) obj[key] = "[redacted]";
                    else RedactInPlace(obj[key]);
                }
                break;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    if (array[index] is JsonValue value && value.TryGetValue<string>(out var text) && ShouldRedact(text)) array[index] = "[redacted]";
                    else RedactInPlace(array[index]);
                }
                break;
        }
    }

    private static bool ShouldRedact(string text) =>
        text.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Query.Length > 0 || uri.UserInfo.Length > 0);
}

internal static class AliyunEndpoints
{
    public static Uri Submit(Uri root) => Combine(root, "services/aigc/video-generation/video-synthesis");

    public static Uri Task(Uri root, string taskId) => Combine(root, "tasks/" + Uri.EscapeDataString(taskId));

    private static Uri Combine(Uri root, string resource)
    {
        var path = root.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return new Uri(path + "/" + resource);
    }
}

internal static class AliyunErrors
{
    public static JsonDocument Parse(string body)
    {
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { throw new SkillException("provider_error", "阿里云响应不是有效 JSON。"); }
    }

    public static SkillException Map(HttpStatusCode status, string body, string apiKey)
    {
        string? code = null;
        try
        {
            using var document = Parse(body);
            code = SafeCode(JsonProtocol.GetString(document.RootElement, "code"), apiKey);
            if (code is null && document.RootElement.TryGetProperty("output", out var output))
            {
                code = SafeCode(JsonProtocol.GetString(output, "code"), apiKey);
            }
        }
        catch (SkillException) { }

        return new SkillException(Normalize(code, status), "阿里云百炼请求未能完成。", code);
    }

    public static string? SafeCode(string? code, string apiKey) =>
        IsSafe(code, apiKey) && code!.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') ? code : null;

    public static bool IsSafe(string? value, string? apiKey) =>
        value is { Length: > 0 and <= 64 } && !value.Any(char.IsControl) &&
        (string.IsNullOrEmpty(apiKey) || !value.Contains(apiKey, StringComparison.Ordinal));

    private static string Normalize(string? code, HttpStatusCode status)
    {
        if (code is "InvalidApiKey" or "invalid_api_key" or "AccessDenied" or "access_denied") return "authentication_failed";
        if (code is "Arrearage") return "insufficient_balance";
        if (code is not null && (code.StartsWith("Throttling", StringComparison.Ordinal) || code is "insufficient_quota")) return "rate_limit_exceeded";
        if (code is not null && (code.Contains("DataInspection", StringComparison.Ordinal) || code is "IPInfringementSuspect")) return "content_policy_violation";
        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "authentication_failed",
            HttpStatusCode.PaymentRequired => "insufficient_balance",
            HttpStatusCode.TooManyRequests => "rate_limit_exceeded",
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => "timeout",
            _ => "provider_error"
        };
    }
}

internal static class HttpStatusExtensions
{
    public static bool IsSuccessStatusCode(this HttpStatusCode status) => (int)status is >= 200 and <= 299;
}
