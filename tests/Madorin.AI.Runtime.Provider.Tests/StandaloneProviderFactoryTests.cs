using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server.ProviderWorkers;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class StandaloneProviderFactoryTests
{
    private readonly TestContext _testContext;

    public StandaloneProviderFactoryTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    [DataRow("OpenAI")]
    [DataRow("Anthropic")]
    [DataRow("OpenAI Compatible")]
    [DataRow("DeepSeek")]
    [DataRow("Kimi")]
    [DataRow("Aliyun")]
    [DataRow("Azure OpenAI")]
    [DataRow("Gemini")]
    [DataRow("GLM")]
    [DataRow("Custom")]
    [DataRow("Ollama")]
    public async Task Create_ProviderWithoutDeclaredCapabilities_EnablesToolCalling(string protocol)
    {
        const string providerId = "default-tool-calling-provider";
        var config = new StandaloneConfig
        {
            Providers =
            [
                new ProviderEntry
                {
                    Name = providerId,
                    Protocol = protocol,
                    BaseUrl = "https://provider.example/v1",
                    ApiKey = ProviderAdapterTestFactory.ApiKey,
                    Models = [ProviderAdapterTestFactory.ModelId]
                }
            ]
        };
        await using var supervisor = new ProviderWorkerSupervisor(AppContext.BaseDirectory);
        var resolver = StandaloneProviderFactory.Create(config, supervisor);
        var adapter = resolver(CreateSelection(providerId));
        Assert.IsTrue(adapter.Capabilities.ToolCalling);
        Assert.IsInstanceOfType<ProviderWorkerAdapter>(adapter);
    }

    [TestMethod]
    [DataRow(ProviderAdapterKind.OpenAI)]
    [DataRow(ProviderAdapterKind.Anthropic)]
    [DataRow(ProviderAdapterKind.OpenAICompatible)]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Create_ConfiguredProfile_ReusesCorrectAdapterAndForwardsConfiguration(
        ProviderAdapterKind kind)
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(kind),
            _testContext.CancellationToken);
        var providerId = $"profile-{kind}";
        var protocol = kind switch
        {
            ProviderAdapterKind.OpenAI => "OpenAI",
            ProviderAdapterKind.Anthropic => "Anthropic",
            ProviderAdapterKind.OpenAICompatible => "OpenAI Compatible",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        var baseUrl = kind is ProviderAdapterKind.OpenAI or ProviderAdapterKind.OpenAICompatible
            ? new Uri(server.BaseAddress, "v1").AbsoluteUri
            : server.BaseAddress.AbsoluteUri;
        var config = new StandaloneConfig
        {
            Providers =
            [
                new ProviderEntry
                {
                    Name = providerId,
                    Protocol = protocol,
                    BaseUrl = baseUrl,
                    ApiKey = ProviderAdapterTestFactory.ApiKey,
                    AuthenticationHeader = "X-Provider-Key",
                    AuthenticationScheme = "Token",
                    Models = [ProviderAdapterTestFactory.ModelId]
                }
            ]
        };
        await using var supervisor = new ProviderWorkerSupervisor(AppContext.BaseDirectory);
        var resolver = StandaloneProviderFactory.Create(config, supervisor);

        var adapter = resolver(CreateSelection(providerId));
        var cachedAdapter = resolver(CreateSelection(providerId));

        Assert.AreSame(adapter, cachedAdapter);
        Assert.AreEqual(providerId, adapter.ProviderId);
        Assert.HasCount(1, adapter.Models);
        Assert.AreEqual(ProviderAdapterTestFactory.ModelId, adapter.Models[0].Id);
        Assert.IsInstanceOfType<ProviderWorkerAdapter>(adapter);

        var events = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest() with { ProviderId = providerId },
            _testContext.CancellationToken);

        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());
        var request = Assert.ContainsSingle(server.Requests);
        Assert.EndsWith(ProviderProtocolScripts.ExpectedPathSuffix(kind), request.Path);
        Assert.Contains(ProviderAdapterTestFactory.ModelId, request.Body, StringComparison.Ordinal);
        AssertAuthentication(kind, request.Headers);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Create_SameProtocolProfiles_IsolatesAdaptersCredentialsAndRequests()
    {
        await using var firstServer = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible),
            _testContext.CancellationToken);
        await using var secondServer = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible),
            _testContext.CancellationToken);
        const string firstProviderId = "compatible-first";
        const string secondProviderId = "compatible-second";
        const string firstModelId = "first-model";
        const string secondModelId = "second-model";
        const string firstApiKey = "first-provider-key";
        const string secondApiKey = "second-provider-key";
        var config = new StandaloneConfig
        {
            Providers =
            [
                new ProviderEntry
                {
                    Name = firstProviderId,
                    Protocol = "OpenAI Compatible",
                    BaseUrl = new Uri(firstServer.BaseAddress, "v1").AbsoluteUri,
                    ApiKey = firstApiKey,
                    Models = [firstModelId]
                },
                new ProviderEntry
                {
                    Name = secondProviderId,
                    Protocol = "OpenAI Compatible",
                    BaseUrl = new Uri(secondServer.BaseAddress, "v1").AbsoluteUri,
                    ApiKey = secondApiKey,
                    Models = [secondModelId]
                }
            ]
        };
        await using var supervisor = new ProviderWorkerSupervisor(AppContext.BaseDirectory);
        var resolver = StandaloneProviderFactory.Create(config, supervisor);

        var firstAdapter = resolver(CreateSelection(firstProviderId, firstModelId));
        var secondAdapter = resolver(CreateSelection(secondProviderId, secondModelId));
        using var firstLifetime = firstAdapter as IDisposable;
        using var secondLifetime = secondAdapter as IDisposable;
        Assert.IsInstanceOfType<ProviderWorkerAdapter>(firstAdapter);
        Assert.IsInstanceOfType<ProviderWorkerAdapter>(secondAdapter);

        Assert.AreNotSame(firstAdapter, secondAdapter);
        Assert.AreSame(firstAdapter, resolver(CreateSelection(firstProviderId, firstModelId)));
        Assert.AreSame(secondAdapter, resolver(CreateSelection(secondProviderId, secondModelId)));
        Assert.AreEqual(firstProviderId, firstAdapter.ProviderId);
        Assert.AreEqual(secondProviderId, secondAdapter.ProviderId);

        var firstRequest = ProviderAdapterTestFactory.CreateRequest() with
        {
            InvocationId = "inv-first",
            ProviderId = firstProviderId,
            ModelId = firstModelId
        };
        var secondRequest = ProviderAdapterTestFactory.CreateRequest() with
        {
            InvocationId = "inv-second",
            ProviderId = secondProviderId,
            ModelId = secondModelId
        };
        await Task.WhenAll(
            ProviderAdapterTestFactory.CollectAsync(
                firstAdapter,
                firstRequest,
                _testContext.CancellationToken),
            ProviderAdapterTestFactory.CollectAsync(
                secondAdapter,
                secondRequest,
                _testContext.CancellationToken));

        var firstHttpRequest = Assert.ContainsSingle(firstServer.Requests);
        var secondHttpRequest = Assert.ContainsSingle(secondServer.Requests);
        Assert.Contains(firstModelId, firstHttpRequest.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(secondModelId, firstHttpRequest.Body, StringComparison.Ordinal);
        Assert.AreEqual($"Bearer {firstApiKey}", firstHttpRequest.Headers["Authorization"]);
        Assert.Contains(secondModelId, secondHttpRequest.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(firstModelId, secondHttpRequest.Body, StringComparison.Ordinal);
        Assert.AreEqual($"Bearer {secondApiKey}", secondHttpRequest.Headers["Authorization"]);
    }

    private static NextTurnSelection CreateSelection(
        string providerId,
        string modelId = ProviderAdapterTestFactory.ModelId) =>
        new(
            1,
            RuntimeMode.Expert,
            new DefaultSelection(providerId, modelId),
            new ExpertModeOptions(new AgentRef("agent", "v1", "test")));

    private static void AssertAuthentication(
        ProviderAdapterKind kind,
        IReadOnlyDictionary<string, string> headers)
    {
        var headerName = kind switch
        {
            ProviderAdapterKind.Anthropic => "x-api-key",
            ProviderAdapterKind.OpenAICompatible => "X-Provider-Key",
            _ => "Authorization"
        };
        var expected = kind switch
        {
            ProviderAdapterKind.Anthropic => ProviderAdapterTestFactory.ApiKey,
            ProviderAdapterKind.OpenAICompatible => $"Token {ProviderAdapterTestFactory.ApiKey}",
            _ => $"Bearer {ProviderAdapterTestFactory.ApiKey}"
        };

        Assert.IsTrue(headers.TryGetValue(headerName, out var actual));
        Assert.AreEqual(expected, actual);
    }
}
