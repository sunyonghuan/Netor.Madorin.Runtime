using System.Reflection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class HostModeSkillsActivationTests
{
    private static readonly string[] ExpectedSkillTools =
    [
        "load_skill",
        "read_skill_resource",
        "run_skill_script"
    ];

    private string _root = null!;
    private string _skillPath = null!;
    private string _sourceRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "madorin-host-skills",
            Guid.NewGuid().ToString("N"));
        _sourceRoot = Path.Combine(_root, "workspace-skills");
        _skillPath = Path.Combine(_sourceRoot, "demo-skill");
        Directory.CreateDirectory(_skillPath);
        File.WriteAllText(
            Path.Combine(_skillPath, "SKILL.md"),
            """
            ---
            name: demo-skill
            description: A long catalog description that must not be advertised until activated.
            ---
            # Demo Skill Body
            secret-body-marker
            """);
        Directory.CreateDirectory(Path.Combine(_skillPath, "references"));
        File.WriteAllText(
            Path.Combine(_skillPath, "references", "style-guide.md"),
            "resource-marker-alpha\nUse this style guide after load_skill.");
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
    public async Task CreateSkillsProvider_HostModeEmptyLoadable_StillCreatesProviderWithThreeTools()
    {
        var context = CreateHostContext(new SkillActivationStore(), allowlist: ["demo-skill"]);
        var provider = MafSkillsAgentFactory.CreateSkillsProvider(context);
        Assert.IsNotNull(provider);

        var source = new HostModeLoadableSkillsSource(context);
        var skills = await source.GetSkillsAsync(CancellationToken.None);
        Assert.IsNotEmpty(skills);
        Assert.AreEqual("host-skills-guide", skills[0].Frontmatter.Name);

        var tools = InvokeBuildTools(provider, skills);
        Assert.HasCount(3, tools);
        CollectionAssert.AreEquivalent(
            ExpectedSkillTools,
            tools.Select(static tool => tool.Name).ToArray());
    }

    [TestMethod]
    public void CreateSkillsProvider_IndependentEmptyPaths_ReturnsNull()
    {
        var context = new SkillsInvocationContext(
            "catalog-v1",
            [],
            [],
            [],
            SkillMaterializedPaths: []);

        Assert.IsNull(MafSkillsAgentFactory.CreateSkillsProvider(context));
    }

    [TestMethod]
    public async Task ProbeB_SameProviderSource_SearchThenLoadableExpands()
    {
        var store = new SkillActivationStore();
        var allowlist = new[] { "demo-skill" };
        var context = CreateHostContext(store, allowlist);
        var source = new HostModeLoadableSkillsSource(context);

        var before = await source.GetSkillsAsync(CancellationToken.None);
        Assert.ContainsSingle(before);
        Assert.AreEqual("host-skills-guide", before[0].Frontmatter.Name);

        var applied = SkillSearchActivationHook.TryApplySearchResult(
            store,
            "host.host..skill.search..1.0.0",
            "skill_search",
            """
            {"hits":[{"name":"demo-skill","scope":"workspace","description":"short hit desc"}]}
            """,
            allowlist);
        Assert.AreEqual(1, applied);

        var after = await source.GetSkillsAsync(CancellationToken.None);
        Assert.ContainsSingle(after);
        Assert.AreEqual("demo-skill", after[0].Frontmatter.Name);
        Assert.AreEqual("short hit desc", after[0].Frontmatter.Description);
        Assert.DoesNotContain(
            "long catalog description",
            after[0].Frontmatter.Description,
            StringComparison.OrdinalIgnoreCase);

        Assert.IsTrue(
            MafSkillsAgentFactory.TryResolveLoadPath(context, "demo-skill", out var path, out var error));
        Assert.AreEqual(_skillPath, path);
        Assert.IsNull(error);

        var provider = MafSkillsAgentFactory.CreateSkillsProvider(context);
        Assert.IsNotNull(provider);
        var tools = InvokeBuildTools(provider, after);
        Assert.HasCount(3, tools);
    }

    [TestMethod]
    public void SkillSearchHook_VersionedToolId_Activates()
    {
        var store = new SkillActivationStore();
        var applied = SkillSearchActivationHook.TryApplySearchResult(
            store,
            "host.plugin.skill.search.v2",
            null,
            """{"hits":[{"name":"demo-skill","scope":"workspace"}]}""",
            allowlistSkillIds: ["demo-skill"]);
        Assert.AreEqual(1, applied);
        Assert.IsTrue(store.IsLoadable("demo-skill", ["demo-skill"]));
    }

    [TestMethod]
    public void LazyResolve_FromScopeRoot_WhenCatalogMapMissing()
    {
        var store = new SkillActivationStore();
        Assert.IsTrue(store.TryActivate("demo-skill", "workspace", ["demo-skill"], "short"));

        var context = new SkillsInvocationContext(
            "catalog-v1",
            [],
            [],
            [],
            SkillMaterializedPaths: [],
            IsHostMode: true,
            ActivationStore: store,
            AllowlistSkillIds: ["demo-skill"],
            CatalogMaterializedPathsByName: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            CatalogDescriptionsByName: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ScopeSourceRoots:
            [
                new SkillScopeSourceRoot("workspace", _sourceRoot)
            ]);

        Assert.IsTrue(
            MafSkillsAgentFactory.TryResolveLoadPath(context, "demo-skill", out var path, out var error));
        Assert.AreEqual(_skillPath, path);
        Assert.IsNull(error);

        var resolved = MafSkillsAgentFactory.ResolveHostLoadableMaterializedPaths(context);
        Assert.ContainsSingle(resolved);
        Assert.AreEqual(_skillPath, resolved[0]);
    }

    [TestMethod]
    public void SkillSearchHook_OutsideAllowlist_CannotLoad()
    {
        var store = new SkillActivationStore();
        var applied = SkillSearchActivationHook.TryApplySearchResult(
            store,
            "host.skill.search",
            "skill_search",
            """{"hits":[{"name":"other-skill","scope":"host-global"}]}""",
            allowlistSkillIds: ["demo-skill"]);
        Assert.AreEqual(0, applied);

        var context = CreateHostContext(store, ["demo-skill"]);
        Assert.IsFalse(
            MafSkillsAgentFactory.TryResolveLoadPath(context, "other-skill", out _, out var error));
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
        Assert.Contains("allowlist", error!, StringComparison.OrdinalIgnoreCase);

        Assert.IsFalse(
            MafSkillsAgentFactory.TryResolveLoadPath(context, "demo-skill", out _, out error));
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
        Assert.Contains("skill.search", error!, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void SkillSearchHook_EmptyAllowlist_BlocksAll()
    {
        var store = new SkillActivationStore();
        var applied = SkillSearchActivationHook.TryApplySearchResult(
            store,
            "host.skill.search",
            null,
            """{"hits":[{"name":"demo-skill"}]}""",
            allowlistSkillIds: []);
        Assert.AreEqual(0, applied);
        Assert.IsEmpty(store.GetLoadableNames([]));
    }

    [TestMethod]
    public void SkillSearchHook_CapsHitsPerResult_DoesNotActivateFullAllowlist()
    {
        var store = new SkillActivationStore();
        var allowlist = Enumerable.Range(1, 20).Select(static i => $"skill-{i:D2}").ToArray();
        var hits = string.Join(
            ',',
            allowlist.Select(static name =>
                $"{{\"name\":\"{name}\",\"description\":\"full capability text for {name}\"}}"));
        var applied = SkillSearchActivationHook.TryApplySearchResult(
            store,
            "host.skill.search",
            "skill_search",
            $"{{\"hits\":[{hits}]}}",
            allowlist,
            maxHitsPerResult: SkillSearchActivationHook.DefaultMaxHitsPerSearchResult);
        Assert.AreEqual(SkillSearchActivationHook.DefaultMaxHitsPerSearchResult, applied);
        Assert.HasCount(
            SkillSearchActivationHook.DefaultMaxHitsPerSearchResult,
            store.GetLoadableNames(allowlist));
        Assert.IsTrue(store.IsLoadable("skill-01", allowlist));
        Assert.IsFalse(store.IsLoadable("skill-09", allowlist));
    }

    [TestMethod]
    public void SkillActivationStore_MaxLoadableCeiling_BlocksFurtherNewNames()
    {
        var store = new SkillActivationStore(maxLoadableSkills: 2);
        Assert.IsTrue(store.TryActivate("a", "workspace", ["a", "b", "c"], "desc-a"));
        Assert.IsTrue(store.TryActivate("b", "workspace", ["a", "b", "c"], "desc-b"));
        Assert.IsFalse(store.TryActivate("c", "workspace", ["a", "b", "c"], "desc-c"));
        Assert.IsTrue(store.TryActivate("a", "workspace", ["a", "b", "c"], "desc-a-refreshed"));
        Assert.AreEqual("desc-a-refreshed", store.Snapshot().Single(static e => e.Name == "a").ShortDescription);
        Assert.HasCount(2, store.GetLoadableNames(["a", "b", "c"]));
    }

    [TestMethod]
    public void BoundDescription_KeepsFullText_AndCutsOnSentenceBoundary()
    {
        var full = "Reviews pull requests for style. Checks error handling and tests. Also validates security.";
        Assert.AreEqual(full, HostModeLoadableSkillsSource.BoundDescription(full, maxLength: 500));

        var longText =
            "Capability A does foo and bar. Capability B handles batch exports and retries. "
            + "Capability C should not appear when the budget is tight enough to cut earlier.";
        var bounded = HostModeLoadableSkillsSource.BoundDescription(longText, maxLength: 70);
        Assert.EndsWith("…", bounded);
        Assert.DoesNotContain("Capability C", bounded, StringComparison.Ordinal);
        Assert.Contains("Capability A", bounded, StringComparison.Ordinal);
        Assert.DoesNotContain("Capability A does foo and ba…", bounded, StringComparison.Ordinal);
    }

    private SkillsInvocationContext CreateHostContext(
        SkillActivationStore store,
        string[] allowlist) =>
        new(
            "catalog-v1",
            [],
            [],
            [],
            SkillMaterializedPaths: [],
            IsHostMode: true,
            ActivationStore: store,
            AllowlistSkillIds: allowlist,
            CatalogMaterializedPathsByName: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["demo-skill"] = _skillPath
            },
            CatalogDescriptionsByName: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["demo-skill"] = "A long catalog description that must not be advertised until activated."
            },
            ScopeSourceRoots:
            [
                new SkillScopeSourceRoot("workspace", _sourceRoot)
            ]);

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
