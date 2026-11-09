using System.Security.Cryptography;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.Aliyun;
using Madorin.AI.Runtime.Providers.Anthropic;
using Madorin.AI.Runtime.Providers.DeepSeek;
using Madorin.AI.Runtime.Providers.Kimi;
using Madorin.AI.Runtime.Providers.OpenAI;
using Madorin.AI.Runtime.Tools.Builtin;

namespace Madorin.AI.Runtime.Provider.Tests;

/// <summary>显式启用的真实端点附件冒烟；只复用本机配置，不把凭据复制到测试产物。</summary>
[TestClass]
[DoNotParallelize]
public sealed class LiveAttachmentProviderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("deepseek", false)]
    [DataRow("deepseek", true)]
    [DataRow("magpie-openai", false)]
    [DataRow("magpie-openai", true)]
    [DataRow("magpie-anthropic", false)]
    [DataRow("magpie-anthropic", true)]
    [DataRow("Kimi", false)]
    [DataRow("Kimi", true)]
    [DataRow("Aliyun", false)]
    [DataRow("Aliyun", true)]
    [DataRow("axis-code(gpt)", false)]
    [DataRow("axis-code(gpt)", true)]
    [DataRow("axis-code(Anthropic)", false)]
    [DataRow("axis-code(Anthropic)", true)]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task ImageInput_UsesRealEndpointWithLocalResolver(string providerName, bool toolResult)
    {
        if (Environment.GetEnvironmentVariable("MADORIN_TEST_LIVE_ATTACHMENTS") != "1")
        {
            Assert.Inconclusive("Set MADORIN_TEST_LIVE_ATTACHMENTS=1 to enable billable endpoint tests.");
        }
        var selectedProvider = Environment.GetEnvironmentVariable("MADORIN_TEST_LIVE_PROVIDER");
        if (!string.IsNullOrEmpty(selectedProvider) && selectedProvider != providerName)
        {
            Assert.Inconclusive("This Provider was not selected for the live test.");
        }

        var imagePath = Environment.GetEnvironmentVariable("MADORIN_TEST_ATTACHMENT_IMAGE");
        Assert.IsFalse(string.IsNullOrWhiteSpace(imagePath), "Set MADORIN_TEST_ATTACHMENT_IMAGE to a PNG fixture.");
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".madorin", "config.json");
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.CancellationToken));
        var providers = config.RootElement.GetProperty("providers").EnumerateArray().ToArray();
        var entry = providers.Single(provider => provider.GetProperty("name").GetString() == providerName);
        var model = providerName switch
        {
            "magpie-openai" => "group/auto-gpt-5-6",
            "magpie-anthropic" => "group/auto-claude-opus-4-8",
            "axis-code(gpt)" => "gpt-5.6-sol",
            "axis-code(Anthropic)" => "claude-opus-4-6",
            "deepseek" => "deepseek-v4-flash",
            "Aliyun" => "qwen3.8-max",
            "Kimi" => "kimi-k3.1-pro",
            _ => entry.GetProperty("models")[0].GetString()!
        };
        Assert.IsTrue(entry.GetProperty("models").EnumerateArray().Any(item => item.GetString() == model),
            "The selected live test model must exist in the user's configuration.");
        var protocol = entry.GetProperty("protocol").GetString()!;
        var key = entry.GetProperty("apiKey").GetString()!;
        var baseUrl = entry.GetProperty("baseUrl").GetString()!;
        string Redact(string value)
        {
            foreach (var provider in providers)
            {
                var secret = provider.GetProperty("apiKey").GetString();
                if (!string.IsNullOrEmpty(secret))
                {
                    value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
                }
            }
            return value;
        }

        var bytes = await File.ReadAllBytesAsync(imagePath, TestContext.CancellationToken);
        var blob = new BlobReference("live-input-only", bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)), "image/png", "live-test",
            DateTimeOffset.UtcNow.AddMinutes(5));
        var resolver = new FixtureResolver(blob, bytes);
        // 媒体 flags 故意为 false；仅声明工具/思考能力，避免与附件无关的历史检查干扰。
        ProviderModel[] models = [new(model, model, Capabilities: new(Streaming: true, Reasoning: true))];
        using var http = new InspectingHttpFactory(bytes);
        // 独立 Chat Adapter 无 HTTP 注入点；测试 relay 保持路径/正文不变，只转发到配置端点。
        await using var relay = protocol is "DeepSeek" or "Kimi"
            ? await LiveAttachmentHttpRelay.StartAsync(new Uri(baseUrl), http.Handler.Inspect,
                TestContext.CancellationToken) : null;
        baseUrl = relay?.AdapterBaseUrl ?? baseUrl;
        var runtime = CreateAdapter(protocol, providerName, key, baseUrl, models, resolver, http);
        using var lifetime = runtime as IDisposable;
        using var args = JsonDocument.Parse("{}");
        var prompt = new TextContentBlock("Can you access the attached image? Give a brief answer and a short description " +
            "if accessible. If it is not accessible, say so. Do not use tools or infer content from file metadata.");
        RuntimeProviderMessage[] messages = toolResult
            ? [new(RuntimeProviderRoles.User, [prompt]),
                new(RuntimeProviderRoles.Assistant, [new ToolCallContentBlock("call-live-image",
                    BuiltinToolRegistry.FileAttachToolId, BuiltinToolRegistry.FileAttachToolId, args.RootElement.Clone())]),
                new(RuntimeProviderRoles.Tool, [new ToolResultContentBlock("call-live-image",
                    BuiltinToolRegistry.FileAttachToolId, true, [new TextContentBlock("Attachment read completed."),
                        new BlobRefContentBlock(blob)])])]
            : [new(RuntimeProviderRoles.User, [prompt, new BlobRefContentBlock(blob)])];
        var request = new RuntimeProviderRequest(Guid.NewGuid().ToString("N"), "live-test", providerName,
            model, messages, Tools: toolResult
                ? [.. new BuiltinToolRegistry().GetTools([BuiltinToolRegistry.FileAttachToolId])] : null,
            MaxTokens: protocol is "Kimi" or "Aliyun" ? 2048 : 512,
            CancellationToken: TestContext.CancellationToken);
        var events = new List<RuntimeProviderEvent>();
        try
        {
            await foreach (var item in runtime.CompleteStreamingAsync(request, TestContext.CancellationToken))
            {
                events.Add(item);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Assert.Fail(Redact($"{providerName}/{model}, tool={toolResult}: {ex.Message}"));
        }

        TestContext.WriteLine($"Provider={providerName}; protocol={protocol}; model={model}; tool={toolResult}; " +
            $"resolver calls={resolver.Calls}; bytes={bytes.Length}; sha256={blob.Sha256}");
        TestContext.WriteLine($"Captured request path={http.Handler.RequestPath}; " +
            $"verified native image parts={http.Handler.VerifiedImageParts}; body bytes={http.Handler.RequestBytes}");
        TestContext.WriteLine("Finish reasons: " + string.Join(",", events.OfType<InvocationCompletedProviderEvent>()
            .Select(item => item.FinishReason)));
        var text = string.Concat(events.OfType<TextDeltaProviderEvent>().Select(item => item.Delta));
        TestContext.WriteLine(Redact("Response: " + text));
        var failures = events.OfType<InvocationFailedProviderEvent>().ToArray();
        foreach (var failure in failures)
        {
            TestContext.WriteLine(Redact($"Failure: {failure.Error.Code}: {failure.Error.Message}"));
        }
        Assert.IsEmpty(failures, "A Provider rejection is recorded as a failed live test, not attachment support.");
        Assert.AreEqual(1, resolver.Calls);
        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        Assert.AreEqual(1, http.Handler.VerifiedImageParts,
            "The native HTTP image part must decode to the exact fixture bytes.");
        // 传输验收不评估颜色/物体识别准确率；不可见回包单独记录，不推断为本地丢图。
    }

    private static IRuntimeProviderAdapter CreateAdapter(string protocol, string name, string key, string baseUrl,
        ProviderModel[] models, IProviderBlobResolver resolver, IHttpClientFactory http) => protocol switch
        {
            "OpenAI" => new OpenAIProviderAdapter(name, key, baseUrl, http, models, resolver),
            "Anthropic" => new AnthropicProviderAdapter(name, key, baseUrl, http, models, resolver),
            "DeepSeek" => new DeepSeekProviderAdapter(baseUrl, key, models: models, providerId: name, blobResolver: resolver),
            "Kimi" => new KimiProviderAdapter(key, baseUrl, models: models, providerId: name, blobResolver: resolver),
            "Aliyun" => new AliyunProviderAdapter(new AliyunProviderOptions(name, key, baseUrl,
                Capabilities: new(Streaming: true, Reasoning: true), Models: models), http, resolver),
            _ => throw new NotSupportedException("No live test adapter for the configured protocol.")
        };

    private sealed class InspectingHttpFactory(byte[] expected) : IHttpClientFactory, IDisposable
    {
        public ImagePartHandler Handler { get; } = new(expected) { InnerHandler = new HttpClientHandler() };
        public HttpClient CreateClient(string name) => new(Handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        public void Dispose() => Handler.Dispose();
    }

    /// <summary>仅在测试中核验协议附件字段，不保存 Header、请求正文或 Base64。</summary>
    private sealed class ImagePartHandler(byte[] expected) : DelegatingHandler
    {
        public string? RequestPath { get; private set; }
        public long RequestBytes { get; private set; }
        public int VerifiedImageParts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestPath = request.RequestUri?.AbsolutePath;
            if (request.Content is { } content)
            {
                var bytes = await content.ReadAsByteArrayAsync(ct);
                Inspect(bytes, RequestPath ?? string.Empty);
            }
            return await base.SendAsync(request, ct);
        }

        public void Inspect(byte[] bytes, string path)
        {
            RequestPath = path;
            RequestBytes = bytes.LongLength;
            using var document = JsonDocument.Parse(bytes);
            VerifyParts(document.RootElement);
        }

        private void VerifyParts(JsonElement value)
        {
            if (value.ValueKind is JsonValueKind.Array)
            {
                foreach (var child in value.EnumerateArray())
                {
                    VerifyParts(child);
                }
            }
            else if (value.ValueKind is JsonValueKind.Object)
            {
                if (value.TryGetProperty("type", out var type))
                {
                    string? encoded = type.GetString() switch
                    {
                        "input_image" => value.GetProperty("image_url").GetString(),
                        "image_url" => value.GetProperty("image_url").GetProperty("url").GetString(),
                        "image" when value.TryGetProperty("source", out var source)
                            => source.GetProperty("data").GetString(),
                        _ => null
                    };
                    if (encoded is not null)
                    {
                        if (encoded.StartsWith("data:", StringComparison.Ordinal))
                        {
                            encoded = encoded[(encoded.IndexOf(',') + 1)..];
                        }
                        CollectionAssert.AreEqual(expected, Convert.FromBase64String(encoded));
                        VerifiedImageParts++;
                    }
                }
                foreach (var property in value.EnumerateObject())
                {
                    VerifyParts(property.Value);
                }
            }
        }
    }

    private sealed class FixtureResolver(BlobReference allowed, byte[] bytes) : IProviderBlobResolver
    {
        public int Calls { get; private set; }
        public ValueTask<ResolvedProviderBlob> ResolveAsync(BlobReference blob, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (blob != allowed)
            {
                throw new UnauthorizedAccessException("Only the test fixture is authorized.");
            }

            Calls++;
            return ValueTask.FromResult(new ResolvedProviderBlob(bytes, "image/png", "sample.png"));
        }
    }
}
