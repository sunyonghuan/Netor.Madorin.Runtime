using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.CommandLine;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.OpenAI.Video.Requests;

namespace Madorin.AI.OpenAI.Video.Provider;

internal sealed class OpenAiVideoClient(HttpClient client, TextWriter? diagnostics = null)
{
    public async Task<JsonObject> ListModelsAsync(ModelListOptions options, SkillInvocation invocation)
    {
        using var response = await SendAsync(HttpMethod.Get, options, "models", null, invocation, "list-models");
        var body = await ReadBodyAsync(response, invocation.CancellationToken);
        EnsureSuccess(response.StatusCode, body);
        return ModelCatalogResponse.Parse(body, "openai", invocation);
    }

    public async Task<OpenAiVideoJob> SubmitAsync(VideoGenerationOptions options, OpenAiVideoRequest mapped,
        WorkspacePaths paths, SkillInvocation invocation)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(mapped.Model), "model");
        form.Add(new StringContent(mapped.Prompt), "prompt");
        form.Add(new StringContent(mapped.Seconds), "seconds");
        form.Add(new StringContent(mapped.Size), "size");
        if (options.ReferencePath is { } file)
        {
            paths.EnsureAllowed(file);
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            if (stream.Length is < 1 or > 20971520) { throw new SkillException("invalid_parameter", "参考图片必须为 1..20 MiB。"); }
            using var bytes = new MemoryStream();
            await stream.CopyToAsync(bytes, invocation.CancellationToken);
            if (bytes.Length > 20971520) { throw new SkillException("invalid_parameter", "参考图片超过 20 MiB。"); }
            var content = bytes.ToArray();
            var format = content.Length >= 8 && content[0] == 137 && content[1] == 80 ? "png" :
                content.Length >= 3 && content[0] == 255 && content[1] == 216 ? "jpeg" : "webp";
            var dimensions = await ImageValidator.ValidateAsync(content, format, invocation.CancellationToken);
            if (FormattableString.Invariant($"{dimensions.Width}x{dimensions.Height}") != mapped.Size)
            {
                throw new SkillException("invalid_parameter", "参考图尺寸必须与目标视频尺寸一致，不自动缩放。");
            }
            var upload = new ByteArrayContent(content);
            upload.Headers.ContentType = new MediaTypeHeaderValue("image/" + format);
            form.Add(upload, "input_reference", "reference." + format);
        }
        using var response = await SendAsync(HttpMethod.Post, new(options.Endpoint, options.ApiKey), "videos", form, invocation, "generate");
        return await ReadJobAsync(response, options.Model, null, options.Task, invocation);
    }

    public async Task<OpenAiVideoJob> RetrieveAsync(ModelListOptions options, SkillInvocation invocation)
    {
        using var response = await SendAsync(HttpMethod.Get, options, "videos/" + Uri.EscapeDataString(invocation.TaskId!), null, invocation, "query");
        return await ReadJobAsync(response, invocation.Model!, invocation.TaskId!,
            invocation.TaskContext!["task"]!.GetValue<string>(), invocation);
    }

    public async Task<OpenAiVideoJob> WaitAsync(OpenAiVideoJob job, ModelListOptions connection, SkillInvocation invocation)
    {
        while (job.Status is "queued" or "in_progress")
        {
            await Task.Delay(TimeSpan.FromSeconds(1), invocation.CancellationToken);
            job = await RetrieveAsync(connection, invocation);
        }
        return job;
    }

    public async Task<JsonArray> DownloadAsync(ModelListOptions options, string outputDirectory, VideoArtifactStore store, SkillInvocation invocation)
    {
        using var response = await SendAsync(HttpMethod.Get, options, "videos/" + Uri.EscapeDataString(invocation.TaskId!) + "/content", null, invocation, "download");
        if (!response.IsSuccessStatusCode)
        {
            EnsureSuccess(response.StatusCode, await ReadBodyAsync(response, invocation.CancellationToken));
        }
        if (response.Content.Headers.ContentType?.MediaType is not ("video/mp4" or "application/octet-stream" or "application/binary"))
        {
            throw new SkillException("provider_error", "视频响应 MIME 不符合 MP4 协议。");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(invocation.CancellationToken);
        var artifact = await store.WriteAsync(stream, outputDirectory, invocation.CancellationToken);
        return new JsonArray(artifact);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, ModelListOptions connection, string relative,
        HttpContent? content, SkillInvocation invocation, string operation)
    {
        invocation.ThrowIfCancellationRequested();
        var uri = new Uri(connection.Endpoint.AbsoluteUri.TrimEnd('/') + "/" + relative);
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);
        var started = Stopwatch.GetTimestamp();
        WriteDiagnostic(invocation, $"{operation} request_started method={method.Method}");
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, invocation.CancellationToken);
        WriteDiagnostic(invocation, $"{operation} response_received status={(int)response.StatusCode} elapsed_ms={(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds}");
        if (response.Headers.TryGetValues("x-request-id", out var trace)) { invocation.ProviderRequestId = trace.FirstOrDefault(); }
        return response;
    }

    private async Task<OpenAiVideoJob> ReadJobAsync(HttpResponseMessage response, string model, string? expectedId,
        string task, SkillInvocation invocation)
    {
        var body = await ReadBodyAsync(response, invocation.CancellationToken);
        EnsureSuccess(response.StatusCode, body);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object || JsonProtocol.HasDuplicateProperties(document.RootElement))
            {
                throw new SkillException("provider_error", "视频任务响应必须为无重复字段的对象。");
            }
            var job = JsonSerializer.Deserialize(body, VideoJsonContext.Default.OpenAiVideoJob);
            if (job is null || !VideoTaskContext.IsValidTaskId(job.Id) || expectedId is not null && job.Id != expectedId)
            {
                throw new SkillException("provider_error", "视频任务 ID 无效或与当前任务不一致。");
            }
            // 任务 ID 在受理后立即保留；其余字段无效时不拼造完整恢复状态。
            invocation.TaskId = job.Id;
            if (job.Model != model || job.Status is not ("queued" or "in_progress" or "completed" or "failed" or "cancelled" or "canceled" or "expired"))
            {
                throw new SkillException("provider_error", "视频任务模型或状态与协议不一致。");
            }
            invocation.TaskContext ??= VideoTaskContext.Create(job.Id!, task, model);
            invocation.RemoteStatus = job.Status;
            invocation.RemoteCancellation ??= "not_requested";
            if (job.Status is "cancelled" or "canceled") { invocation.RemoteCancellation = "confirmed"; }
            WriteDiagnostic(invocation, "remote_status=" + job.Status);
            return job;
        }
        catch (JsonException)
        {
            throw new SkillException("provider_error", "视频任务响应不是有效 JSON。");
        }
    }

    private void WriteDiagnostic(SkillInvocation invocation, string message)
    {
        if (!invocation.Verbose || diagnostics is null) { return; }
        try { diagnostics.WriteLine("openai-video " + message); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = new byte[1048577];
        var length = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, cancellationToken);
        if (length > 1048576) { throw new SkillException("provider_error", "Provider JSON 响应超过 1 MiB。"); }
        try { return new System.Text.UTF8Encoding(false, true).GetString(bytes, 0, length); }
        catch (System.Text.DecoderFallbackException) { throw new SkillException("provider_error", "Provider JSON 不是 UTF-8。"); }
    }

    internal static string ErrorCode(string? code) => code switch
    {
        "content_policy_violation" => "content_policy_violation",
        "insufficient_quota" or "billing_hard_limit_reached" => "insufficient_balance",
        _ => "provider_error"
    };

    private static void EnsureSuccess(HttpStatusCode status, string body)
    {
        if ((int)status is >= 200 and < 300) { return; }
        string? code = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object || JsonProtocol.HasDuplicateProperties(document.RootElement))
            {
                throw new JsonException();
            }
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                !JsonProtocol.HasDuplicateProperties(error))
            {
                code = JsonProtocol.GetString(error, "code");
            }
        }
        catch (JsonException)
        {
            throw new SkillException("provider_error", "Provider 错误响应不是有效的无重复字段 JSON 对象。");
        }
        var normalized = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "authentication_failed",
            HttpStatusCode.TooManyRequests => "rate_limit_exceeded",
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => "timeout",
            _ => ErrorCode(code)
        };
        // 未知远端 code 也可能含用户内容，不原样回显。
        throw new SkillException(normalized, "OpenAI 视频请求未能完成。", ErrorCode(code) != "provider_error" ? code : null);
    }
}
