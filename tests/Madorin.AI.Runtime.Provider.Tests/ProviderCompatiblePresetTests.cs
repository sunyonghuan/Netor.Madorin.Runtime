using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.Gemini;
using Madorin.AI.Runtime.Providers.GLM;
using Madorin.AI.Runtime.Providers.OpenAICompatible;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ProviderCompatiblePresetTests
{
    private readonly TestContext _testContext;

    public ProviderCompatiblePresetTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    [DataRow("Gemini")]
    [DataRow("GLM")]
    [DataRow("Custom")]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task StandaloneFactory_CompatiblePreset_StreamsWithConfiguredProviderId(
        string protocol)
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible),
            _testContext.CancellationToken);
        var providerId = $"profile-{protocol}";
        var config = new StandaloneConfig
        {
            Providers =
            [
                new ProviderEntry
                {
                    Name = providerId,
                    Protocol = protocol,
                    BaseUrl = new Uri(server.BaseAddress, "v1").AbsoluteUri,
                    ApiKey = ProviderAdapterTestFactory.ApiKey,
                    Models = [ProviderAdapterTestFactory.ModelId]
                }
            ]
        };
        var adapter = StandaloneProviderFactory.CreateAdapter(config, providerId);
        using var adapterLifetime = adapter as IDisposable;

        Assert.AreEqual(providerId, adapter.ProviderId);
        AssertPresetType(protocol, adapter);
        var events = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest(),
            _testContext.CancellationToken);

        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());
        var request = Assert.ContainsSingle(server.Requests);
        Assert.AreEqual(
            $"Bearer {ProviderAdapterTestFactory.ApiKey}",
            request.Headers["Authorization"]);
    }

    [TestMethod]
    [DataRow("Gemini")]
    [DataRow("GLM")]
    [DataRow("Custom")]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task StandaloneFactory_CompatiblePreset_EnablesToolCallingByDefault(
        string protocol)
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.ToolCall(ProviderAdapterKind.OpenAICompatible),
            _testContext.CancellationToken);
        var adapter = CreateAdapter(protocol, server.BaseAddress);
        using var adapterLifetime = adapter as IDisposable;
        ToolDescriptor[] tools =
        [
            new("weather", "host", "get_weather", "Gets weather.", "{\"type\":\"object\"}")
        ];

        var events = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest(tools),
            _testContext.CancellationToken);

        Assert.IsTrue(adapter.Capabilities.ToolCalling);
        Assert.ContainsSingle(events.OfType<ToolCallCompleteProviderEvent>());
        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());
        Assert.ContainsSingle(server.Requests);
    }

    [TestMethod]
    [DataRow("Gemini")]
    [DataRow("GLM")]
    [DataRow("Custom")]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task StandaloneFactory_CompatiblePreset_MapsRedactedAuthenticationFailure(
        string protocol)
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => FakeProviderResponse.Error(
                401,
                $"{{\"error\":{{\"message\":\"{ProviderAdapterTestFactory.ApiKey} sensitive prompt\"}}}}"),
            _testContext.CancellationToken);
        var adapter = CreateAdapter(protocol, server.BaseAddress);
        using var adapterLifetime = adapter as IDisposable;

        var events = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest(),
            _testContext.CancellationToken);

        var failure = Assert.IsInstanceOfType<InvocationFailedProviderEvent>(
            Assert.ContainsSingle(events));
        Assert.AreEqual(RuntimeErrorCodes.AuthenticationFailed, failure.Error.Code);
        Assert.DoesNotContain(
            ProviderAdapterTestFactory.ApiKey,
            failure.Error.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive prompt", failure.Error.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("Gemini")]
    [DataRow("GLM")]
    [DataRow("Custom")]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task StandaloneFactory_CompatiblePreset_CancellationStopsSlowStream(
        string protocol)
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.SlowText(
                ProviderAdapterKind.OpenAICompatible,
                TimeSpan.FromSeconds(5)),
            _testContext.CancellationToken);
        var adapter = CreateAdapter(protocol, server.BaseAddress);
        using var adapterLifetime = adapter as IDisposable;
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
    public async Task GeminiFetchModelsAsync_UsesSourceGeneratedContractAndStableOrdering()
    {
        const string response =
            """
            {
              "data": [
                { "id": "gemini-z", "display_name": "Gemini Z", "owned_by": "google" },
                { "id": "gemini-a", "owned_by": "google" },
                { "id": "gemini-z", "display_name": "duplicate" }
              ]
            }
            """;
        await using var server = await FakeProviderServer.StartAsync(
            _ => FakeProviderResponse.Error(200, response),
            _testContext.CancellationToken);
        using var adapter = new GeminiProviderAdapter(
            new GeminiProviderOptions(
                "gemini-profile",
                ProviderAdapterTestFactory.ApiKey,
                new Uri(server.BaseAddress, "v1").AbsoluteUri,
                Models: [new ProviderModel("gemini-a", "Configured A")]),
            ProviderHttpClientFactory.Shared);

        var models = await adapter.FetchModelsAsync(_testContext.CancellationToken);

        Assert.HasCount(2, models);
        Assert.AreEqual("gemini-a", models[0].Id);
        Assert.AreEqual("gemini-a", models[0].DisplayName);
        Assert.AreEqual("gemini-z", models[1].Id);
        Assert.AreEqual("Gemini Z", models[1].DisplayName);
        var request = Assert.ContainsSingle(server.Requests);
        Assert.AreEqual("/v1/models", request.Path);
        Assert.AreEqual(
            $"Bearer {ProviderAdapterTestFactory.ApiKey}",
            request.Headers["Authorization"]);
    }

    [TestMethod]
    public void ProtocolCatalog_NewPresetsHaveExpectedVisibilityAndRequirements()
    {
        var gemini = StandaloneProviderProtocolCatalog.GetRequired("Gemini");
        var glm = StandaloneProviderProtocolCatalog.GetRequired("GLM");
        var custom = StandaloneProviderProtocolCatalog.GetRequired("Custom");

        Assert.IsTrue(gemini.IsVisibleInWizard);
        Assert.IsTrue(gemini.IsApiKeyRequired);
        Assert.IsTrue(glm.IsVisibleInWizard);
        Assert.IsTrue(glm.IsBaseUrlRequired);
        Assert.IsTrue(custom.IsVisibleInWizard);
        Assert.IsTrue(custom.IsBaseUrlRequired);
        Assert.AreEqual("models", custom.DefaultProbePath);
    }

    private static void AssertPresetType(string protocol, IRuntimeProviderAdapter adapter)
    {
        switch (protocol)
        {
            case "Gemini":
                Assert.IsInstanceOfType<GeminiProviderAdapter>(adapter);
                break;
            case "GLM":
                Assert.IsInstanceOfType<GlmProviderAdapter>(adapter);
                break;
            case "Custom":
                Assert.IsInstanceOfType<OpenAICompatibleProviderAdapter>(adapter);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null);
        }
    }

    private static IRuntimeProviderAdapter CreateAdapter(string protocol, Uri baseAddress)
    {
        var providerId = $"profile-{protocol}";
        var config = new StandaloneConfig
        {
            Providers =
            [
                new ProviderEntry
                {
                    Name = providerId,
                    Protocol = protocol,
                    BaseUrl = new Uri(baseAddress, "v1").AbsoluteUri,
                    ApiKey = ProviderAdapterTestFactory.ApiKey,
                    Models = [ProviderAdapterTestFactory.ModelId]
                }
            ]
        };
        return StandaloneProviderFactory.CreateAdapter(config, providerId);
    }
}
