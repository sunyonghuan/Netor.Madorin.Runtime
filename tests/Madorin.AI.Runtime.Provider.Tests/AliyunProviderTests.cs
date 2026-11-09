using System.Net;
using System.Text.Json;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.Aliyun;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class AliyunProviderTests
{
    private readonly TestContext _testContext;

    public AliyunProviderTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task StandaloneFactory_StreamsWithConfiguredProviderIdAndBearerAuthentication()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible),
            _testContext.CancellationToken);
        using var adapter = CreateAdapter(server.BaseAddress);

        var events = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest(),
            _testContext.CancellationToken);

        Assert.AreEqual("aliyun-cn", adapter.ProviderId);
        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());
        var request = Assert.ContainsSingle(server.Requests);
        Assert.AreEqual("/compatible-mode/v1/chat/completions", request.Path);
        Assert.AreEqual(
            $"Bearer {ProviderAdapterTestFactory.ApiKey}",
            request.Headers["Authorization"]);
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task DefaultCapabilities_EnableToolCallingAndRejectOtherUndeclaredFeatures()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.ToolCall(ProviderAdapterKind.OpenAICompatible),
            _testContext.CancellationToken);
        using var adapter = CreateAdapter(server.BaseAddress, modelId: "qwen-vl-max");
        ToolDescriptor[] tools =
        [
            new("weather", "host", "get_weather", "Gets weather.", "{\"type\":\"object\"}")
        ];

        Assert.IsTrue(adapter.Capabilities.Streaming);
        Assert.IsTrue(adapter.Capabilities.ToolCalling);
        Assert.IsFalse(adapter.Capabilities.Reasoning);
        Assert.IsFalse(adapter.Capabilities.Vision);
        Assert.IsFalse(adapter.Capabilities.Usage);

        var events = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest(tools),
            _testContext.CancellationToken);

        Assert.ContainsSingle(events.OfType<ToolCallCompleteProviderEvent>());
        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());
        await Assert.ThrowsExactlyAsync<RuntimeProviderException>(async () =>
        {
            await adapter.ValidateCapabilitiesAsync(
                [new ReasoningContentBlock("reasoning")],
                _testContext.CancellationToken);
        });
        await adapter.ValidateCapabilitiesAsync(
            [CreateImageBlock()],
            _testContext.CancellationToken);
        Assert.ContainsSingle(server.Requests);
    }

    [TestMethod]
    public void ProtocolCatalog_UsesDashScopeDefaultsAndRequiredBearerCredential()
    {
        var definition = StandaloneProviderProtocolCatalog.GetRequired("Aliyun");

        Assert.IsTrue(definition.IsVisibleInWizard);
        Assert.IsTrue(definition.IsApiKeyRequired);
        Assert.IsFalse(definition.IsBaseUrlRequired);
        Assert.AreEqual(AliyunModelCatalog.DefaultBaseUrl, definition.DefaultBaseUrl);
        Assert.AreEqual(AliyunModelCatalog.DefaultModelId, definition.DefaultModel);
        Assert.AreEqual("Authorization", definition.DefaultAuthenticationHeader);
        Assert.AreEqual("Bearer", definition.DefaultAuthenticationScheme);
        Assert.AreEqual("models", definition.DefaultProbePath);
    }

    [TestMethod]
    public void StandaloneConfigValidator_EmptyApiKey_IsRejectedForAliyun()
    {
        var config = CreateConfig(new Uri(AliyunModelCatalog.DefaultBaseUrl));
        config.Providers[0].ApiKey = string.Empty;
        config.DefaultProvider = "aliyun-cn";
        config.DefaultModel = ProviderAdapterTestFactory.ModelId;

        var errors = StandaloneConfigValidator.Validate(config, "config.json");

        Assert.ContainsSingle(errors.Where(static error =>
            error.Field.EndsWith(".apiKey", StringComparison.Ordinal)));
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task ExplicitCapabilities_EnableCompatibleToolCallingAndUsage()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.ToolCall(ProviderAdapterKind.OpenAICompatible),
            _testContext.CancellationToken);
        using var adapter = CreateAdapter(
            server.BaseAddress,
            new ProviderCapabilities(
                Streaming: true,
                ToolCalling: true,
                Vision: true,
                Reasoning: true,
                Files: true,
                Usage: true));
        ToolDescriptor[] tools =
        [
            new(
                "weather",
                "host",
                "get_weather",
                "Gets weather.",
                "{\"type\":\"object\",\"properties\":{\"city\":{\"type\":\"string\"}}}")
        ];

        await adapter.ValidateCapabilitiesAsync(
            [new ReasoningContentBlock("reasoning"), CreateImageBlock()],
            _testContext.CancellationToken);
        var events = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest(tools),
            _testContext.CancellationToken);

        Assert.ContainsSingle(events.OfType<ToolCallCompleteProviderEvent>());
        Assert.ContainsSingle(events.OfType<UsageUpdatedProviderEvent>());
        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task ExplicitReasoningCapability_MapsCompatibleReasoningEvents()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.Reasoning(ProviderAdapterKind.OpenAICompatible),
            _testContext.CancellationToken);
        using var adapter = CreateAdapter(
            server.BaseAddress,
            new ProviderCapabilities(Streaming: true, Reasoning: true));

        var events = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest(),
            _testContext.CancellationToken);

        Assert.ContainsSingle(events.OfType<ReasoningDeltaProviderEvent>());
        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task FetchModelsAsync_UsesCompatibleEndpointAndReturnsStableCatalog()
    {
        const string response =
            """
            {
              "data": [
                { "id": "qwen-z", "object": "model", "owned_by": "system" },
                { "id": "qwen-a", "object": "model", "owned_by": "system" },
                { "id": "qwen-z", "object": "model", "owned_by": "duplicate" },
                { "object": "model" }
              ],
              "object": "list"
            }
            """;
        await using var server = await FakeProviderServer.StartAsync(
            _ => FakeProviderResponse.Error(200, response),
            _testContext.CancellationToken);
        using var adapter = new AliyunProviderAdapter(
            new AliyunProviderOptions(
                "aliyun-cn",
                ProviderAdapterTestFactory.ApiKey,
                new Uri(server.BaseAddress, "compatible-mode/v1").AbsoluteUri,
                Models: [new ProviderModel("qwen-a", "Configured Qwen A")]),
            ProviderHttpClientFactory.Shared);

        var models = await adapter.FetchModelsAsync(_testContext.CancellationToken);

        Assert.HasCount(2, models);
        Assert.AreEqual("qwen-a", models[0].Id);
        Assert.AreEqual("Configured Qwen A", models[0].DisplayName);
        Assert.AreEqual("qwen-z", models[1].Id);
        Assert.AreEqual("qwen-z", models[1].DisplayName);
        var request = Assert.ContainsSingle(server.Requests);
        Assert.AreEqual("/compatible-mode/v1/models", request.Path);
        Assert.AreEqual(
            $"Bearer {ProviderAdapterTestFactory.ApiKey}",
            request.Headers["Authorization"]);
    }

    [TestMethod]
    [DataRow("{\"data\":[]}", false)]
    [DataRow("{}", true)]
    [DataRow("{not-json", true)]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task FetchModelsAsync_EmptyMissingOrInvalidPayload_HasStableBehavior(
        string response,
        bool shouldFail)
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => FakeProviderResponse.Error(200, response),
            _testContext.CancellationToken);
        using var adapter = CreateAdapter(server.BaseAddress);

        if (!shouldFail)
        {
            var models = await adapter.FetchModelsAsync(_testContext.CancellationToken);
            Assert.IsEmpty(models);
            return;
        }

        if (response.StartsWith("{not", StringComparison.Ordinal))
        {
            await Assert.ThrowsAsync<JsonException>(async () =>
            {
                await adapter.FetchModelsAsync(_testContext.CancellationToken);
            });
        }
        else
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            {
                await adapter.FetchModelsAsync(_testContext.CancellationToken);
            });
        }
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task FetchModelsAsync_ResponseExceedsLimit_FailsWithoutPartialCatalog()
    {
        var response = $"{{\"data\":[{{\"id\":\"{new string('x', 256)}\"}}]}}";
        await using var server = await FakeProviderServer.StartAsync(
            _ => FakeProviderResponse.Error(200, response),
            _testContext.CancellationToken);
        using var adapter = new AliyunProviderAdapter(
            new AliyunProviderOptions(
                "aliyun-cn",
                ProviderAdapterTestFactory.ApiKey,
                new Uri(server.BaseAddress, "compatible-mode/v1").AbsoluteUri,
                MaximumModelCatalogResponseBytes: 64),
            ProviderHttpClientFactory.Shared);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            await adapter.FetchModelsAsync(_testContext.CancellationToken);
        });
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task FetchModelsAsync_Cancellation_StopsResponseRead()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => new FakeProviderResponse(
                200,
                "application/json",
                [
                    new FakeProviderChunk("{\"data\":["),
                    new FakeProviderChunk("{\"id\":\"late\"}]}", TimeSpan.FromSeconds(5))
                ]),
            _testContext.CancellationToken);
        using var adapter = CreateAdapter(server.BaseAddress);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            _testContext.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await adapter.FetchModelsAsync(cts.Token);
        });
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task FetchModelsAsync_RateLimit_PreservesSafeMetadataAndRedactsBody()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => FakeProviderResponse.Error(
                429,
                $"{{\"error\":{{\"message\":\"{ProviderAdapterTestFactory.ApiKey} sensitive prompt\"}}}}",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["retry-after"] = "3",
                    ["x-request-id"] = "dashscope-request-models"
                }),
            _testContext.CancellationToken);
        using var adapter = CreateAdapter(server.BaseAddress);

        var exception = await Assert.ThrowsExactlyAsync<ProviderHttpException>(async () =>
        {
            await adapter.FetchModelsAsync(_testContext.CancellationToken);
        });

        Assert.AreEqual(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.AreEqual("3", exception.RetryAfter);
        Assert.AreEqual("dashscope-request-models", exception.RequestId);
        Assert.DoesNotContain(
            ProviderAdapterTestFactory.ApiKey,
            exception.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive prompt", exception.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(401, RuntimeErrorCodes.AuthenticationFailed, false)]
    [DataRow(429, RuntimeErrorCodes.ProviderRateLimited, true)]
    [DataRow(500, RuntimeErrorCodes.ProviderRequestFailed, true)]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_HttpFailure_MapsStatusRequestIdAndRetryability(
        int statusCode,
        string expectedCode,
        bool expectedRetryable)
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => FakeProviderResponse.Error(
                statusCode,
                $"{{\"error\":{{\"message\":\"{ProviderAdapterTestFactory.ApiKey} sensitive prompt\"}}}}",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["retry-after"] = "3",
                    ["x-request-id"] = "dashscope-request-chat"
                }),
            _testContext.CancellationToken);
        using var adapter = CreateAdapter(server.BaseAddress);

        var events = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest(),
            _testContext.CancellationToken);

        var failure = Assert.IsInstanceOfType<InvocationFailedProviderEvent>(
            Assert.ContainsSingle(events));
        Assert.AreEqual(expectedCode, failure.Error.Code);
        Assert.AreEqual(expectedRetryable, failure.Error.IsRetryable);
        Assert.DoesNotContain(
            ProviderAdapterTestFactory.ApiKey,
            failure.Error.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive prompt", failure.Error.Message, StringComparison.Ordinal);
        var details = failure.Error.ProviderDetails;
        Assert.IsTrue(details.HasValue);
        Assert.AreEqual(
            "dashscope-request-chat",
            details.Value.GetProperty("requestId").GetString());
        Assert.AreEqual("3", details.Value.GetProperty("retryAfter").GetString());
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_InvalidJson_ReturnsProtocolFailureWithoutCompletion()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.InvalidJson(ProviderAdapterKind.OpenAICompatible),
            _testContext.CancellationToken);
        using var adapter = CreateAdapter(server.BaseAddress);

        var events = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest(),
            _testContext.CancellationToken);

        var failure = Assert.ContainsSingle(events.OfType<InvocationFailedProviderEvent>());
        Assert.AreEqual(RuntimeErrorCodes.ProviderProtocolError, failure.Error.Code);
        Assert.IsEmpty(events.OfType<InvocationCompletedProviderEvent>());
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_DisconnectedStream_PreservesDeltaThenFails()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.Disconnected(ProviderAdapterKind.OpenAICompatible),
            _testContext.CancellationToken);
        using var adapter = CreateAdapter(server.BaseAddress);

        var events = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest(),
            _testContext.CancellationToken);

        Assert.AreEqual(
            "Hel",
            string.Concat(events.OfType<TextDeltaProviderEvent>().Select(static item => item.Delta)));
        var failure = Assert.ContainsSingle(events.OfType<InvocationFailedProviderEvent>());
        Assert.IsTrue(
            failure.Error.Code is RuntimeErrorCodes.ProviderProtocolError
                or RuntimeErrorCodes.ProviderRequestFailed);
        Assert.IsEmpty(events.OfType<InvocationCompletedProviderEvent>());
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_CancellationStopsSlowStream()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.SlowText(
                ProviderAdapterKind.OpenAICompatible,
                TimeSpan.FromSeconds(5)),
            _testContext.CancellationToken);
        using var adapter = CreateAdapter(server.BaseAddress);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            _testContext.CancellationToken);
        await using var enumerator = adapter
            .CompleteStreamingAsync(ProviderAdapterTestFactory.CreateRequest(), cts.Token)
            .GetAsyncEnumerator(cts.Token);

        Assert.IsTrue(await enumerator.MoveNextAsync());
        Assert.IsInstanceOfType<TextDeltaProviderEvent>(enumerator.Current);
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await enumerator.MoveNextAsync();
        });
    }

    [TestMethod]
    [TestCategory("External")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ConfiguredDashScope_ReturnsTerminalEvent()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("MADORIN_PROVIDER_SMOKE"),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                "Set MADORIN_PROVIDER_SMOKE=1 to run opt-in real Provider smoke tests.");
        }

        var apiKey = Environment.GetEnvironmentVariable("MADORIN_ALIYUN_API_KEY");
        var modelId = Environment.GetEnvironmentVariable("MADORIN_ALIYUN_MODEL_ID");
        var baseUrl = Environment.GetEnvironmentVariable("MADORIN_ALIYUN_BASE_URL");
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(modelId))
        {
            Assert.Inconclusive("MADORIN_ALIYUN_API_KEY and _MODEL_ID are required.");
        }

        using var adapter = new AliyunProviderAdapter(
            new AliyunProviderOptions(
                "aliyun-smoke",
                apiKey,
                string.IsNullOrWhiteSpace(baseUrl)
                    ? AliyunModelCatalog.DefaultBaseUrl
                    : baseUrl,
                Models: [new ProviderModel(modelId, modelId)]));
        var request = new RuntimeProviderRequest(
            Guid.NewGuid().ToString("N"),
            "smoke-agent",
            adapter.ProviderId,
            modelId,
            [
                new RuntimeProviderMessage(
                    RuntimeProviderRoles.User,
                    [new TextContentBlock("Reply with exactly OK.")])
            ],
            Tools: null,
            Temperature: 0,
            MaxTokens: 16,
            StructuredOutput: null);

        var events = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            request,
            _testContext.CancellationToken);

        Assert.IsTrue(events.Any(static providerEvent =>
            providerEvent is InvocationCompletedProviderEvent or InvocationFailedProviderEvent));
    }

    private static AliyunProviderAdapter CreateAdapter(
        Uri baseAddress,
        ProviderCapabilities? capabilities = null,
        string modelId = ProviderAdapterTestFactory.ModelId)
    {
        var config = CreateConfig(baseAddress, capabilities, modelId);
        var entry = config.Providers[0];
        return new AliyunProviderAdapter(
            new AliyunProviderOptions(
                entry.Name,
                entry.ApiKey,
                entry.BaseUrl,
                entry.Capabilities,
                [new ProviderModel(modelId, modelId)],
                string.IsNullOrWhiteSpace(entry.ConfigurationVersion)
                    ? "1"
                    : entry.ConfigurationVersion));
    }

    private static StandaloneConfig CreateConfig(
        Uri baseAddress,
        ProviderCapabilities? capabilities = null,
        string modelId = ProviderAdapterTestFactory.ModelId) =>
        new()
        {
            Providers =
            [
                new ProviderEntry
                {
                    Name = "aliyun-cn",
                    Protocol = "Aliyun",
                    BaseUrl = new Uri(baseAddress, "compatible-mode/v1").AbsoluteUri,
                    ApiKey = ProviderAdapterTestFactory.ApiKey,
                    Capabilities = capabilities,
                    Models = [modelId]
                }
            ]
        };

    private static BlobRefContentBlock CreateImageBlock() =>
        new(
            new BlobReference(
                "blob-image",
                4,
                new string('0', 64),
                "image/png",
                "invocation",
                DateTimeOffset.UtcNow.AddMinutes(1)));
}
