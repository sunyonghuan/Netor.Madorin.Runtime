using System.Text.Json;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.WorkerHost;
using Madorin.AI.Runtime.Server.ProviderWorkers;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ProviderModelMetadataTests(TestContext testContext)
{
    [TestMethod]
    public void ConnectionRoundTrip_PreservesDescriptorsAndLegacyIds()
    {
        var connection = new ProviderConnectionConfig("p", "DeepSeek", "https://example.test", "key",
            Models: ["chat", "summary"], ModelDescriptors: [new("summary", "Summary", ContextWindow: 32768, MaxOutputTokens: 2048)]);

        var json = JsonSerializer.Serialize(connection, ProviderWorkerJsonContext.Default.ProviderConnectionConfig);
        var restored = JsonSerializer.Deserialize(json, ProviderWorkerJsonContext.Default.ProviderConnectionConfig);

        Assert.IsNotNull(restored);
        CollectionAssert.AreEqual(connection.Models!.ToArray(), restored.Models!.ToArray());
        CollectionAssert.AreEqual(connection.ModelDescriptors!.ToArray(), restored.ModelDescriptors!.ToArray());
    }

    [TestMethod]
    public void Resolve_ExplicitDescriptorsOverrideCatalog_UnknownIdsStayUnknown()
    {
        var connection = new ProviderConnectionConfig("p", "DeepSeek", "https://example.test", "key",
            Models: ["known", "custom"], ModelDescriptors: [new("known", "Known", ContextWindow: 65536)]);
        ProviderModel[] catalog = [new("known", "Known", ContextWindow: 32768, MaxOutputTokens: 2048)];

        var models = ProviderConnectionModels.Resolve(connection, catalog);

        Assert.HasCount(2, models);
        Assert.AreEqual(65536, models.Single(m => m.Id == "known").ContextWindow);
        Assert.AreEqual(2048, models.Single(m => m.Id == "known").MaxOutputTokens);
        Assert.IsNull(models.Single(m => m.Id == "custom").ContextWindow);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task LegacyIdOnlyWorker_ResolvesKnownLimitsFromLocalHandshake()
    {
        await using var supervisor = new ProviderWorkerSupervisor(AppContext.BaseDirectory);
        var adapter = supervisor.CreateAdapter("DeepSeek", new ProviderConnectionConfig(
            "p", "DeepSeek", "https://not-used.invalid", "not-used", Models: ["deepseek-chat", "custom"]));

        var models = await ((IRuntimeProviderModelCatalogSource)adapter).FetchModelsAsync(testContext.CancellationToken);

        Assert.AreEqual(64000, models.Single(m => m.Id == "deepseek-chat").ContextWindow);
        Assert.AreEqual(8000, models.Single(m => m.Id == "deepseek-chat").MaxOutputTokens);
        Assert.IsNull(models.Single(m => m.Id == "custom").ContextWindow);
    }

    [TestMethod]
    public void LegacyHello_WithoutCatalog_RemainsCompatible()
    {
        const string json = """{"ipcVersion":1,"slug":"deepseek","protocol":"DeepSeek"}""";

        var restored = JsonSerializer.Deserialize(json, ProviderWorkerJsonContext.Default.HelloOkFrame);

        Assert.IsNotNull(restored);
        Assert.IsNull(restored.Models);
    }
}
