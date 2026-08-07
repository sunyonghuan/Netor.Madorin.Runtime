using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.OpenAICompatible;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class OpenAICompatibleProviderUserAgentTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_UsesDefaultUserAgent()
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible));
        using var adapter = CreateAdapter(server.BaseAddress);

        var events = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest(),
            TestContext.CancellationToken);

        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        var request = Assert.ContainsSingle(server.Requests);
        Assert.AreEqual(OpenAICompatibleProviderAdapter.DefaultUserAgent, request.Headers["User-Agent"]);
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_UsesConfiguredUserAgent()
    {
        const string userAgent = "axis-code-madorin/1.0";
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible));
        using var adapter = new OpenAICompatibleProviderAdapter(
            new OpenAICompatibleProviderOptions(
                "configured-provider",
                $"{server.BaseAddress.AbsoluteUri.TrimEnd('/')}/v1",
                ProviderAdapterTestFactory.ApiKey,
                Models: [new ProviderModel(
                    ProviderAdapterTestFactory.ModelId,
                    "Test model",
                    Capabilities: ProviderAdapterTestFactory.CreateCapabilities())],
                UserAgent: userAgent),
            new HttpClient(),
            disposeHttpClient: true);

        var events = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest(),
            TestContext.CancellationToken);

        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        var request = Assert.ContainsSingle(server.Requests);
        Assert.AreEqual(userAgent, request.Headers["User-Agent"]);
    }

    private static OpenAICompatibleProviderAdapter CreateAdapter(Uri baseAddress) =>
        new(
            new OpenAICompatibleProviderOptions(
                "default-provider",
                $"{baseAddress.AbsoluteUri.TrimEnd('/')}/v1",
                ProviderAdapterTestFactory.ApiKey,
                Models: [new ProviderModel(
                    ProviderAdapterTestFactory.ModelId,
                    "Test model",
                    Capabilities: ProviderAdapterTestFactory.CreateCapabilities())]),
            new HttpClient(),
            disposeHttpClient: true);
}
