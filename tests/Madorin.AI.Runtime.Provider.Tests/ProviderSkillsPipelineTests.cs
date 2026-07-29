using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.Aliyun;
using Madorin.AI.Runtime.Providers.Anthropic;
using Madorin.AI.Runtime.Providers.AzureOpenAI;
using Madorin.AI.Runtime.Providers.DeepSeek;
using Madorin.AI.Runtime.Providers.Gemini;
using Madorin.AI.Runtime.Providers.GLM;
using Madorin.AI.Runtime.Providers.Kimi;
using Madorin.AI.Runtime.Providers.OpenAI;
using Madorin.AI.Runtime.Providers.OpenAICompatible;
using Madorin.AI.Runtime.Providers.Ollama;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ProviderSkillsPipelineTests
{
    private const string SkillBodyMarker = "secret-body-marker";

    private readonly TestContext _testContext;
    private string _root = null!;
    private string _skillPath = null!;

    public ProviderSkillsPipelineTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "madorin-provider-skills",
            Guid.NewGuid().ToString("N"));
        _skillPath = Path.Combine(_root, "provider-skill");
        Directory.CreateDirectory(_skillPath);
        File.WriteAllText(
            Path.Combine(_skillPath, "SKILL.md"),
            $"---\nname: provider-skill\ndescription: Provider Skill\n---\n{SkillBodyMarker}\n");
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
    [DataRow(SkillsPipelineProvider.OpenAI)]
    [DataRow(SkillsPipelineProvider.Anthropic)]
    [DataRow(SkillsPipelineProvider.OpenAICompatible)]
    [DataRow(SkillsPipelineProvider.DeepSeek)]
    [DataRow(SkillsPipelineProvider.Kimi)]
    [DataRow(SkillsPipelineProvider.Aliyun)]
    [DataRow(SkillsPipelineProvider.AzureOpenAI)]
    [DataRow(SkillsPipelineProvider.Gemini)]
    [DataRow(SkillsPipelineProvider.GLM)]
    [DataRow(SkillsPipelineProvider.Ollama)]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task CompleteStreamingAsync_WithSkills_InjectsProgressiveToolsWithoutPreloadingBody(
        SkillsPipelineProvider provider)
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(GetProtocolKind(provider)),
            _testContext.CancellationToken);
        using var fixture = CreateAdapter(provider, server.BaseAddress);
        ToolDescriptor[] tools =
        [
            new(
                "weather",
                "host",
                "get_weather",
                "Gets the current weather.",
                "{\"type\":\"object\"}")
        ];
        var request = ProviderAdapterTestFactory.CreateRequest(tools) with
        {
            Skills = new SkillsInvocationContext(
                "catalog-v1",
                ["provider-skill"],
                ["content-hash-v1"],
                ["workspace"],
                [_skillPath])
        };

        var events = await ProviderAdapterTestFactory.CollectAsync(
            fixture.Adapter,
            request,
            _testContext.CancellationToken);

        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        Assert.IsEmpty(events.OfType<InvocationFailedProviderEvent>());
        var captured = Assert.ContainsSingle(server.Requests);
        Assert.Contains("load_skill", captured.Body, StringComparison.Ordinal);
        Assert.Contains("provider-skill", captured.Body, StringComparison.Ordinal);
        Assert.Contains("Provider Skill", captured.Body, StringComparison.Ordinal);
        Assert.Contains("get_weather", captured.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(SkillBodyMarker, captured.Body, StringComparison.Ordinal);
    }

    private static ProviderAdapterKind GetProtocolKind(SkillsPipelineProvider provider) => provider switch
    {
        SkillsPipelineProvider.OpenAI => ProviderAdapterKind.OpenAI,
        SkillsPipelineProvider.Anthropic => ProviderAdapterKind.Anthropic,
        _ => ProviderAdapterKind.OpenAICompatible
    };

    private static ProviderAdapterFixture CreateAdapter(
        SkillsPipelineProvider provider,
        Uri baseAddress)
    {
        var root = baseAddress.AbsoluteUri.TrimEnd('/');
        var endpoint = $"{root}/v1";
        ProviderModel[] models =
        [
            new(
                ProviderAdapterTestFactory.ModelId,
                "Test model",
                Capabilities: ProviderAdapterTestFactory.CreateCapabilities())
        ];
        IRuntimeProviderAdapter adapter = provider switch
        {
            SkillsPipelineProvider.OpenAI => new OpenAIProviderAdapter(
                ProviderAdapterTestFactory.ApiKey,
                endpoint,
                models),
            SkillsPipelineProvider.Anthropic => new AnthropicProviderAdapter(
                ProviderAdapterTestFactory.ApiKey,
                root,
                models),
            SkillsPipelineProvider.OpenAICompatible => new OpenAICompatibleProviderAdapter(
                new OpenAICompatibleProviderOptions(
                    "compatible",
                    endpoint,
                    ProviderAdapterTestFactory.ApiKey,
                    Capabilities: ProviderAdapterTestFactory.CreateCapabilities(),
                    Models: models),
                new HttpClient(),
                disposeHttpClient: true),
            SkillsPipelineProvider.DeepSeek => new DeepSeekProviderAdapter(
                endpoint,
                ProviderAdapterTestFactory.ApiKey,
                models: models),
            SkillsPipelineProvider.Kimi => new KimiProviderAdapter(
                ProviderAdapterTestFactory.ApiKey,
                endpoint,
                models: models),
            SkillsPipelineProvider.Aliyun => new AliyunProviderAdapter(
                new AliyunProviderOptions(
                    "aliyun",
                    ProviderAdapterTestFactory.ApiKey,
                    endpoint,
                    Models: models)),
            SkillsPipelineProvider.AzureOpenAI => new AzureOpenAIProviderAdapter(
                new AzureOpenAIProviderOptions(
                    "azure-openai",
                    ProviderAdapterTestFactory.ApiKey,
                    root,
                    models)),
            SkillsPipelineProvider.Gemini => new GeminiProviderAdapter(
                new GeminiProviderOptions(
                    "gemini",
                    ProviderAdapterTestFactory.ApiKey,
                    endpoint,
                    Models: models)),
            SkillsPipelineProvider.GLM => new GlmProviderAdapter(
                new GlmProviderOptions(
                    "glm",
                    ProviderAdapterTestFactory.ApiKey,
                    endpoint,
                    Models: models)),
            SkillsPipelineProvider.Ollama => new OllamaProviderAdapter(
                new OllamaProviderOptions(
                    "ollama",
                    endpoint,
                    Models: models)),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };

        return new ProviderAdapterFixture(adapter);
    }
}

public enum SkillsPipelineProvider
{
    OpenAI,
    Anthropic,
    OpenAICompatible,
    DeepSeek,
    Kimi,
    Aliyun,
    AzureOpenAI,
    Gemini,
    GLM,
    Ollama
}
