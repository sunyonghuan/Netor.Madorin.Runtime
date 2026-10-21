using System.Net;
using Madorin.AI.Multimedia.Core.CommandLine;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.OpenAI.Image.Requests;

namespace Madorin.AI.OpenAI.Image.Provider;

internal sealed class OpenAiImageClient(HttpClient client)
{
    public static OpenAiImageRequest Map(ImageGenerationOptions options) =>
        new(options.Model, options.Prompt, options.ImageCount, options.Size, options.Quality, options.OutputFormat);

    public static JsonObject Preview(OpenAiImageRequest request) =>
        JsonSerializer.SerializeToNode(request, OpenAiJsonContext.Default.OpenAiImageRequest)!.AsObject();

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

        return ModelCatalogResponse.Parse(body, "openai", invocation);
    }

    public async Task<JsonArray> GenerateAsync(
        ImageGenerationOptions options, OpenAiImageRequest request, SkillInvocation invocation, ArtifactStore artifacts)
    {
        invocation.CancellationToken.ThrowIfCancellationRequested();
        var endpoint = new Uri(new Uri(options.Endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/"), "images/generations");
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        message.Content = JsonContent.Create(request, OpenAiJsonContext.Default.OpenAiImageRequest);
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
            throw new SkillException("provider_error", "OpenAI 响应不是有效 JSON。");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != options.ImageCount)
            {
                throw new SkillException("provider_error", "OpenAI 响应图片数量与请求不匹配。");
            }

            var result = new JsonArray();
            var index = 0;
            foreach (var item in data.EnumerateArray())
            {
                invocation.CancellationToken.ThrowIfCancellationRequested();
                var encoded = JsonProtocol.GetString(item, "b64_json");
                if (encoded is null)
                {
                    throw new SkillException("provider_error", "OpenAI 响应缺少图片数据。");
                }

                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(encoded);
                }
                catch (FormatException)
                {
                    throw new SkillException("provider_error", "OpenAI 图片数据格式无效。");
                }

                var artifact = await artifacts.WriteImageAsync(bytes, options.OutputFormat, options.OutputDirectory!, ++index, invocation.CancellationToken);
                result.Add((JsonNode)artifact);
            }

            return result;
        }
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
        return new SkillException(normalized, "OpenAI 请求未能完成。", code);
    }
}
