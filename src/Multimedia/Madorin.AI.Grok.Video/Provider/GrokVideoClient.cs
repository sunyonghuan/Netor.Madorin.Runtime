using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Madorin.AI.Grok.Video.Metadata;
using Madorin.AI.Grok.Video.Requests;
using Madorin.AI.Multimedia.Core.CommandLine;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Grok.Video.Provider;

internal sealed class GrokVideoClient(HttpClient client, TimeSpan? pollInterval = null)
{
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
    public static GrokVideoRequest Map(VideoGenerationOptions options) =>
        new(options.Model, options.Prompt, options.DurationSeconds, options.AspectRatio, options.Resolution);

    public static JsonObject Preview(GrokVideoRequest request) =>
        JsonSerializer.SerializeToNode(request, GrokVideoJsonContext.Default.GrokVideoRequest)!.AsObject();

    public async Task<JsonObject> ListModelsAsync(ModelListOptions options, SkillInvocation invocation)
    {
        invocation.ThrowIfCancellationRequested();
        using var response = await SendAsync(HttpMethod.Get, Endpoint(options.Endpoint, "models"), options.ApiKey, null, invocation);
        var body = await GrokVideoResponseReader.ReadBodyAsync(response, invocation);
        if (!response.IsSuccessStatusCode)
        {
            throw MapError(response.StatusCode, body, options.ApiKey);
        }

        return ModelCatalogResponse.Parse(body, "grok", invocation);
    }

    public async Task<JsonObject> SubmitAsync(VideoGenerationOptions options, GrokVideoRequest request, SkillInvocation invocation)
    {
        invocation.CancellationToken.ThrowIfCancellationRequested();
        using var response = await SendAsync(
            HttpMethod.Post, Endpoint(options.Endpoint, "videos/generations"), options.ApiKey, request, invocation);
        var body = await GrokVideoResponseReader.ReadBodyAsync(response, invocation);
        if (!response.IsSuccessStatusCode)
        {
            throw MapError(response.StatusCode, body, options.ApiKey);
        }

        var document = GrokVideoResponseReader.Parse(body);
        using (document)
        {
            var taskId = GetString(document.RootElement, "request_id");
            if (!GrokVideoTaskIds.IsValid(taskId) || !GrokVideoErrors.IsSafeProviderValue(taskId, options.ApiKey))
            {
                throw new SkillException("provider_error", "Grok 提交响应缺少任务 ID。");
            }

            invocation.TaskId = taskId;
            invocation.TaskContext = GrokVideoMetadata.TaskContext(taskId!, options.Model);
            invocation.RemoteCancellation = "unsupported";
            return TaskResponse(taskId!, "submitted", null, invocation);
        }
    }

    public async Task<VideoJob> QueryOnceAsync(TaskOperationOptions options, SkillInvocation invocation)
    {
        invocation.ThrowIfCancellationRequested();
        return await ReadJobAsync(options.Endpoint, options.TaskId, options.ApiKey, invocation);
    }

    public async Task<VideoJob> WaitAsync(Uri endpoint, string taskId, string apiKey, SkillInvocation invocation)
    {
        while (true)
        {
            var job = await ReadJobAsync(endpoint, taskId, apiKey, invocation);
            if (job.Status is "done" or "failed")
            {
                return job;
            }

            await Task.Delay(_pollInterval, invocation.CancellationToken);
        }
    }

    public async Task<JsonArray> DownloadAsync(VideoJob job, string outputDirectory, SkillInvocation invocation, VideoArtifactStore artifacts)
    {
        if (job.Status is not "done" || job.Url is null)
        {
            throw new SkillException(job.Status is "pending" ? "task_not_ready" : "provider_error", "视频尚未完成，不能下载。");
        }

        if (!Uri.TryCreate(job.Url, UriKind.Absolute, out var videoUri) || videoUri.Scheme != Uri.UriSchemeHttps ||
            videoUri.UserInfo.Length > 0 || videoUri.Fragment.Length > 0)
        {
            throw new SkillException("provider_error", "Grok 视频地址无效。");
        }

        VideoValidator.EnsureTools();
        invocation.CancellationToken.ThrowIfCancellationRequested();
        using var message = new HttpRequestMessage(HttpMethod.Get, videoUri);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, invocation.CancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new SkillException("provider_error", "Grok 视频下载失败。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(invocation.CancellationToken);
        var artifact = await artifacts.WriteAsync(stream, outputDirectory, invocation.CancellationToken);
        artifact["index"] = N(1);
        return new JsonArray((JsonNode)artifact);
    }

    public static JsonObject TaskView(VideoJob job, SkillInvocation invocation)
    {
        invocation.TaskId = job.TaskId;
        invocation.RemoteStatus = job.Status;
        invocation.RemoteCancellation = "unsupported";
        var status = job.Status switch
        {
            "done" => "succeeded",
            "failed" => "failed",
            _ => "pending"
        };
        JsonObject? error = status == "failed"
            ? Obj(("code", S(GrokVideoErrors.Normalize(job.ErrorCode))), ("message", S("Grok 视频生成失败。")),
                ("retryable", B(false)),
                ("provider_error", job.ErrorCode is null ? null : Obj(("code", S(job.ErrorCode)), ("message", S("Grok 视频生成失败。")))))
            : null;
        return TaskResponse(job.TaskId, status, error, invocation);
    }

    private async Task<VideoJob> ReadJobAsync(Uri endpoint, string taskId, string apiKey, SkillInvocation invocation)
    {
        invocation.ThrowIfCancellationRequested();
        using var response = await SendAsync(HttpMethod.Get, Endpoint(endpoint, "videos/" + Uri.EscapeDataString(taskId)), apiKey, null, invocation);
        var body = await GrokVideoResponseReader.ReadBodyAsync(response, invocation);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new SkillException("provider_error", "Grok 视频任务不存在。");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw MapError(response.StatusCode, body, apiKey);
        }

        var document = GrokVideoResponseReader.Parse(body);
        using (document)
        {
            var root = document.RootElement;
            var status = GetString(root, "status");
            if (status is not ("pending" or "done" or "failed"))
            {
                throw new SkillException("provider_error", "Grok 返回了未知的视频状态。");
            }
            invocation.RemoteStatus = status;

            string? url = null;
            int? duration = null;
            if (root.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.Object)
            {
                url = GetString(video, "url");
                if (video.TryGetProperty("duration", out var durationValue))
                {
                    if (durationValue.ValueKind != JsonValueKind.Number || !durationValue.TryGetInt32(out var seconds) || seconds < 0)
                    {
                        throw new SkillException("provider_error", "Grok 视频时长格式无效。");
                    }

                    duration = seconds;
                }
            }

            var model = GetString(root, "model");
            if (root.TryGetProperty("model", out var modelValue) && modelValue.ValueKind != JsonValueKind.Null &&
                (!ModelIds.IsValid(model) || !GrokVideoErrors.IsSafeProviderValue(model, apiKey)))
            {
                throw new SkillException("provider_error", "Grok 返回了无效的任务模型。");
            }

            if (model is not null && invocation.Model is not null && model != invocation.Model)
            {
                throw new SkillException("provider_error", "远端任务模型与原任务上下文不一致。");
            }

            if (status == "done" && (!Uri.TryCreate(url, UriKind.Absolute, out var videoUri) ||
                videoUri.Scheme != Uri.UriSchemeHttps || videoUri.UserInfo.Length > 0 || videoUri.Fragment.Length > 0))
            {
                throw new SkillException("provider_error", "Grok 已完成任务缺少有效的视频地址。");
            }
            string? errorCode = null;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                errorCode = GrokVideoErrors.SafeCode(GetString(error, "code"), apiKey);
            }

            return new VideoJob(taskId, status, url, duration, model, errorCode);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, Uri endpoint, string apiKey, GrokVideoRequest? request, SkillInvocation invocation)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsControl))
        {
            throw new SkillException("invalid_parameter", "api-key 必须非空且不含控制字符。");
        }

        using var message = new HttpRequestMessage(method, endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (request is not null)
        {
            message.Content = JsonContent.Create(request, GrokVideoJsonContext.Default.GrokVideoRequest);
        }

        var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, invocation.CancellationToken);
        if (response.Headers.TryGetValues("x-request-id", out var ids))
        {
            var traceId = ids.FirstOrDefault();
            if (GrokVideoErrors.IsSafeProviderValue(traceId, apiKey, 256))
            {
                invocation.ProviderRequestId = traceId;
            }
        }
        return response;
    }

    private static Uri Endpoint(Uri root, string relative) =>
        new(new Uri(root.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/"), relative);

    private static JsonObject TaskResponse(string taskId, string status, JsonObject? error, SkillInvocation invocation)
    {
        return Obj(
            ("schema_version", S(SkillProtocol.ContractVersion)), ("response_id", S(invocation.ResponseId)), ("status", S(status)),
            ("task_id", S(taskId)), ("task_context", invocation.TaskContext?.DeepClone()),
            ("provider_metadata", SkillResponses.ProviderMetadata("grok", invocation)), ("error", error),
            ("ignored_parameters", invocation.IgnoredParameters.DeepClone()),
            ("converted_parameters", invocation.ConvertedParameters.DeepClone()));
    }

    private static SkillException MapError(HttpStatusCode status, string body, string apiKey)
    {
        string? code = null;
        try
        {
            using var document = GrokVideoResponseReader.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                code = GrokVideoErrors.SafeCode(GetString(error, "code"), apiKey);
            }
        }
        catch (SkillException)
        {
        }

        var normalized = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "authentication_failed",
            HttpStatusCode.TooManyRequests => "rate_limit_exceeded",
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => "timeout",
            _ => "provider_error"
        };
        normalized = GrokVideoErrors.Normalize(code, normalized);
        return new SkillException(normalized, "Grok 请求未能完成。", code);
    }
}
