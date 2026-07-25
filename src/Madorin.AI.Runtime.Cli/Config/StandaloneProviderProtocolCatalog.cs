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

namespace Madorin.AI.Runtime.Cli.Config;

internal sealed record StandaloneProviderProtocolDefinition(
    string Protocol,
    string DisplayName,
    bool IsVisibleInWizard,
    string DefaultBaseUrl,
    string DefaultModel,
    bool IsBaseUrlRequired,
    bool IsApiKeyRequired,
    string DefaultAuthenticationHeader,
    string? DefaultAuthenticationScheme,
    string? DefaultProbePath,
    Func<ProviderEntry, IRuntimeProviderAdapter> BuildAdapter);

internal static class StandaloneProviderProtocolCatalog
{
    private static readonly IReadOnlyList<StandaloneProviderProtocolDefinition> Definitions =
    [
        new(
            "OpenAI",
            "OpenAI",
            true,
            "https://api.openai.com/v1",
            "gpt-5",
            false,
            true,
            "Authorization",
            "Bearer",
            null,
            BuildOpenAI),
        new(
            "Anthropic",
            "Anthropic",
            true,
            "https://api.anthropic.com",
            "claude-opus-4-1",
            false,
            true,
            "x-api-key",
            null,
            null,
            BuildAnthropic),
        new(
            "OpenAI Compatible",
            "OpenAI Compatible",
            true,
            string.Empty,
            "gpt-5",
            true,
            true,
            "Authorization",
            "Bearer",
            "models",
            BuildCompatible),
        new(
            "DeepSeek",
            "DeepSeek",
            true,
            "https://api.deepseek.com/v1",
            "deepseek-chat",
            false,
            true,
            "Authorization",
            "Bearer",
            null,
            BuildDeepSeek),
        new(
            "Kimi",
            "Kimi",
            true,
            "https://api.moonshot.cn/v1",
            "moonshot-v1-8k",
            false,
            true,
            "Authorization",
            "Bearer",
            null,
            BuildKimi),
        new(
            "Aliyun",
            "Aliyun / DashScope",
            true,
            AliyunModelCatalog.DefaultBaseUrl,
            AliyunModelCatalog.DefaultModelId,
            false,
            true,
            "Authorization",
            "Bearer",
            "models",
            BuildAliyun),
        new(
            "Azure OpenAI",
            "Azure OpenAI (Experimental)",
            false,
            string.Empty,
            string.Empty,
            true,
            true,
            "api-key",
            null,
            null,
            BuildAzureOpenAI),
        new(
            "Gemini",
            "Gemini",
            true,
            GeminiModelCatalog.DefaultBaseUrl,
            GeminiModelCatalog.DefaultModelId,
            false,
            true,
            "Authorization",
            "Bearer",
            "models",
            BuildGemini),
        new(
            "GLM",
            "GLM",
            true,
            string.Empty,
            string.Empty,
            true,
            true,
            "Authorization",
            "Bearer",
            "models",
            BuildGlm),
        new(
            "Custom",
            "Custom",
            true,
            string.Empty,
            "gpt-5",
            true,
            true,
            "Authorization",
            "Bearer",
            "models",
            BuildCompatible),
        new(
            "Ollama",
            "Ollama",
            true,
            OllamaModelCatalog.DefaultBaseUrl,
            OllamaModelCatalog.DefaultModelId,
            false,
            false,
            "Authorization",
            "Bearer",
            null,
            BuildOllama)
    ];

    public static IReadOnlyList<StandaloneProviderProtocolDefinition> All => Definitions;

    public static IReadOnlyList<StandaloneProviderProtocolDefinition> WizardEntries { get; } =
        Definitions.Where(static definition => definition.IsVisibleInWizard).ToArray();

    public static StandaloneProviderProtocolDefinition GetRequired(string protocol) =>
        Definitions.FirstOrDefault(definition =>
            definition.Protocol.Equals(protocol, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"Unsupported protocol '{protocol}'.");

    public static bool TryGet(
        string protocol,
        out StandaloneProviderProtocolDefinition? definition)
    {
        definition = Definitions.FirstOrDefault(candidate =>
            candidate.Protocol.Equals(protocol, StringComparison.Ordinal));
        return definition is not null;
    }

    public static StandaloneProviderProtocolDefinition? ResolveWizardInput(string input)
    {
        if (int.TryParse(input, out var selectedIndex)
            && selectedIndex >= 1
            && selectedIndex <= WizardEntries.Count)
        {
            return WizardEntries[selectedIndex - 1];
        }

        return WizardEntries.FirstOrDefault(definition =>
            definition.Protocol.Equals(input, StringComparison.OrdinalIgnoreCase));
    }

    private static OpenAIProviderAdapter BuildOpenAI(ProviderEntry entry) =>
        new(
            entry.Name,
            entry.ApiKey,
            GetBaseUrl(entry, "OpenAI"),
            ToProviderModels(entry.Models));

    private static AnthropicProviderAdapter BuildAnthropic(ProviderEntry entry) =>
        new(
            entry.Name,
            entry.ApiKey,
            GetBaseUrl(entry, "Anthropic"),
            ToProviderModels(entry.Models));

    private static DeepSeekProviderAdapter BuildDeepSeek(ProviderEntry entry) =>
        new(
            GetBaseUrl(entry, "DeepSeek"),
            entry.ApiKey,
            models: ToProviderModels(entry.Models),
            providerId: entry.Name);

    private static KimiProviderAdapter BuildKimi(ProviderEntry entry) =>
        new(
            entry.ApiKey,
            baseUrl: GetBaseUrl(entry, "Kimi"),
            models: ToProviderModels(entry.Models),
            providerId: entry.Name);

    private static AliyunProviderAdapter BuildAliyun(ProviderEntry entry) =>
        new(
            new AliyunProviderOptions(
                entry.Name,
                entry.ApiKey,
                GetBaseUrl(entry, "Aliyun"),
                entry.Capabilities,
                ToProviderModels(entry.Models),
                entry.ConfigurationVersion));

    private static AzureOpenAIProviderAdapter BuildAzureOpenAI(ProviderEntry entry) =>
        new(
            new AzureOpenAIProviderOptions(
                entry.Name,
                entry.ApiKey,
                GetBaseUrl(entry, "Azure OpenAI"),
                ToProviderModels(entry.Models),
                entry.ApiVersion ?? AzureOpenAIProtocol.DefaultApiVersion,
                entry.Capabilities,
                entry.ConfigurationVersion));

    private static GeminiProviderAdapter BuildGemini(ProviderEntry entry) =>
        new(
            new GeminiProviderOptions(
                entry.Name,
                entry.ApiKey,
                GetBaseUrl(entry, "Gemini"),
                entry.Capabilities,
                ToProviderModels(entry.Models),
                entry.ConfigurationVersion));

    private static GlmProviderAdapter BuildGlm(ProviderEntry entry) =>
        new(
            new GlmProviderOptions(
                entry.Name,
                entry.ApiKey,
                GetBaseUrl(entry, "GLM"),
                entry.Capabilities,
                ToProviderModels(entry.Models),
                entry.ConfigurationVersion,
                entry.ProbePath));

    private static OllamaProviderAdapter BuildOllama(ProviderEntry entry) =>
        new(
            new OllamaProviderOptions(
                entry.Name,
                GetBaseUrl(entry, "Ollama"),
                entry.ApiKey,
                entry.AuthenticationHeader,
                entry.AuthenticationScheme,
                string.IsNullOrWhiteSpace(entry.ApiKey)
                    ? ProviderAuthenticationMode.None
                    : ProviderAuthenticationMode.Required,
                entry.Capabilities,
                ToProviderModels(entry.Models),
                entry.ConfigurationVersion));

    private static OpenAICompatibleProviderAdapter BuildCompatible(ProviderEntry entry)
    {
        var baseUrl = string.IsNullOrWhiteSpace(entry.BaseUrl)
            ? "https://api.openai.com/v1"
            : entry.BaseUrl;
        var options = new OpenAICompatibleProviderOptions(
            entry.Name,
            baseUrl,
            entry.ApiKey,
            entry.AuthenticationHeader,
            entry.AuthenticationScheme,
            entry.Capabilities,
            ToProviderModels(entry.Models),
            entry.ConfigurationVersion,
            entry.ProbePath);
        return new OpenAICompatibleProviderAdapter(
            options,
            ProviderHttpClientFactory.Shared.CreateClient(entry.Name),
            disposeHttpClient: true);
    }

    private static string GetBaseUrl(ProviderEntry entry, string protocol) =>
        string.IsNullOrWhiteSpace(entry.BaseUrl)
            ? GetRequired(protocol).DefaultBaseUrl
            : entry.BaseUrl;

    private static List<ProviderModel> ToProviderModels(IEnumerable<string> ids) =>
        ids.Select(static id => new ProviderModel(id, id)).ToList();
}
