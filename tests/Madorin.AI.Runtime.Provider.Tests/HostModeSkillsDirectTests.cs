using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class HostModeSkillsDirectTests
{
    private string _root = null!;
    private string _sourceRoot = null!;
    private readonly Dictionary<string, string> _pathsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _descriptionsByName = new(StringComparer.OrdinalIgnoreCase);

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "madorin-host-skills-direct",
            Guid.NewGuid().ToString("N"));
        _sourceRoot = Path.Combine(_root, "workspace-skills");
        Directory.CreateDirectory(_sourceRoot);
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
    public void Normalize_DefaultsAndUnknown_AreProgressive()
    {
        Assert.AreEqual(SkillDiscoveryModes.Progressive, SkillDiscoveryModes.Normalize(null));
        Assert.AreEqual(SkillDiscoveryModes.Progressive, SkillDiscoveryModes.Normalize(""));
        Assert.AreEqual(SkillDiscoveryModes.Progressive, SkillDiscoveryModes.Normalize("  "));
        Assert.AreEqual(SkillDiscoveryModes.Progressive, SkillDiscoveryModes.Normalize("unknown"));
        Assert.AreEqual(SkillDiscoveryModes.Direct, SkillDiscoveryModes.Normalize("direct"));
        Assert.AreEqual(SkillDiscoveryModes.Direct, SkillDiscoveryModes.Normalize("DIRECT"));
        Assert.IsFalse(SkillDiscoveryModes.IsDirect(null));
        Assert.IsTrue(SkillDiscoveryModes.IsDirect("direct"));
    }

    [TestMethod]
    public async Task Direct_Advertise_IncludesAllowlistShortDescriptions()
    {
        CreateSkill("alpha-skill", "Alpha skill short description.");
        CreateSkill("beta-skill", "Beta skill short description.");
        var context = CreateDirectContext(["alpha-skill", "beta-skill"]);
        var source = new HostModeLoadableSkillsSource(context);

        var skills = await source.GetSkillsAsync(CancellationToken.None);
        var names = skills.Select(static s => s.Frontmatter.Name).ToArray();

        Assert.HasCount(2, skills);
        Assert.IsTrue(names.Contains("alpha-skill", StringComparer.OrdinalIgnoreCase));
        Assert.IsTrue(names.Contains("beta-skill", StringComparer.OrdinalIgnoreCase));
        Assert.IsFalse(names.Contains("host-skills-guide", StringComparer.OrdinalIgnoreCase));
        Assert.AreEqual(
            "Alpha skill short description.",
            skills.Single(static s => s.Frontmatter.Name == "alpha-skill").Frontmatter.Description);
    }

    [TestMethod]
    public async Task Direct_AdvertiseDescription_DoesNotContainSkillBody()
    {
        // 验证：Advertise 用短描述；body 不进入 Frontmatter.Description（防上下文膨胀主风险面）
        CreateSkill("alpha-skill", "Short advertise only.");
        var context = CreateDirectContext(["alpha-skill"]);
        var source = new HostModeLoadableSkillsSource(context);
        var skills = await source.GetSkillsAsync(CancellationToken.None);
        var alpha = skills.Single(static s => s.Frontmatter.Name == "alpha-skill");

        Assert.AreEqual("Short advertise only.", alpha.Frontmatter.Description);
        Assert.DoesNotContain("body-marker-alpha-skill", alpha.Frontmatter.Description, StringComparison.Ordinal);
        // body 不进 Advertise description 即消除「名表=全文灌广告」主风险；
        // body 仍随 AgentInlineSkill 注册供 MAF load_skill 按需读取（与 progressive Loadable 同路径）。
    }

    [TestMethod]
    public void Direct_LoadAllowlisted_SucceedsWithoutSearch()
    {
        CreateSkill("alpha-skill", "Alpha.");
        var context = CreateDirectContext(["alpha-skill"]);

        Assert.IsTrue(
            MafSkillsAgentFactory.TryResolveLoadPath(context, "alpha-skill", out var path, out var error),
            error);
        Assert.IsFalse(string.IsNullOrWhiteSpace(path));
        Assert.IsNull(error);
    }

    [TestMethod]
    public void Direct_LoadOutsideAllowlist_IsRejected()
    {
        CreateSkill("alpha-skill", "Alpha.");
        CreateSkill("other-skill", "Other.");
        var context = CreateDirectContext(["alpha-skill"]);

        Assert.IsFalse(
            MafSkillsAgentFactory.TryResolveLoadPath(context, "other-skill", out _, out var error));
        Assert.Contains("allowlist", error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("skill.search", error!, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void Direct_SearchHook_IsNoOp()
    {
        CreateSkill("alpha-skill", "Alpha.");
        var store = new SkillActivationStore();
        var applied = SkillSearchActivationHook.TryApplySearchResult(
            store,
            "host.skill.search",
            "skill.search",
            """{"hits":[{"name":"alpha-skill","description":"should not activate"}]}""",
            ["alpha-skill"],
            skillDiscoveryMode: SkillDiscoveryModes.Direct);

        Assert.AreEqual(0, applied);
        Assert.IsEmpty(store.GetLoadableNames(["alpha-skill"]));
    }

    [TestMethod]
    public async Task Direct_AllowlistLongerThan24_AllLoadable()
    {
        var allowlist = new List<string>(30);
        for (var i = 1; i <= 30; i++)
        {
            var name = $"skill-{i:D2}";
            CreateSkill(name, $"Description for {name}.");
            allowlist.Add(name);
        }

        var store = new SkillActivationStore(); // progressive ceiling 24 must not apply
        var context = CreateDirectContext([.. allowlist], store);
        var source = new HostModeLoadableSkillsSource(context);
        var skills = await source.GetSkillsAsync(CancellationToken.None);

        Assert.HasCount(30, skills);
        Assert.AreEqual(0, store.Count);

        Assert.IsTrue(
            MafSkillsAgentFactory.TryResolveLoadPath(context, "skill-30", out _, out var error),
            error);
        Assert.IsTrue(
            MafSkillsAgentFactory.TryResolveLoadPath(context, "skill-01", out _, out error),
            error);
    }

    [TestMethod]
    public void Direct_ProgressiveStillRequiresActivation()
    {
        CreateSkill("alpha-skill", "Alpha.");
        var context = CreateHostContext(
            new SkillActivationStore(),
            ["alpha-skill"],
            SkillDiscoveryModes.Progressive);

        Assert.IsFalse(
            MafSkillsAgentFactory.TryResolveLoadPath(context, "alpha-skill", out _, out var error));
        Assert.Contains("skill.search", error!, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void SkillsCapabilities_DiscoveryDirect_DefaultsFalse_AndCanBeTrue()
    {
        var defaults = new SkillsCapabilities();
        Assert.IsFalse(defaults.DiscoveryDirect);

        var enabled = new SkillsCapabilities(DiscoveryDirect: true);
        Assert.IsTrue(enabled.DiscoveryDirect);
    }

    [TestMethod]
    public void AgentRef_SkillDiscoveryMode_RoundTripsViaSourceGen()
    {
        var agent = new AgentRef(
            "agent-1",
            "v1",
            "prompt",
            SkillIds: ["alpha-skill"],
            SkillDiscoveryMode: "direct");
        var json = JsonSerializer.Serialize(agent, RuntimeJsonContext.Default.AgentRef);
        var restored = JsonSerializer.Deserialize(json, RuntimeJsonContext.Default.AgentRef);
        Assert.IsNotNull(restored);
        Assert.AreEqual("direct", restored!.SkillDiscoveryMode);
        Assert.Contains("skillDiscoveryMode", json, StringComparison.Ordinal);
    }

    [TestMethod]
    public void MeetingParticipant_SkillDiscoveryMode_RoundTripsViaHandWrittenJson()
    {
        var participant = new MeetingParticipant(
            "p1",
            new AgentRef(
                "agent-1",
                "v1",
                "prompt",
                SkillIds: ["alpha-skill"],
                SkillDiscoveryMode: "direct"),
            "Alice",
            1,
            ParticipantStatus.Active);
        var json = JsonSerializer.Serialize(participant, RuntimeJsonContext.Default.MeetingParticipant);
        var restored = JsonSerializer.Deserialize(json, RuntimeJsonContext.Default.MeetingParticipant);
        Assert.IsNotNull(restored);
        Assert.AreEqual("direct", restored!.Agent.SkillDiscoveryMode);
        Assert.Contains("skillDiscoveryMode", json, StringComparison.Ordinal);
    }

    private void CreateSkill(string name, string description)
    {
        var skillPath = Path.Combine(_sourceRoot, name);
        Directory.CreateDirectory(skillPath);
        File.WriteAllText(
            Path.Combine(skillPath, "SKILL.md"),
            $"""
            ---
            name: {name}
            description: {description}
            ---
            # {name} body
            body-marker-{name}
            """);
        _pathsByName[name] = skillPath;
        _descriptionsByName[name] = description;
    }

    private SkillsInvocationContext CreateDirectContext(
        string[] allowlist,
        SkillActivationStore? store = null) =>
        CreateHostContext(store ?? new SkillActivationStore(), allowlist, SkillDiscoveryModes.Direct);

    private SkillsInvocationContext CreateHostContext(
        SkillActivationStore store,
        string[] allowlist,
        string discoveryMode) =>
        new(
            "catalog-v1",
            [],
            [],
            [],
            SkillMaterializedPaths: [],
            IsHostMode: true,
            ActivationStore: store,
            AllowlistSkillIds: allowlist,
            CatalogMaterializedPathsByName: new Dictionary<string, string>(
                _pathsByName,
                StringComparer.OrdinalIgnoreCase),
            CatalogDescriptionsByName: new Dictionary<string, string>(
                _descriptionsByName,
                StringComparer.OrdinalIgnoreCase),
            ScopeSourceRoots:
            [
                new SkillScopeSourceRoot("workspace", _sourceRoot)
            ],
            SkillDiscoveryMode: discoveryMode);
}
