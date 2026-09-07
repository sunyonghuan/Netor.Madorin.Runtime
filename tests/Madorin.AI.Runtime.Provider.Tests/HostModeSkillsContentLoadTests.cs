using System.Reflection;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Provider.Tests;

/// <summary>
/// Deterministic host-mode probes that actually invoke MAF load_skill / read_skill_resource
/// and assert full skill body + resource payloads (no live model required).
/// </summary>
[TestClass]
public sealed class HostModeSkillsContentLoadTests
{
    private const string BodyMarker = "secret-body-marker";
    private const string ResourceMarker = "resource-marker-alpha";
    private const string FullDiscoveryDescription =
        "Reviews pull requests for style, correctness, error handling, and security. "
        + "Supports batch export of findings and optional auto-fix suggestions. "
        + "Also covers dependency audits, changelog drafting, and release checklist validation for multi-package monorepos.";

    private string _root = null!;
    private string _skillPath = null!;
    private string _sourceRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "madorin-host-skills-content",
            Guid.NewGuid().ToString("N"));
        _sourceRoot = Path.Combine(_root, "workspace-skills");
        _skillPath = Path.Combine(_sourceRoot, "demo-skill");
        Directory.CreateDirectory(Path.Combine(_skillPath, "references"));
        File.WriteAllText(
            Path.Combine(_skillPath, "SKILL.md"),
            $"""
            ---
            name: demo-skill
            description: {FullDiscoveryDescription}
            ---
            # Demo Skill Body
            {BodyMarker}
            Follow the style guide in references.
            """);
        File.WriteAllText(
            Path.Combine(_skillPath, "references", "style-guide.md"),
            $"{ResourceMarker}\nPrefer explicit error handling.");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task LoadSkill_AfterActivation_ReturnsFullBodyMarker()
    {
        var (provider, skills) = await CreateActivatedProviderAsync().ConfigureAwait(false);
        var tools = InvokeBuildTools(provider, skills);
        var load = tools.Single(static tool => tool.Name == "load_skill");

        var result = await load.InvokeAsync(CreateToolArgs(("skillName", "demo-skill")))
            .ConfigureAwait(false);

        var text = NormalizeToolResult(result);
        StringAssert.Contains(text, BodyMarker, StringComparison.Ordinal);
        StringAssert.Contains(text, "Demo Skill Body", StringComparison.Ordinal);
        Assert.DoesNotContain("host-skills-guide", text, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task ReadSkillResource_AfterActivation_ReturnsResourceMarker()
    {
        var (provider, skills) = await CreateActivatedProviderAsync().ConfigureAwait(false);
        var tools = InvokeBuildTools(provider, skills);
        var read = tools.Single(static tool => tool.Name == "read_skill_resource");

        foreach (var resourceName in new[] { "style-guide", "references/style-guide.md" })
        {
            var result = await read.InvokeAsync(
                    CreateToolArgs(
                        ("skillName", "demo-skill"),
                        ("resourceName", resourceName)))
                .ConfigureAwait(false);

            var text = NormalizeToolResult(result);
            StringAssert.Contains(
                text,
                ResourceMarker,
                StringComparison.Ordinal,
                $"resourceName={resourceName}");
        }
    }

    [TestMethod]
    public async Task AdvertiseDescription_UsesFullSearchHitText_NotHard160Cut()
    {
        var store = new SkillActivationStore();
        Assert.AreEqual(
            1,
            SkillSearchActivationHook.TryApplySearchResult(
                store,
                "host.skill.search",
                "skill_search",
                BuildSearchHitsJson("demo-skill", FullDiscoveryDescription),
                ["demo-skill"]));

        var context = CreateContext(store);
        var source = new HostModeLoadableSkillsSource(context);
        var skills = await source.GetSkillsAsync(CancellationToken.None).ConfigureAwait(false);
        var demo = skills.Single(static skill =>
            string.Equals(skill.Frontmatter.Name, "demo-skill", StringComparison.OrdinalIgnoreCase));

        Assert.AreEqual(FullDiscoveryDescription, demo.Frontmatter.Description);
        Assert.DoesNotEndWith("…", demo.Frontmatter.Description);
        Assert.IsGreaterThan(160, demo.Frontmatter.Description.Length);
    }

    [TestMethod]
    public async Task LoadSkill_BeforeActivation_DoesNotExposeDemoBody()
    {
        var store = new SkillActivationStore();
        var context = CreateContext(store);
        var provider = MafSkillsAgentFactory.CreateSkillsProvider(context);
        Assert.IsNotNull(provider);
        var source = new HostModeLoadableSkillsSource(context);
        var skills = await source.GetSkillsAsync(CancellationToken.None).ConfigureAwait(false);
        var tools = InvokeBuildTools(provider!, skills);
        var load = tools.Single(static tool => tool.Name == "load_skill");

        var result = await load.InvokeAsync(CreateToolArgs(("skillName", "demo-skill")))
            .ConfigureAwait(false);

        var text = NormalizeToolResult(result);
        Assert.DoesNotContain(BodyMarker, text, StringComparison.Ordinal);
    }

    private static AIFunctionArguments CreateToolArgs(params (string Key, object Value)[] values)
    {
        var args = new AIFunctionArguments();
        foreach (var (key, value) in values)
        {
            args[key] = value;
        }

        // MAF resource/script tools require a service provider even when unused.
        args.Services = new ServiceCollection().BuildServiceProvider();
        return args;
    }

    private async Task<(AgentSkillsProvider Provider, IList<AgentSkill> Skills)> CreateActivatedProviderAsync()
    {
        var store = new SkillActivationStore();
        Assert.AreEqual(
            1,
            SkillSearchActivationHook.TryApplySearchResult(
                store,
                "host.skill.search",
                "skill_search",
                BuildSearchHitsJson("demo-skill", FullDiscoveryDescription),
                ["demo-skill"]));

        var context = CreateContext(store);
        var provider = MafSkillsAgentFactory.CreateSkillsProvider(context);
        Assert.IsNotNull(provider);
        var source = new HostModeLoadableSkillsSource(context);
        var skills = await source.GetSkillsAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(
            skills.Any(static skill =>
                string.Equals(skill.Frontmatter.Name, "demo-skill", StringComparison.OrdinalIgnoreCase)));
        return (provider!, skills);
    }

    private SkillsInvocationContext CreateContext(SkillActivationStore store) =>
        new(
            "catalog-content-v1",
            [],
            [],
            [],
            SkillMaterializedPaths: [],
            IsHostMode: true,
            ActivationStore: store,
            AllowlistSkillIds: ["demo-skill"],
            CatalogMaterializedPathsByName: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["demo-skill"] = _skillPath
            },
            CatalogDescriptionsByName: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["demo-skill"] = FullDiscoveryDescription
            },
            ScopeSourceRoots:
            [
                new SkillScopeSourceRoot("workspace", _sourceRoot)
            ]);

    private static string BuildSearchHitsJson(string name, string description) =>
        "{\"hits\":[{\"name\":\"" + EscapeJson(name)
        + "\",\"scope\":\"workspace\",\"description\":\"" + EscapeJson(description) + "\"}]}";

    private static string EscapeJson(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string NormalizeToolResult(object? result)
    {
        if (result is null)
        {
            return string.Empty;
        }

        if (result is string text)
        {
            return text;
        }

        if (result is JsonElement element)
        {
            return element.ValueKind is JsonValueKind.String
                ? element.GetString() ?? element.GetRawText()
                : element.GetRawText();
        }

        return result.ToString() ?? string.Empty;
    }

#pragma warning disable MAAI001 // Experimental MAF Skills APIs used only in probes.
    private static IList<AIFunction> InvokeBuildTools(
        AgentSkillsProvider provider,
        IList<AgentSkill> skills)
    {
        var method = typeof(AgentSkillsProvider).GetMethod(
            "BuildTools",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        var result = method.Invoke(provider, [skills]);
        Assert.IsInstanceOfType<IList<AIFunction>>(result);
        return (IList<AIFunction>)result!;
    }
#pragma warning restore MAAI001
}
