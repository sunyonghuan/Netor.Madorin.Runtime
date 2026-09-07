using System.Net.Http;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.OpenAICompatible;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Provider.Tests;

/// <summary>
/// Live probe against ~/.madorin/config.json. Opt-in via MADORIN_LIVE_SKILLS_PROBE=1.
/// Verifies real outbound tool/skill payloads and model behavior for host-mode Skills.
/// </summary>
[TestClass]
public sealed class HostModeSkillsLiveProbeTests
{
    private const string SkillBodyMarker = "secret-body-marker";
    private const string LongCatalogDescription =
        "A long catalog description that must not be advertised until activated and should stay truncated.";

    private readonly TestContext _testContext;
    private string _root = null!;
    private string _skillPath = null!;
    private string _sourceRoot = null!;

    public HostModeSkillsLiveProbeTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "madorin-host-skills-live",
            Guid.NewGuid().ToString("N"));
        _sourceRoot = Path.Combine(_root, "workspace-skills");
        _skillPath = Path.Combine(_sourceRoot, "demo-skill");
        Directory.CreateDirectory(_skillPath);
        File.WriteAllText(
            Path.Combine(_skillPath, "SKILL.md"),
            $"""
            ---
            name: demo-skill
            description: {LongCatalogDescription}
            ---
            # Demo Skill Body
            {SkillBodyMarker}
            """);
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
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Live_HostModeEmptyLoadable_OutboundRequestContainsSkillToolsWithoutBody()
    {
        EnsureLiveProbeEnabled();
        var (provider, modelId, capture, adapter) = await CreateLiveAdapterAsync()
            .ConfigureAwait(false);
        await using var _ = adapter as IAsyncDisposable;
        using var __ = adapter as IDisposable;

        var store = new SkillActivationStore();
        var skills = CreateHostSkills(store, activated: false);
        var request = CreateRequest(
            modelId,
            provider,
            skills,
            """
            List the names of skill-related tools you can call right now.
            Reply with a JSON array of tool names only. Do not call any tool.
            """);

        var events = await ProviderAdapterTestFactory.CollectAsync(
                adapter,
                request,
                _testContext.CancellationToken)
            .ConfigureAwait(false);

        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>(), FormatFailure(events));
        Assert.IsNotEmpty(capture.RequestBodies);

        var outbound = string.Join("\n---\n", capture.RequestBodies);
        StringAssert.Contains(outbound, "load_skill", StringComparison.Ordinal);
        StringAssert.Contains(outbound, "read_skill_resource", StringComparison.Ordinal);
        StringAssert.Contains(outbound, "run_skill_script", StringComparison.Ordinal);
        Assert.DoesNotContain(SkillBodyMarker, outbound, StringComparison.Ordinal);
        Assert.DoesNotContain(LongCatalogDescription, outbound, StringComparison.Ordinal);

        var assistantText = ExtractAssistantText(events);
        _testContext.WriteLine($"provider={provider}; model={modelId}");
        _testContext.WriteLine($"assistant={assistantText}");
        _testContext.WriteLine($"outboundToolsSnippet={TrimForLog(outbound)}");

        Assert.IsFalse(string.IsNullOrWhiteSpace(assistantText));
        Assert.IsTrue(
            assistantText.Contains("load_skill", StringComparison.OrdinalIgnoreCase)
            || outbound.Contains("\"load_skill\"", StringComparison.Ordinal),
            "Expected the model reply or outbound tools payload to mention load_skill.");
    }

    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task Live_HostModeAfterActivation_ModelCanLoadSkillBody()
    {
        EnsureLiveProbeEnabled();
        var (provider, modelId, capture, adapter) = await CreateLiveAdapterAsync()
            .ConfigureAwait(false);
        await using var _ = adapter as IAsyncDisposable;
        using var __ = adapter as IDisposable;

        var store = new SkillActivationStore();
        var applied = SkillSearchActivationHook.TryApplySearchResult(
            store,
            "host.skill.search",
            "skill_search",
            """{"hits":[{"name":"demo-skill","scope":"workspace","description":"short hit desc"}]}""",
            ["demo-skill"]);
        Assert.AreEqual(1, applied);

        var skills = CreateHostSkills(store, activated: true);
        var request = CreateRequest(
            modelId,
            provider,
            skills,
            """
            You must call load_skill with name "demo-skill".
            After the tool returns, reply with the exact marker word found in the skill body.
            Do not invent the marker.
            """);

        var events = await ProviderAdapterTestFactory.CollectAsync(
                adapter,
                request,
                _testContext.CancellationToken)
            .ConfigureAwait(false);

        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>(), FormatFailure(events));
        Assert.IsNotEmpty(capture.RequestBodies);

        var outbound = string.Join("\n---\n", capture.RequestBodies);
        StringAssert.Contains(outbound, "demo-skill", StringComparison.Ordinal);
        StringAssert.Contains(outbound, "short hit desc", StringComparison.Ordinal);
        Assert.DoesNotContain(LongCatalogDescription, outbound, StringComparison.Ordinal);

        var assistantText = ExtractAssistantText(events);
        var toolCalls = events.OfType<ToolCallCompleteProviderEvent>().ToArray();

        _testContext.WriteLine($"provider={provider}; model={modelId}");
        _testContext.WriteLine($"assistant={assistantText}");
        _testContext.WriteLine(
            $"toolCalls={string.Join(',', toolCalls.Select(static call => $"{call.Name}/{call.ToolId}"))}");
        _testContext.WriteLine($"outboundSnippet={TrimForLog(outbound)}");

        var sawLoadSkillCall = toolCalls.Any(static call =>
            call.Name.Contains("load_skill", StringComparison.OrdinalIgnoreCase)
            || call.ToolId.Contains("load_skill", StringComparison.OrdinalIgnoreCase));
        var sawMarker = assistantText.Contains(SkillBodyMarker, StringComparison.Ordinal)
            || outbound.Contains(SkillBodyMarker, StringComparison.Ordinal);

        Assert.IsTrue(
            sawLoadSkillCall || sawMarker,
            "Expected either an explicit load_skill tool call or the skill body marker after MAF auto-invocation.");
    }

    private SkillsInvocationContext CreateHostSkills(SkillActivationStore store, bool activated)
    {
        var paths = activated
            ? MafSkillsAgentFactory.ResolveHostLoadableMaterializedPaths(
                new SkillsInvocationContext(
                    "catalog-live",
                    [],
                    [],
                    [],
                    [],
                    IsHostMode: true,
                    ActivationStore: store,
                    AllowlistSkillIds: ["demo-skill"],
                    CatalogMaterializedPathsByName: new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["demo-skill"] = _skillPath
                    },
                    CatalogDescriptionsByName: new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["demo-skill"] = LongCatalogDescription
                    },
                    ScopeSourceRoots: [new SkillScopeSourceRoot("workspace", _sourceRoot)]))
            : [];

        return new SkillsInvocationContext(
            "catalog-live",
            [],
            [],
            [],
            paths,
            IsHostMode: true,
            ActivationStore: store,
            AllowlistSkillIds: ["demo-skill"],
            CatalogMaterializedPathsByName: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["demo-skill"] = _skillPath
            },
            CatalogDescriptionsByName: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["demo-skill"] = LongCatalogDescription
            },
            ScopeSourceRoots: [new SkillScopeSourceRoot("workspace", _sourceRoot)]);
    }

    private static RuntimeProviderRequest CreateRequest(
        string modelId,
        string providerId,
        SkillsInvocationContext skills,
        string prompt)
    {
        ToolDescriptor[] hostTools =
        [
            new(
                "host.skill.search",
                "host",
                "skill.search",
                "Search skills by query and return hits.",
                """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}"""),
            new(
                "host.skill.dir.host_global",
                "host",
                "skill.dir.host_global",
                "Returns the host-global skills root path.",
                """{"type":"object","properties":{}}""")
        ];

        return new RuntimeProviderRequest(
            Guid.NewGuid().ToString("N"),
            "live-host-skills-agent",
            providerId,
            modelId,
            [new RuntimeProviderMessage(RuntimeProviderRoles.User, [new TextContentBlock(prompt)])],
            hostTools,
            Temperature: 0f,
            MaxTokens: 800,
            Skills: skills);
    }

    private async Task<(string Provider, string ModelId, CapturingHandler Capture, IRuntimeProviderAdapter Adapter)>
        CreateLiveAdapterAsync()
    {
        var loader = new StandaloneConfigLoader();
        var config = await loader.LoadAsync(_testContext.CancellationToken).ConfigureAwait(false)
            ?? throw new AssertFailedException($"Missing config at {loader.ConfigPath}");

        var providerName = string.IsNullOrWhiteSpace(config.DefaultProvider)
            ? config.Providers.FirstOrDefault()?.Name
            : config.DefaultProvider;
        Assert.IsFalse(string.IsNullOrWhiteSpace(providerName), "No default provider configured.");

        var entry = config.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, providerName, StringComparison.Ordinal))
            ?? throw new AssertFailedException($"Provider '{providerName}' not found in config.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(entry.ApiKey), "Provider apiKey is empty.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(entry.BaseUrl), "Provider baseUrl is empty.");

        var modelId = string.IsNullOrWhiteSpace(config.DefaultModel)
            ? entry.Models.FirstOrDefault()
            : config.DefaultModel;
        Assert.IsFalse(string.IsNullOrWhiteSpace(modelId), "No model configured.");

        var capture = new CapturingHandler
        {
            InnerHandler = new HttpClientHandler()
        };
        var httpClient = new HttpClient(capture);
        ProviderModel[] models =
        [
            new(
                modelId!,
                modelId!,
                Capabilities: ProviderAdapterTestFactory.CreateCapabilities())
        ];
        var options = new OpenAICompatibleProviderOptions(
            entry.Name,
            entry.BaseUrl.TrimEnd('/'),
            entry.ApiKey,
            AuthenticationHeader: string.IsNullOrWhiteSpace(entry.AuthenticationHeader)
                ? "Authorization"
                : entry.AuthenticationHeader,
            AuthenticationScheme: entry.AuthenticationScheme ?? "Bearer",
            Capabilities: ProviderAdapterTestFactory.CreateCapabilities(),
            Models: models,
            UserAgent: string.IsNullOrWhiteSpace(entry.UserAgent) ? null : entry.UserAgent);
        IRuntimeProviderAdapter adapter = new OpenAICompatibleProviderAdapter(
            options,
            httpClient,
            disposeHttpClient: true);
        return (entry.Name, modelId!, capture, adapter);
    }

    private static void EnsureLiveProbeEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("MADORIN_LIVE_SKILLS_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                "Set MADORIN_LIVE_SKILLS_PROBE=1 to run the live host-mode skills probe against ~/.madorin/config.json.");
        }
    }

    private static string ExtractAssistantText(IEnumerable<RuntimeProviderEvent> events)
    {
        var builder = new StringBuilder();
        foreach (var providerEvent in events)
        {
            if (providerEvent is TextDeltaProviderEvent textDelta)
            {
                builder.Append(textDelta.Delta);
            }
        }

        return builder.ToString();
    }

    private static string FormatFailure(IEnumerable<RuntimeProviderEvent> events)
    {
        var failed = events.OfType<InvocationFailedProviderEvent>().FirstOrDefault();
        return failed is null
            ? "no failure"
            : $"{failed.Error.Code}: {failed.Error.Message}";
    }

    private static string TrimForLog(string value)
    {
        const int max = 4000;
        return value.Length <= max ? value : value[..max] + "…";
    }

    private sealed class CapturingHandler : DelegatingHandler
    {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
                RequestBodies.Add(body);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
