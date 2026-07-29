using System.Net;
using System.Text.Json;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.AzureOpenAI;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class AzureOpenAIProviderTests
{
    private readonly TestContext _testContext;

    public AzureOpenAIProviderTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task StandaloneFactory_SdkRequest_UsesDeploymentApiVersionAndApiKeyHeader()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible),
            _testContext.CancellationToken);
        using var adapter = CreateFactoryAdapter(server.BaseAddress);

        var events = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest(),
            _testContext.CancellationToken);

        Assert.AreEqual("azure-production", adapter.ProviderId);
        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());
        var request = Assert.ContainsSingle(server.Requests);
        Assert.AreEqual(
            $"/openai/deployments/{ProviderAdapterTestFactory.ModelId}/chat/completions",
            request.Path);
        Assert.AreEqual("?api-version=2024-10-21", request.QueryString);
        Assert.AreEqual(ProviderAdapterTestFactory.ApiKey, request.Headers["api-key"]);
        Assert.IsFalse(request.Headers.ContainsKey("Authorization"));
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task MultipleDeployments_RouteToIndependentDeploymentPaths()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible),
            _testContext.CancellationToken);
        using var adapter = new AzureOpenAIProviderAdapter(
            new AzureOpenAIProviderOptions(
                "azure-production",
                ProviderAdapterTestFactory.ApiKey,
                server.BaseAddress.AbsoluteUri,
                [
                    new ProviderModel("deployment-a", "Deployment A"),
                    new ProviderModel("deployment-b", "Deployment B")
                ]),
            ProviderHttpClientFactory.Shared);

        await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest() with { ModelId = "deployment-a" },
            _testContext.CancellationToken);
        await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest() with { ModelId = "deployment-b" },
            _testContext.CancellationToken);

        Assert.HasCount(2, server.Requests);
        var paths = server.Requests.Select(static request => request.Path).ToArray();
        Assert.Contains("/openai/deployments/deployment-a/chat/completions", paths);
        Assert.Contains("/openai/deployments/deployment-b/chat/completions", paths);
        Assert.IsTrue(server.Requests.All(static request =>
            request.QueryString.Equals("?api-version=2024-10-21", StringComparison.Ordinal)));
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task UnknownDeployment_FailsBeforeHttp()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => FakeProviderResponse.Error(500, "HTTP should not be reached"),
            _testContext.CancellationToken);
        using var adapter = CreateFactoryAdapter(server.BaseAddress);

        var events = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest() with { ModelId = "not-configured" },
            _testContext.CancellationToken);

        var failure = Assert.IsInstanceOfType<InvocationFailedProviderEvent>(
            Assert.ContainsSingle(events));
        Assert.AreEqual(RuntimeErrorCodes.CapabilityInsufficient, failure.Error.Code);
        Assert.IsEmpty(server.Requests);
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task DefaultCapabilities_EnableToolCalling()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.ToolCall(ProviderAdapterKind.OpenAICompatible),
            _testContext.CancellationToken);
        using var adapter = CreateFactoryAdapter(server.BaseAddress);
        ToolDescriptor[] tools =
        [
            new("weather", "host", "get_weather", "Gets weather.", "{\"type\":\"object\"}")
        ];

        Assert.IsTrue(adapter.Capabilities.Streaming);
        Assert.IsTrue(adapter.Capabilities.ToolCalling);
        Assert.IsFalse(adapter.Capabilities.Usage);
        var events = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest(tools),
            _testContext.CancellationToken);

        Assert.ContainsSingle(events.OfType<ToolCallCompleteProviderEvent>());
        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());
        Assert.ContainsSingle(server.Requests);
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task ExplicitCapabilities_EnableCompatibleToolCallingAndUsage()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.ToolCall(ProviderAdapterKind.OpenAICompatible),
            _testContext.CancellationToken);
        var capabilities = new ProviderCapabilities(
            Streaming: true,
            ToolCalling: true,
            Usage: true);
        using var adapter = CreateDirectAdapter(server.BaseAddress, capabilities);
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
    public async Task FetchModelsAsync_ReturnsConfiguredDeploymentsWithoutHttp()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => FakeProviderResponse.Error(500, "HTTP should not be reached"),
            _testContext.CancellationToken);
        using var adapter = CreateFactoryAdapter(server.BaseAddress);

        var models = await adapter.FetchModelsAsync(_testContext.CancellationToken);

        var model = Assert.ContainsSingle(models);
        Assert.AreEqual(ProviderAdapterTestFactory.ModelId, model.Id);
        Assert.IsEmpty(server.Requests);
    }

    [TestMethod]
    [DataRow(401, RuntimeErrorCodes.AuthenticationFailed, false)]
    [DataRow(429, RuntimeErrorCodes.ProviderRateLimited, true)]
    [DataRow(500, RuntimeErrorCodes.ProviderRequestFailed, true)]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_HttpFailure_MapsAzureMetadataAndRedactsBody(
        int statusCode,
        string expectedCode,
        bool expectedRetryable)
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => FakeProviderResponse.Error(
                statusCode,
                $"{{\"error\":{{\"code\":\"DeploymentNotFound\",\"message\":\"{ProviderAdapterTestFactory.ApiKey} sensitive prompt\"}}}}",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["retry-after"] = "4",
                    ["apim-request-id"] = "azure-request-chat"
                }),
            _testContext.CancellationToken);
        using var adapter = CreateDirectAdapter(server.BaseAddress);

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
        Assert.AreEqual(statusCode, details.Value.GetProperty("statusCode").GetInt32());
        Assert.AreEqual("4", details.Value.GetProperty("retryAfter").GetString());
        Assert.AreEqual("azure-request-chat", details.Value.GetProperty("requestId").GetString());
        Assert.AreEqual("DeploymentNotFound", details.Value.GetProperty("providerCode").GetString());
        Assert.DoesNotContain(
            ProviderAdapterTestFactory.ApiKey,
            details.Value.GetRawText(),
            StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_UnusableErrorBody_UsesSafeHttpMetadata(
        bool responseExceedsLimit)
    {
        var body = responseExceedsLimit ? new string('x', 256) : "{not-json";
        await using var server = await FakeProviderServer.StartAsync(
            _ => FakeProviderResponse.Error(
                500,
                body,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["apim-request-id"] = "azure-safe-request"
                }),
            _testContext.CancellationToken);
        using var adapter = CreateDirectAdapter(
            server.BaseAddress,
            maximumErrorResponseBytes: responseExceedsLimit ? 64 : 65_536);

        var events = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest(),
            _testContext.CancellationToken);

        var failure = Assert.IsInstanceOfType<InvocationFailedProviderEvent>(
            Assert.ContainsSingle(events));
        Assert.AreEqual(RuntimeErrorCodes.ProviderRequestFailed, failure.Error.Code);
        Assert.IsTrue(failure.Error.IsRetryable);
        Assert.DoesNotContain(body, failure.Error.Message, StringComparison.Ordinal);
        var details = failure.Error.ProviderDetails;
        Assert.IsTrue(details.HasValue);
        Assert.AreEqual("azure-safe-request", details.Value.GetProperty("requestId").GetString());
        Assert.IsFalse(details.Value.TryGetProperty("providerCode", out _));
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_InvalidJson_ReturnsProtocolFailureWithoutCompletion()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.InvalidJson(ProviderAdapterKind.OpenAICompatible),
            _testContext.CancellationToken);
        using var adapter = CreateDirectAdapter(server.BaseAddress);

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
        using var adapter = CreateDirectAdapter(server.BaseAddress);

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
        using var adapter = CreateDirectAdapter(server.BaseAddress);
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
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task UpdateCredentialsAsync_UpdatesCachedDeploymentWithoutBearerHeader()
    {
        const string updatedApiKey = "azure-updated-key";
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible),
            _testContext.CancellationToken);
        using var adapter = CreateDirectAdapter(server.BaseAddress);

        await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest(),
            _testContext.CancellationToken);
        await adapter.UpdateCredentialsAsync(
            new CredentialsUpdateParameters(
                "run-azure",
                adapter.ProviderId,
                updatedApiKey),
            _testContext.CancellationToken);
        await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest(),
            _testContext.CancellationToken);

        Assert.HasCount(2, server.Requests);
        Assert.AreEqual(
            ProviderAdapterTestFactory.ApiKey,
            server.Requests[0].Headers["api-key"]);
        Assert.AreEqual(updatedApiKey, server.Requests[1].Headers["api-key"]);
        Assert.IsTrue(server.Requests.All(static request =>
            !request.Headers.ContainsKey("Authorization")));
    }

    [TestMethod]
    public void StandaloneConfig_ApiVersion_SourceGeneratedJsonRoundTrips()
    {
        var config = CreateAzureConfig(new Uri("https://example.openai.azure.com"));
        config.Providers[0].ApiVersion = "2025-04-01-preview";

        var json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.StandaloneConfig);
        var restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.StandaloneConfig);

        Assert.IsNotNull(restored);
        Assert.AreEqual("2025-04-01-preview", Assert.ContainsSingle(restored.Providers).ApiVersion);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("2024-10-21&secret=true")]
    [DataRow("2024-10-21 preview")]
    public void StandaloneConfigValidator_InvalidAzureApiVersion_ReturnsFieldError(
        string apiVersion)
    {
        var config = CreateAzureConfig(new Uri("https://example.openai.azure.com"));
        config.Providers[0].ApiVersion = apiVersion;

        var errors = StandaloneConfigValidator.Validate(config, "config.json");

        Assert.ContainsSingle(errors.Where(static error =>
            error.Field.EndsWith(".apiVersion", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ProtocolCatalog_RemainsHiddenUntilRealAzureValidation()
    {
        var definition = StandaloneProviderProtocolCatalog.GetRequired("Azure OpenAI");

        Assert.IsFalse(definition.IsVisibleInWizard);
        Assert.IsTrue(definition.IsApiKeyRequired);
        Assert.IsTrue(definition.IsBaseUrlRequired);
        Assert.AreEqual(string.Empty, definition.DefaultBaseUrl);
        Assert.AreEqual(string.Empty, definition.DefaultModel);
        Assert.AreEqual("api-key", definition.DefaultAuthenticationHeader);
        Assert.IsNull(definition.DefaultAuthenticationScheme);
        Assert.IsFalse(StandaloneProviderProtocolCatalog.WizardEntries.Contains(definition));
    }

    [TestMethod]
    [TestCategory("External")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ConfiguredAzureOpenAI_ReturnsTerminalEvent()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("MADORIN_PROVIDER_SMOKE"),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                "Set MADORIN_PROVIDER_SMOKE=1 to run opt-in real Provider smoke tests.");
        }

        var apiKey = Environment.GetEnvironmentVariable("MADORIN_AZURE_OPENAI_API_KEY");
        var modelId = Environment.GetEnvironmentVariable("MADORIN_AZURE_OPENAI_MODEL_ID");
        var baseUrl = Environment.GetEnvironmentVariable("MADORIN_AZURE_OPENAI_BASE_URL");
        var apiVersion = Environment.GetEnvironmentVariable("MADORIN_AZURE_OPENAI_API_VERSION");
        if (string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(modelId)
            || string.IsNullOrWhiteSpace(baseUrl))
        {
            Assert.Inconclusive(
                "MADORIN_AZURE_OPENAI_API_KEY, _MODEL_ID, and _BASE_URL are required.");
        }

        using var adapter = new AzureOpenAIProviderAdapter(
            new AzureOpenAIProviderOptions(
                "azure-smoke",
                apiKey,
                baseUrl,
                [new ProviderModel(modelId, modelId)],
                string.IsNullOrWhiteSpace(apiVersion)
                    ? AzureOpenAIProtocol.DefaultApiVersion
                    : apiVersion));
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

    private static AzureOpenAIProviderAdapter CreateFactoryAdapter(Uri baseAddress)
    {
        var config = new StandaloneConfig
        {
            Providers =
            [
                new ProviderEntry
                {
                    Name = "azure-production",
                    Protocol = "Azure OpenAI",
                    BaseUrl = baseAddress.AbsoluteUri,
                    ApiKey = ProviderAdapterTestFactory.ApiKey,
                    ApiVersion = "2024-10-21",
                    Models = [ProviderAdapterTestFactory.ModelId]
                }
            ]
        };
        var adapter = StandaloneProviderFactory.CreateAdapter(config, "azure-production");
        return Assert.IsInstanceOfType<AzureOpenAIProviderAdapter>(adapter);
    }

    private static AzureOpenAIProviderAdapter CreateDirectAdapter(
        Uri baseAddress,
        ProviderCapabilities? capabilities = null,
        int maximumErrorResponseBytes = 65_536) =>
        new(
            new AzureOpenAIProviderOptions(
                "azure-production",
                ProviderAdapterTestFactory.ApiKey,
                baseAddress.AbsoluteUri,
                [
                    new ProviderModel(
                        ProviderAdapterTestFactory.ModelId,
                        "Test deployment",
                        Capabilities: capabilities)
                ],
                Capabilities: capabilities,
                MaximumErrorResponseBytes: maximumErrorResponseBytes),
            ProviderHttpClientFactory.Shared);

    private static StandaloneConfig CreateAzureConfig(Uri baseAddress) =>
        new()
        {
            DefaultProvider = "azure-production",
            DefaultModel = ProviderAdapterTestFactory.ModelId,
            Providers =
            [
                new ProviderEntry
                {
                    Name = "azure-production",
                    Protocol = "Azure OpenAI",
                    BaseUrl = baseAddress.AbsoluteUri,
                    ApiKey = ProviderAdapterTestFactory.ApiKey,
                    ApiVersion = AzureOpenAIProtocol.DefaultApiVersion,
                    Models = [ProviderAdapterTestFactory.ModelId]
                }
            ]
        };
}
