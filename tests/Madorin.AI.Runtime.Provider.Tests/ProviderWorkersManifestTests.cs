using System.Text.Json;
using Madorin.AI.Runtime.Providers.WorkerHost;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ProviderWorkersManifestTests
{
    [TestMethod]
    public void ProviderWorkersJson_ListsTenSlugsMatchingProtocolMap()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "provider-workers.json");
        Assert.IsTrue(File.Exists(path), path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var workers = document.RootElement.GetProperty("workers");
        Assert.AreEqual(10, workers.GetArrayLength());

        var slugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var worker in workers.EnumerateArray())
        {
            var protocol = worker.GetProperty("protocol").GetString();
            var slug = worker.GetProperty("slug").GetString();
            var project = worker.GetProperty("project").GetString();
            Assert.IsFalse(string.IsNullOrWhiteSpace(protocol));
            Assert.IsFalse(string.IsNullOrWhiteSpace(slug));
            Assert.IsFalse(string.IsNullOrWhiteSpace(project));
            Assert.AreNotEqual("Custom", protocol);
            Assert.IsTrue(ProviderProtocolSlugs.TryMap(protocol, out var mapped));
            Assert.AreEqual(mapped, slug);
            Assert.IsTrue(slugs.Add(slug!), slug);
        }

        string[] expected =
        [
            "openai",
            "anthropic",
            "openai-compatible",
            "deepseek",
            "kimi",
            "aliyun",
            "azure-openai",
            "gemini",
            "glm",
            "ollama"
        ];
        foreach (var slug in expected)
        {
            Assert.Contains(slug, slugs);
        }
    }
}
