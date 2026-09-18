using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Server;

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
    public void TryMapProtocolToSlug_CoversCatalogProtocols()
    {
        foreach (var definition in StandaloneProviderProtocolCatalog.All)
        {
            Assert.IsTrue(
                RuntimeProviderAdapterFactory.TryMapProtocolToSlug(definition.Protocol, out var slug),
                definition.Protocol);
            Assert.IsFalse(string.IsNullOrWhiteSpace(slug));
        }

        Assert.IsTrue(RuntimeProviderAdapterFactory.TryMapProtocolToSlug("Custom", out var customSlug));
        Assert.AreEqual("openai-compatible", customSlug);
        Assert.IsTrue(
            RuntimeProviderAdapterFactory.TryMapProtocolToSlug("OpenAI Compatible", out var compatibleSlug));
        Assert.AreEqual(customSlug, compatibleSlug);
    }
}
