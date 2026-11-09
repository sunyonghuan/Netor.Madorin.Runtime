using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http.Json;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;
using Madorin.AI.Multimedia.Core.CommandLine;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.Gemini.Image.Requests;

namespace Madorin.AI.Gemini.Image.Provider;

internal sealed class GeminiImageClient(HttpClient client)
{
    public static GeminiImageRequest Map(ImageGenerationOptions options) => new(
        [new GeminiContent([new GeminiPart(options.Prompt)])],
        new GeminiGenerationConfig(["TEXT", "IMAGE"], 1, options.AspectRatio == "auto" ? null : new GeminiImageConfig(options.AspectRatio)));

    public static JsonObject Preview(GeminiImageRequest request) =>
        JsonSerializer.SerializeToNode(request, GeminiJsonContext.Default.GeminiImageRequest)!.AsObject();

    public async Task<JsonObject> ListModelsAsync(ModelListOptions options, SkillInvocation invocation)
    {
        invocation.ThrowIfCancellationRequested();
        var endpoint = new Uri(options.Endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/models");
        using var message = new HttpRequestMessage(HttpMethod.Get, endpoint);
        message.Headers.TryAddWithoutValidation("x-goog-api-key", options.ApiKey);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, invocation.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(invocation.CancellationToken);
        if (!response.IsSuccessStatusCode) throw MapError(response.StatusCode, body);
        invocation.ProviderRequestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
        return ParseModels(body, invocation);
    }

    public async Task<JsonArray> GenerateAsync(ImageGenerationOptions options, GeminiImageRequest request,
        SkillInvocation invocation, ArtifactStore artifacts)
    {
        invocation.ThrowIfCancellationRequested();
        var endpoint = new Uri(options.Endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/models/" + Uri.EscapeDataString(NormalizeModelId(options.Model)) + ":generateContent");
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        message.Headers.TryAddWithoutValidation("x-goog-api-key", options.ApiKey);
        message.Content = JsonContent.Create(request, GeminiJsonContext.Default.GeminiImageRequest);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, invocation.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(invocation.CancellationToken);
        if (!response.IsSuccessStatusCode) throw MapError(response.StatusCode, body);
        invocation.ProviderRequestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;

        JsonDocument document;
        try { document = JsonDocument.Parse(body); }
        catch (JsonException) { throw new SkillException("provider_error", "Gemini 响应不是有效 JSON。"); }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("candidates", out var candidates) ||
                candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() != 1)
                throw new SkillException("provider_error", "Gemini 响应缺少唯一候选结果。");

            var candidate = candidates[0];
            if (!candidate.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
                throw new SkillException("provider_error", "Gemini 响应缺少内容片段。");

            JsonElement imagePart = default;
            var imageCount = 0;
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("inlineData", out var inlineData)) { imagePart = inlineData; imageCount++; }
            }
            if (imageCount != 1) throw new SkillException("provider_error", "Gemini 响应必须包含一个图片片段。");
            var encoded = JsonProtocol.GetString(imagePart, "data");
            var mime = JsonProtocol.GetString(imagePart, "mimeType");
            if (encoded is null) throw new SkillException("provider_error", "Gemini 图片片段缺少 Base64 数据。");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(encoded); }
            catch (FormatException) { throw new SkillException("provider_error", "Gemini 图片数据格式无效。"); }
            var format = mime switch
            {
                "image/png" => "png", "image/jpeg" => "jpeg", "image/webp" => "webp",
                _ => throw new SkillException("provider_error", "Gemini 返回了不支持的图片 MIME 类型。")
            };
            var artifact = await artifacts.WriteImageAsync(bytes, format, options.OutputDirectory!, 1, invocation.CancellationToken);
            return new JsonArray((JsonNode)artifact);
        }
    }

    private static string NormalizeModelId(string model) => model.StartsWith("models/", StringComparison.Ordinal) ? model[7..] : model;

    private static JsonObject ParseModels(string body, SkillInvocation invocation)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("models", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new SkillException("provider_error", "Gemini 模型目录缺少 models 数组。");
            var models = new JsonArray();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateArray())
            {
                var name = JsonProtocol.GetString(entry, "name");
                var id = name?.StartsWith("models/", StringComparison.Ordinal) == true ? name[7..] : name;
                var methods = entry.TryGetProperty("supportedGenerationMethods", out var supported) && supported.ValueKind == JsonValueKind.Array
                    ? supported.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()).ToArray()
                    : [];
                if (!ModelIds.IsValid(id) || !ids.Add(id!)) throw new SkillException("provider_error", "Gemini 模型目录包含无效或重复的 ID。");
                models.Add(Obj(("id", S(id)), ("name", S(id)), ("tasks", methods.Contains("generateContent", StringComparer.Ordinal) ? new JsonArray(S("text-to-image")) : null)));
            }
            return Obj(("schema_version", S(SkillProtocol.ContractVersion)), ("response_id", S(invocation.ResponseId)),
                ("provider", S("gemini")), ("model_source", S("api")), ("models", models));
        }
        catch (JsonException) { throw new SkillException("provider_error", "Gemini 模型目录不是有效 JSON。"); }
    }

    private static SkillException MapError(HttpStatusCode status, string body)
    {
        string? code = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error)) code = JsonProtocol.GetString(error, "status");
        }
        catch (JsonException) { }
        var normalized = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "authentication_failed",
            HttpStatusCode.TooManyRequests => "rate_limit_exceeded",
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => "timeout",
            _ => "provider_error"
        };
        if (code == "RESOURCE_EXHAUSTED") normalized = "rate_limit_exceeded";
        if (code == "PERMISSION_DENIED") normalized = "authentication_failed";
        return new SkillException(normalized, "Gemini 请求未能完成。", code);
    }
}




