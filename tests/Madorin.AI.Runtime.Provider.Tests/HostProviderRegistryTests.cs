using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Server.ProviderWorkers;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class HostProviderRegistryTests
{
    [TestMethod]
    public async Task Upsert_And_Resolve_BuildsAdapterByProviderId()
    {
        await using var supervisor = new ProviderWorkerSupervisor(AppContext.BaseDirectory);
        var registry = new HostProviderRegistry(supervisor);
        registry.Upsert(
        [
            new HostProviderConfig(
                " vendor-a ",
                " OpenAI ",
                " https://api.openai.com/v1 ",
                " sk-test ",
                Models: ["gpt-5"])
        ]);

        Assert.IsTrue(registry.Contains("vendor-a"));
        var adapter = registry.Resolve("vendor-a");
        Assert.AreEqual("vendor-a", adapter.ProviderId);

        var viaSelection = registry.CreateResolver()(
            new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection("vendor-a", "gpt-5"),
                new ExpertModeOptions(new AgentRef("a", "1", "prompt"))));
        Assert.AreEqual("vendor-a", viaSelection.ProviderId);
    }

    [TestMethod]
    public async Task Upsert_SameProviderId_OverwritesPreviousEntry()
    {
        await using var supervisor = new ProviderWorkerSupervisor(AppContext.BaseDirectory);
        var registry = new HostProviderRegistry(supervisor);
        registry.Upsert(
        [
            new HostProviderConfig(
                "p1",
                "OpenAI",
                "https://api.openai.com/v1",
                "key-1")
        ]);
        registry.Upsert(
        [
            new HostProviderConfig(
                "p1",
                "OpenAI",
                "https://api.openai.com/v1",
                "key-2")
        ]);

        Assert.IsTrue(registry.TryUpdateCredential("p1", "key-3"));
        var adapter = registry.Resolve("p1");
        Assert.AreEqual("p1", adapter.ProviderId);
    }

    [TestMethod]
    public void Upsert_RejectsBlankRequiredFields()
    {
        var registry = new HostProviderRegistry();
        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => registry.Upsert(
            [
                new HostProviderConfig(" ", "OpenAI", "https://x", "key")
            ]));
        StringAssert.Contains(ex.Message, "ProviderId");
    }

    [TestMethod]
    public void Upsert_RejectsUnknownProtocol()
    {
        var registry = new HostProviderRegistry();
        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => registry.Upsert(
            [
                new HostProviderConfig("p1", "UnknownProto", "https://x", "key")
            ]));
        StringAssert.Contains(ex.Message, "unsupported protocol");
    }

    [TestMethod]
    public void Resolve_MissingProvider_Throws()
    {
        var registry = new HostProviderRegistry();
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => registry.Resolve("missing"));
        StringAssert.Contains(ex.Message, "not configured");
    }

    [TestMethod]
    public async Task Upsert_SameConnection_MergesModelLimitsAndUsesLatestCredential()
    {
        await using var supervisor = new CapturingSupervisor();
        var registry = new HostProviderRegistry(supervisor);
        registry.Upsert([new HostProviderConfig("p", "DeepSeek", "https://example.test", "old-key",
            Models: ["chat"], ModelDescriptors: [new("chat", 131072, 8192)])]);
        registry.Upsert([new HostProviderConfig("p", "DeepSeek", "https://example.test", "new-key",
            Models: ["summary"], ModelDescriptors: [new("summary", 32768, 2048)])]);
        registry.Upsert([new HostProviderConfig("p", "DeepSeek", "https://example.test", "latest-key",
            Models: ["chat"], ModelDescriptors: [new("chat", MaxOutputTokens: 4096)])]);

        var adapter = registry.Resolve("p");

        Assert.AreEqual("latest-key", supervisor.Connection!.ApiKey);
        Assert.HasCount(2, adapter.Models);
        Assert.AreEqual(131072, adapter.Models.Single(m => m.Id == "chat").ContextWindow);
        Assert.AreEqual(4096, adapter.Models.Single(m => m.Id == "chat").MaxOutputTokens);
        Assert.AreEqual(32768, adapter.Models.Single(m => m.Id == "summary").ContextWindow);
        Assert.AreEqual(2048, adapter.Models.Single(m => m.Id == "summary").MaxOutputTokens);
        Assert.IsTrue(registry.TryUpdateCredential("p", "refreshed-key"));
        var refreshed = registry.Resolve("p");
        Assert.AreEqual("refreshed-key", supervisor.Connection.ApiKey);
        CollectionAssert.AreEquivalent(adapter.Models.ToArray(), refreshed.Models.ToArray());
    }

    [TestMethod]
    public async Task Upsert_DifferentEndpoint_DoesNotInheritPreviousModelLimits()
    {
        await using var supervisor = new CapturingSupervisor();
        var registry = new HostProviderRegistry(supervisor);
        registry.Upsert([new HostProviderConfig("p", "DeepSeek", "https://old.test", "key",
            Models: ["chat"], ModelDescriptors: [new("chat", 131072, 8192)])]);
        registry.Upsert([new HostProviderConfig("p", "DeepSeek", "https://new.test", "key", Models: ["chat"])]);

        Assert.IsNull(Assert.ContainsSingle(registry.Resolve("p").Models).ContextWindow);
    }

    [TestMethod]
    [DataRow(0, 10)]
    [DataRow(10, 0)]
    [DataRow(-1, 10)]
    public void Upsert_InvalidModelLimits_AreRejected(int window, int output)
    {
        var registry = new HostProviderRegistry();
        Assert.ThrowsExactly<ArgumentException>(() => registry.Upsert([
            new HostProviderConfig("p", "OpenAI", "https://example.test", "key",
                ModelDescriptors: [new("chat", window, output)])]));
    }

    private sealed class CapturingSupervisor : IProviderWorkerSupervisor
    {
        private readonly ProviderWorkerProcess _process = new(AppContext.BaseDirectory, "deepseek", "DeepSeek", "test");

        public ProviderConnectionConfig? Connection { get; private set; }

        public IRuntimeProviderAdapter CreateAdapter(string protocol, ProviderConnectionConfig connection)
        {
            Connection = connection;
            return new ProviderWorkerAdapter(_process, connection);
        }

        public ValueTask DisposeAsync() => _process.DisposeAsync();
    }
}
