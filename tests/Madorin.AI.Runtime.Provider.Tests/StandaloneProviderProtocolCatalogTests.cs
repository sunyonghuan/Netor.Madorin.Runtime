using Madorin.AI.Runtime.Cli.Config;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class StandaloneProviderProtocolCatalogTests
{
    private static readonly string[] LegacyProtocols =
    [
        "OpenAI",
        "Anthropic",
        "OpenAI Compatible",
        "DeepSeek",
        "Kimi"
    ];

    private static readonly string[] ExpectedProtocols =
    [
        .. LegacyProtocols,
        "Aliyun",
        "Azure OpenAI",
        "Gemini",
        "GLM",
        "Custom",
        "Ollama"
    ];

    [TestMethod]
    public void All_ContainsLegacyProtocolsOnceWithStableDefaults()
    {
        Assert.HasCount(ExpectedProtocols.Length, StandaloneProviderProtocolCatalog.All);
        CollectionAssert.AreEqual(
            ExpectedProtocols,
            StandaloneProviderProtocolCatalog.All.Select(static item => item.Protocol).ToArray());
        CollectionAssert.AreEqual(
            LegacyProtocols,
            StandaloneProviderProtocolCatalog.All
                .Take(LegacyProtocols.Length)
                .Select(static item => item.Protocol)
                .ToArray());
        Assert.AreEqual(
            ExpectedProtocols.Length,
            StandaloneProviderProtocolCatalog.All.Select(static item => item.Protocol)
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.IsTrue(StandaloneProviderProtocolCatalog.All
            .Where(static item => item.Protocol is not "Azure OpenAI")
            .All(static item => item.IsVisibleInWizard));
        Assert.IsFalse(
            StandaloneProviderProtocolCatalog.GetRequired("Azure OpenAI").IsVisibleInWizard);
        Assert.HasCount(
            ExpectedProtocols.Length - 1,
            StandaloneProviderProtocolCatalog.WizardEntries);
        Assert.IsTrue(StandaloneProviderProtocolCatalog.GetRequired("OpenAI Compatible").IsBaseUrlRequired);
        Assert.IsFalse(StandaloneProviderProtocolCatalog.GetRequired("Ollama").IsApiKeyRequired);
        Assert.IsTrue(StandaloneProviderProtocolCatalog.All
            .Where(static item => item.Protocol is not "Ollama")
            .All(static item => item.IsApiKeyRequired));
    }

    [TestMethod]
    public void ResolveWizardInput_AcceptsNumberAndCaseInsensitiveName()
    {
        Assert.AreEqual("OpenAI", StandaloneProviderProtocolCatalog.ResolveWizardInput("1")?.Protocol);
        Assert.AreEqual(
            "OpenAI Compatible",
            StandaloneProviderProtocolCatalog.ResolveWizardInput("openai compatible")?.Protocol);
        Assert.IsNull(StandaloneProviderProtocolCatalog.ResolveWizardInput("99"));
        Assert.IsNull(StandaloneProviderProtocolCatalog.ResolveWizardInput("unknown"));
    }

    [TestMethod]
    public void BuildAdapter_LegacyProtocolsPreserveConfiguredProviderId()
    {
        foreach (var definition in StandaloneProviderProtocolCatalog.All.Take(LegacyProtocols.Length))
        {
            var providerId = $"profile-{definition.Protocol}";
            var entry = new ProviderEntry
            {
                Name = providerId,
                Protocol = definition.Protocol,
                BaseUrl = definition.DefaultBaseUrl,
                ApiKey = ProviderAdapterTestFactory.ApiKey,
                Models = [definition.DefaultModel]
            };

            var adapter = definition.BuildAdapter(entry);
            using var adapterLifetime = adapter as IDisposable;

            Assert.AreEqual(providerId, adapter.ProviderId);
        }
    }
}
