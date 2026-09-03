using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Server;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class HostProviderRegistryTests
{
    [TestMethod]
    public void Upsert_And_Resolve_BuildsAdapterByProviderId()
    {
        var registry = new HostProviderRegistry();
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
    public void Upsert_SameProviderId_OverwritesPreviousEntry()
    {
        var registry = new HostProviderRegistry();
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
}
