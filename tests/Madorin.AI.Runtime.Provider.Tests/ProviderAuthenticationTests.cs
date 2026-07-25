using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.OpenAICompatible;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ProviderAuthenticationTests
{
    private readonly TestContext _testContext;

    public ProviderAuthenticationTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task FetchModelsAsync_NoAuthentication_DoesNotSendAuthorizationAndReturnsStableCatalog()
    {
        const string response =
            """
            {
              "data": [
                { "id": "zeta", "display_name": "Zeta" },
                { "id": "alpha", "owned_by": "vendor" },
                { "id": "zeta", "display_name": "Duplicate" },
                { "id": "" }
              ]
            }
            """;
        await using var server = await FakeProviderServer.StartAsync(
            _ => FakeProviderResponse.Error(200, response),
            _testContext.CancellationToken);
        using var adapter = CreateAdapter(
            server.BaseAddress,
            ProviderAuthenticationMode.None,
            apiKey: string.Empty);

        Assert.IsInstanceOfType<IRuntimeProviderModelCatalogSource>(adapter);
        var models = await adapter.FetchModelsAsync(_testContext.CancellationToken);

        Assert.HasCount(2, models);
        Assert.AreEqual("alpha", models[0].Id);
        Assert.AreEqual("alpha", models[0].DisplayName);
        Assert.AreEqual("zeta", models[1].Id);
        Assert.AreEqual("Zeta", models[1].DisplayName);
        Assert.IsFalse(adapter.ConfigurationSchema.Fields.Single(
            static field => field.Name == "apiKey").IsRequired);
        var request = Assert.ContainsSingle(server.Requests);
        Assert.AreEqual("/v1/models", request.Path);
        Assert.IsFalse(request.Headers.ContainsKey("Authorization"));
    }

    [TestMethod]
    public async Task UpdateCredentialsAsync_NoAuthentication_IsRejected()
    {
        using var adapter = new OpenAICompatibleProviderAdapter(
            new OpenAICompatibleProviderOptions(
                "local",
                "http://localhost:11434/v1",
                string.Empty,
                AuthenticationMode: ProviderAuthenticationMode.None),
            new HttpClient(new HttpClientHandler()),
            disposeHttpClient: true);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            adapter.UpdateCredentialsAsync(
                new CredentialsUpdateParameters("run-1", "local", "not-used"),
                _testContext.CancellationToken));
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_NoAuthentication_DoesNotSendSdkPlaceholderCredential()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible),
            _testContext.CancellationToken);
        using var adapter = CreateAdapter(
            server.BaseAddress,
            ProviderAuthenticationMode.None,
            apiKey: string.Empty);

        var events = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest(),
            _testContext.CancellationToken);

        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        var request = Assert.ContainsSingle(server.Requests);
        Assert.IsFalse(request.Headers.ContainsKey("Authorization"));
        Assert.DoesNotContain("madorin-no-auth", request.Body, StringComparison.Ordinal);
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task FetchModelsAsync_ResponseExceedsLimit_FailsWithoutReturningPartialData()
    {
        var response = $"{{\"data\":[{{\"id\":\"{new string('x', 256)}\"}}]}}";
        await using var server = await FakeProviderServer.StartAsync(
            _ => FakeProviderResponse.Error(200, response),
            _testContext.CancellationToken);
        using var adapter = CreateAdapter(
            server.BaseAddress,
            ProviderAuthenticationMode.Required,
            ProviderAdapterTestFactory.ApiKey,
            maximumResponseBytes: 64);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            await adapter.FetchModelsAsync(_testContext.CancellationToken);
        });
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task FetchModelsAsync_InvalidJson_ReportsProtocolFailure()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => FakeProviderResponse.Error(200, "{not-json"),
            _testContext.CancellationToken);
        using var adapter = CreateAdapter(
            server.BaseAddress,
            ProviderAuthenticationMode.Required,
            ProviderAdapterTestFactory.ApiKey);
        await Assert.ThrowsAsync<JsonException>(async () =>
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
        using var adapter = CreateAdapter(
            server.BaseAddress,
            ProviderAuthenticationMode.None,
            apiKey: string.Empty);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            _testContext.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await adapter.FetchModelsAsync(cts.Token);
        });
    }

    private static OpenAICompatibleProviderAdapter CreateAdapter(
        Uri baseAddress,
        ProviderAuthenticationMode authenticationMode,
        string apiKey,
        int maximumResponseBytes = 65_536)
    {
        var options = new OpenAICompatibleProviderOptions(
            "compatible-profile",
            new Uri(baseAddress, "v1").AbsoluteUri,
            apiKey,
            Models: [new ProviderModel(ProviderAdapterTestFactory.ModelId, "Test model")],
            ProbePath: "models",
            MaximumProbeResponseBytes: maximumResponseBytes,
            AuthenticationMode: authenticationMode);
        return new OpenAICompatibleProviderAdapter(
            options,
            ProviderHttpClientFactory.Shared.CreateClient("compatible-profile"),
            disposeHttpClient: true);
    }
}
