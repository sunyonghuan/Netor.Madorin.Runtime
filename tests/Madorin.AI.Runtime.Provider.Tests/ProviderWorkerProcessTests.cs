using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Server.ProviderWorkers;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ProviderWorkerProcessTests
{
    [TestMethod]
    public async Task CompatibleWorker_HelloThenShutdown_ProcessStarts()
    {
        var directory = AppContext.BaseDirectory;
        var exe = ProviderWorkerPaths.GetExecutablePath(directory, "openai-compatible");
        if (!File.Exists(exe))
        {
            Assert.Inconclusive($"Worker exe not found: {exe}");
            return;
        }

        await using var process = new ProviderWorkerProcess(
            directory,
            "openai-compatible",
            "OpenAI Compatible",
            "0.1.46");
        await process.EnsureReadyAsync(TestContext.CancellationToken);
        await process.DisposeAsync();
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task CompatibleWorker_StreamingEventsReturnFromFakeHttp()
    {
        var directory = AppContext.BaseDirectory;
        var exe = ProviderWorkerPaths.GetExecutablePath(directory, "openai-compatible");
        if (!File.Exists(exe))
        {
            Assert.Inconclusive($"Worker exe not found: {exe}");
            return;
        }

        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible),
            TestContext.CancellationToken);
        await using var supervisor = new ProviderWorkerSupervisor(directory);
        var connection = new ProviderConnectionConfig(
            "compatible-worker",
            "OpenAI Compatible",
            new Uri(server.BaseAddress, "v1").AbsoluteUri,
            ProviderAdapterTestFactory.ApiKey,
            Models: [ProviderAdapterTestFactory.ModelId]);
        var adapter = supervisor.CreateAdapter(connection.Protocol, connection);
        var events = await ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest() with { ProviderId = "compatible-worker" },
            TestContext.CancellationToken);

        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());
        var request = Assert.ContainsSingle(server.Requests);
        Assert.Contains(ProviderAdapterTestFactory.ModelId, request.Body, StringComparison.Ordinal);
        Assert.AreEqual($"Bearer {ProviderAdapterTestFactory.ApiKey}", request.Headers["Authorization"]);
    }

    [TestMethod]
    public void TryMapProtocolToSlug_CustomSharesCompatible()
    {
        Assert.IsTrue(RuntimeProviderAdapterFactory.TryMapProtocolToSlug("Custom", out var custom));
        Assert.IsTrue(RuntimeProviderAdapterFactory.TryMapProtocolToSlug("OpenAI Compatible", out var compatible));
        Assert.AreEqual("openai-compatible", custom);
        Assert.AreEqual(compatible, custom);
    }

    public TestContext TestContext { get; set; } = null!;
}
