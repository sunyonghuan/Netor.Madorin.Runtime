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

namespace Madorin.AI.Runtime.Server;

/// <summary>
/// Protocol → adapter mapping shared by host Registry and standalone provider factory.
/// </summary>
public static class RuntimeProviderAdapterFactory
{
    private static readonly HashSet<string> KnownProtocols = new(StringComparer.Ordinal)
    {
        "OpenAI",
        "Anthropic",
        "OpenAI Compatible",
        "DeepSeek",
        "Kimi",
        "Aliyun",
        "Azure OpenAI",
        "Gemini",
        "GLM",
        "Custom",
        "Ollama"
    };

    public static bool IsKnownProtocol(string protocol) =>
        !string.IsNullOrWhiteSpace(protocol) && KnownProtocols.Contains(protocol.Trim());

    public static IRuntimeProviderAdapter Create(ProviderConnectionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Protocol);

        var protocol = config.Protocol.Trim();
        return protocol switch
        {
            "OpenAI" => BuildOpenAI(config),
            "Anthropic" => BuildAnthropic(config),
            "DeepSeek" => BuildDeepSeek(config),
            "Kimi" => BuildKimi(config),
            "Aliyun" => BuildAliyun(config),
            "Azure OpenAI" => BuildAzureOpenAI(config),
            "Gemini" => BuildGemini(config),
            "GLM" => BuildGlm(config),
            "Ollama" => BuildOllama(config),
            "OpenAI Compatible" or "Custom" => BuildCompatible(config),
            _ => throw new InvalidOperationException(
                $"Provider '{config.ProviderId}' uses unsupported protocol '{config.Protocol}'.")
        };
    }

    private static OpenAIProviderAdapter BuildOpenAI(ProviderConnectionConfig config) =>
        new(
            config.ProviderId,
            config.ApiKey,
            GetBaseUrl(config, "https://api.openai.com/v1"),
            ToProviderModels(config.Models));

    private static AnthropicProviderAdapter BuildAnthropic(ProviderConnectionConfig config) =>
        new(
            config.ProviderId,
            config.ApiKey,
            GetBaseUrl(config, "https://api.anthropic.com"),
            ToProviderModels(config.Models));

    private static DeepSeekProviderAdapter BuildDeepSeek(ProviderConnectionConfig config) =>
        new(
            GetBaseUrl(config, "https://api.deepseek.com/v1"),
            config.ApiKey,
            models: ToProviderModels(config.Models),
            providerId: config.ProviderId);

    private static KimiProviderAdapter BuildKimi(ProviderConnectionConfig config) =>
        new(
            config.ApiKey,
            baseUrl: GetBaseUrl(config, "https://api.moonshot.cn/v1"),
            models: ToProviderModels(config.Models),
            providerId: config.ProviderId,
            maxTools: config.MaxTools);

    private static AliyunProviderAdapter BuildAliyun(ProviderConnectionConfig config) =>
        new(
            new AliyunProviderOptions(
                config.ProviderId,
                config.ApiKey,
                GetBaseUrl(config, AliyunModelCatalog.DefaultBaseUrl),
                config.Capabilities,
                ToProviderModels(config.Models),
                ConfigurationVersionOrDefault(config)));

    private static AzureOpenAIProviderAdapter BuildAzureOpenAI(ProviderConnectionConfig config) =>
        new(
            new AzureOpenAIProviderOptions(
                config.ProviderId,
                config.ApiKey,
                GetBaseUrl(config, string.Empty),
                ToProviderModels(config.Models),
                config.ApiVersion ?? AzureOpenAIProtocol.DefaultApiVersion,
                config.Capabilities,
                ConfigurationVersionOrDefault(config)));

    private static GeminiProviderAdapter BuildGemini(ProviderConnectionConfig config) =>
        new(
            new GeminiProviderOptions(
                config.ProviderId,
                config.ApiKey,
                GetBaseUrl(config, GeminiModelCatalog.DefaultBaseUrl),
                config.Capabilities,
                ToProviderModels(config.Models),
                ConfigurationVersionOrDefault(config)));

    private static GlmProviderAdapter BuildGlm(ProviderConnectionConfig config) =>
        new(
            new GlmProviderOptions(
                config.ProviderId,
                config.ApiKey,
                GetBaseUrl(config, string.Empty),
                config.Capabilities,
                ToProviderModels(config.Models),
                ConfigurationVersionOrDefault(config),
                config.ProbePath));

    private static OllamaProviderAdapter BuildOllama(ProviderConnectionConfig config) =>
        new(
            new OllamaProviderOptions(
                config.ProviderId,
                GetBaseUrl(config, OllamaModelCatalog.DefaultBaseUrl),
                config.ApiKey ?? string.Empty,
                AuthenticationHeaderOrDefault(config),
                config.AuthenticationScheme,
                string.IsNullOrWhiteSpace(config.ApiKey)
                    ? ProviderAuthenticationMode.None
                    : ProviderAuthenticationMode.Required,
                config.Capabilities,
                ToProviderModels(config.Models),
                ConfigurationVersionOrDefault(config)));

    private static OpenAICompatibleProviderAdapter BuildCompatible(ProviderConnectionConfig config)
    {
        var baseUrl = string.IsNullOrWhiteSpace(config.BaseUrl)
            ? "https://api.openai.com/v1"
            : config.BaseUrl.Trim();
        var options = new OpenAICompatibleProviderOptions(
            config.ProviderId,
            baseUrl,
            config.ApiKey,
            AuthenticationHeaderOrDefault(config),
            config.AuthenticationScheme,
            config.Capabilities,
            ToProviderModels(config.Models),
            ConfigurationVersionOrDefault(config),
            config.ProbePath,
            UserAgent: config.UserAgent);
        return new OpenAICompatibleProviderAdapter(
            options,
            ProviderHttpClientFactory.Shared.CreateClient(config.ProviderId),
            disposeHttpClient: true);
    }

    private static string GetBaseUrl(ProviderConnectionConfig config, string defaultBaseUrl) =>
        string.IsNullOrWhiteSpace(config.BaseUrl)
            ? defaultBaseUrl
            : config.BaseUrl.Trim();

    private static string ConfigurationVersionOrDefault(ProviderConnectionConfig config) =>
        string.IsNullOrWhiteSpace(config.ConfigurationVersion)
            ? "1"
            : config.ConfigurationVersion.Trim();

    private static string AuthenticationHeaderOrDefault(ProviderConnectionConfig config) =>
        string.IsNullOrWhiteSpace(config.AuthenticationHeader)
            ? "Authorization"
            : config.AuthenticationHeader.Trim();

    private static List<ProviderModel> ToProviderModels(IReadOnlyList<string>? ids) =>
        ids is null || ids.Count == 0
            ? []
            : ids.Select(static id => new ProviderModel(id, id)).ToList();
}
