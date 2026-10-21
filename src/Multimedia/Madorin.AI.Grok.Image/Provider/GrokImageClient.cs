using System.Net;
using Madorin.AI.Multimedia.Core.CommandLine;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.Grok.Image.Requests;

namespace Madorin.AI.Grok.Image.Provider;

internal sealed class GrokImageClient(HttpClient client)
{
    public static GrokImageRequest Map(ImageGenerationOptions options) =>
        new(options.Model, options.Prompt, options.ImageCount, options.AspectRatio, "b64_json");

    public static JsonObject Preview(GrokImageRequest request) =>
        JsonSerializer.SerializeToNode(request, GrokJsonContext.Default.GrokImageRequest)!.AsObject();

    public async Task<JsonObject> ListModelsAsync(ModelListOptions options, SkillInvocation invocation)
    {
        invocation.ThrowIfCancellationRequested();
        var endpoint = new Uri(new Uri(options.Endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/"), "models");
        using var message = new HttpRequestMessage(HttpMethod.Get, endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, invocation.CancellationToken);
        invocation.ProviderRequestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
        var body = await response.Content.ReadAsStringAsync(invocation.CancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw MapError(response.StatusCode, body);
        }

        return ModelCatalogResponse.Parse(body, "grok", invocation);
    }

    public async Task<JsonArray> GenerateAsync(
        ImageGenerationOptions options, GrokImageRequest request, SkillInvocation invocation, ArtifactStore artifacts)
    {
        invocation.CancellationToken.ThrowIfCancellationRequested();
        var endpoint = new Uri(new Uri(options.Endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/"), "images/generations");
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        message.Content = JsonContent.Create(request, GrokJsonContext.Default.GrokImageRequest);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, invocation.CancellationToken);
        invocation.ProviderRequestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
        var body = await response.Content.ReadAsStringAsync(invocation.CancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw MapError(response.StatusCode, body);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new SkillException("provider_error", "Grok 响应不是有效 JSON。");
        }

        using (document)
        {
            var root = document.RootElement;
            if (response.StatusCode == HttpStatusCode.Accepted ||
                JsonProtocol.GetString(root, "status") is "pending" or "failed" ||
                root.ValueKind == JsonValueKind.Object && root.TryGetProperty("request_id", out _))
            {
                throw new SkillException("provider_error", "Grok 返回了异步任务；当前技能只支持同步 Base64 图片响应。");
            }

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != options.ImageCount)
            {
                throw new SkillException("provider_error", "Grok 响应图片数量与请求不匹配。");
            }

            var result = new JsonArray();
            var index = 0;
            foreach (var item in data.EnumerateArray())
            {
                invocation.CancellationToken.ThrowIfCancellationRequested();
                var encoded = JsonProtocol.GetString(item, "b64_json");
                if (encoded is null)
                {
                    throw new SkillException("provider_error", "Grok 响应缺少图片数据。");
                }

                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(encoded);
                }
                catch (FormatException)
                {
                    throw new SkillException("provider_error", "Grok 图片数据格式无效。");
                }

                var mime = JsonProtocol.GetString(item, "mime_type");
                var format = DetectFormat(bytes, mime);
                var artifact = await artifacts.WriteImageAsync(bytes, format, options.OutputDirectory!, ++index, invocation.CancellationToken);
                result.Add((JsonNode)artifact);
            }

            return result;
        }
    }

    private static string DetectFormat(byte[] bytes, string? mime)
    {
        var format = mime switch
        {
            null or "" => null,
            "image/png" => "png",
            "image/webp" => "webp",
            "image/jpeg" or "image/jpg" => "jpeg",
            _ => throw new SkillException("provider_error", "Grok 返回了不支持的图片 MIME 类型。")
        };

        if (format is not null)
        {
            return format;
        }

        if (bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            return "png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
        {
            return "jpeg";
        }

        if (bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            return "webp";
        }

        throw new SkillException("provider_error", "Grok 返回的图片缺少可识别的 MIME 类型或文件签名。");
    }

    private static SkillException MapError(HttpStatusCode status, string body)
    {
        string? code = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error))
            {
                code = JsonProtocol.GetString(error, "code");
            }
        }
        catch (JsonException)
        {
        }

        var normalized = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "authentication_failed",
            HttpStatusCode.TooManyRequests => "rate_limit_exceeded",
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => "timeout",
            _ => "provider_error"
        };
        normalized = code switch
        {
            "insufficient_quota" or "billing_hard_limit_reached" => "insufficient_balance",
            "content_policy_violation" => "content_policy_violation",
            _ => normalized
        };
        return new SkillException(normalized, "Grok 请求未能完成。", code);
    }
}
