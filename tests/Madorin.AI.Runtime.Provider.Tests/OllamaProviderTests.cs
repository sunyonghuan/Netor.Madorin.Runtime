using System.Text.Json;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.Ollama;
using Madorin.AI.Runtime.Server.ProviderWorkers;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class OllamaProviderTests
{
    private readonly TestContext _testContext;

    public OllamaProviderTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task StandaloneFactory_DefaultNoAuthentication_StreamsWithUsage()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible),
            _testContext.CancellationToken);
        using var adapter = CreateAdapter(server.BaseAddress);

        var events = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest(),
            _testContext.CancellationToken);

        Assert.AreEqual("ollama-local", adapter.ProviderId);
        Assert.ContainsSingle(events.OfType<UsageUpdatedProviderEvent>());
        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        var request = Assert.ContainsSingle(server.Requests);
        Assert.AreEqual("/v1/chat/completions", request.Path);
        Assert.IsFalse(request.Headers.ContainsKey("Authorization"));
    }

    [TestMethod]
    public void StandaloneConfigValidator_EmptyApiKey_IsValidForOllama()
    {
        var config = CreateConfig(new Uri(OllamaModelCatalog.DefaultBaseUrl));
        config.DefaultProvider = "ollama-local";
        config.DefaultModel = ProviderAdapterTestFactory.ModelId;

        var errors = StandaloneConfigValidator.Validate(config, "config.json");

        Assert.IsEmpty(errors);
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task StandaloneFactory_ProxyAuthentication_UsesConfiguredHeader()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible),
            _testContext.CancellationToken);
        var config = CreateConfig(server.BaseAddress);
        config.Providers[0].ApiKey = ProviderAdapterTestFactory.ApiKey;
        config.Providers[0].AuthenticationHeader = "X-Ollama-Key";
        config.Providers[0].AuthenticationScheme = "Token";
        await using var supervisor = new ProviderWorkerSupervisor(AppContext.BaseDirectory);
        var created = StandaloneProviderFactory.CreateAdapter(config, "ollama-local", supervisor);

        await ProviderAdapterTestFactory.CollectAsync(
            created,
            ProviderAdapterTestFactory.CreateRequest(),
            _testContext.CancellationToken);

        var request = Assert.ContainsSingle(server.Requests);
        Assert.AreEqual(
            $"Token {ProviderAdapterTestFactory.ApiKey}",
            request.Headers["X-Ollama-Key"]);
        Assert.IsFalse(request.Headers.ContainsKey("Authorization"));
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task FetchModelsAsync_RemovesV1AndReturnsStableCatalog()
    {
        const string response =
            """
            {
              "models": [
                { "name": "zeta:latest", "details": { "family": "qwen2" } },
                { "name": "alpha:latest", "details": { "family": "llama" } },
                { "name": "zeta:latest", "details": { "family": "duplicate" } },
                { "details": { "family": "missing-name" } }
              ]
            }
            """;
        await using var server = await FakeProviderServer.StartAsync(
            _ => FakeProviderResponse.Error(200, response),
            _testContext.CancellationToken);
        using var adapter = CreateAdapter(server.BaseAddress);

        var models = await adapter.FetchModelsAsync(_testContext.CancellationToken);

        Assert.HasCount(2, models);
        Assert.AreEqual("alpha:latest", models[0].Id);
        Assert.AreEqual("alpha:latest (llama)", models[0].DisplayName);
        Assert.AreEqual("zeta:latest", models[1].Id);
        Assert.AreEqual("zeta:latest (qwen2)", models[1].DisplayName);
        var request = Assert.ContainsSingle(server.Requests);
        Assert.AreEqual("/api/tags", request.Path);
        Assert.IsFalse(request.Headers.ContainsKey("Authorization"));
    }

    [TestMethod]
    [DataRow("{\"models\":[]}", false)]
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

        if (shouldFail)
        {
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

            return;
        }

        var models = await adapter.FetchModelsAsync(_testContext.CancellationToken);
        Assert.IsEmpty(models);
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task FetchModelsAsync_ResponseExceedsLimit_FailsWithoutPartialCatalog()
    {
        var response = $"{{\"models\":[{{\"name\":\"{new string('x', 256)}\"}}]}}";
        await using var server = await FakeProviderServer.StartAsync(
            _ => FakeProviderResponse.Error(200, response),
            _testContext.CancellationToken);
        using var adapter = CreateAdapter(server.BaseAddress, maximumResponseBytes: 64);

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
                    new FakeProviderChunk("{\"models\":["),
                    new FakeProviderChunk("{\"name\":\"late\"}]}", TimeSpan.FromSeconds(5))
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
    public async Task CompleteStreamingAsync_DeclaredToolCalling_UsesCompatibleProtocol()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.ToolCall(ProviderAdapterKind.OpenAICompatible),
            _testContext.CancellationToken);
        var capabilities = new ProviderCapabilities(
            Streaming: true,
            ToolCalling: true,
            Usage: true);
        using var adapter = CreateAdapter(server.BaseAddress, capabilities: capabilities);
        ToolDescriptor[] tools =
        [
            new(
                "weather",
                "host",
                "get_weather",
                "Gets weather.",
                "{\"type\":\"object\",\"properties\":{\"city\":{\"type\":\"string\"}}}")
        ];

        var events = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest(tools),
            _testContext.CancellationToken);

        Assert.ContainsSingle(events.OfType<ToolCallCompleteProviderEvent>());
        Assert.ContainsSingle(events.OfType<UsageUpdatedProviderEvent>());
        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
    }

    [TestMethod]
    [TestCategory("External")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ConfiguredLocalOllama_ReturnsTerminalEvent()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("MADORIN_PROVIDER_SMOKE"),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                "Set MADORIN_PROVIDER_SMOKE=1 to run opt-in real Provider smoke tests.");
        }

        var baseUrl = Environment.GetEnvironmentVariable("MADORIN_OLLAMA_BASE_URL");
        var modelId = Environment.GetEnvironmentVariable("MADORIN_OLLAMA_MODEL_ID");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(modelId))
        {
            Assert.Inconclusive("MADORIN_OLLAMA_BASE_URL and _MODEL_ID are required.");
        }

        using var adapter = new OllamaProviderAdapter(
            new OllamaProviderOptions(
                "ollama-smoke",
                baseUrl,
                Models: [new ProviderModel(modelId, modelId)]));
        var request = new RuntimeProviderRequest(
            Guid.NewGuid().ToString("N"),
            "smoke-agent",
            adapter.ProviderId,
            modelId,
            [
                new RuntimeProviderMessage(
                    RuntimeProviderRoles.User,
                    [new TextContentBlock("Reply with OK.")])
            ],
            MaxTokens: 16);

        var events = await ProviderAdapterTestFactory.CollectAsync(adapter, request);

        Assert.IsTrue(events.Any(static item => item is TextDeltaProviderEvent));
        Assert.IsTrue(events.Any(static item => item is InvocationCompletedProviderEvent));
        Assert.IsFalse(events.Any(static item => item is InvocationFailedProviderEvent));
    }

    private static OllamaProviderAdapter CreateAdapter(
        Uri baseAddress,
        int maximumResponseBytes = 65_536,
        ProviderCapabilities? capabilities = null) =>
        new(
            new OllamaProviderOptions(
                "ollama-local",
                new Uri(baseAddress, "v1").AbsoluteUri,
                Capabilities: capabilities,
                Models: [new ProviderModel(ProviderAdapterTestFactory.ModelId, "Test model")],
                MaximumModelCatalogResponseBytes: maximumResponseBytes));

    private static StandaloneConfig CreateConfig(Uri baseAddress) => new()
    {
        Providers =
        [
            new ProviderEntry
            {
                Name = "ollama-local",
                Protocol = "Ollama",
                BaseUrl = new Uri(baseAddress, "v1").AbsoluteUri,
                Models = [ProviderAdapterTestFactory.ModelId]
            }
        ]
    };
}
