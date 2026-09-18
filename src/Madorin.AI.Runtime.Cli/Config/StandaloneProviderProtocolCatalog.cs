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
    string? DefaultProbePath);

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
            null),
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
            null),
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
            "models"),
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
            null),
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
            null),
        new(
            "Aliyun",
            "Aliyun / DashScope",
            true,
            "https://dashscope.aliyuncs.com/compatible-mode/v1",
            "qwen-plus",
            false,
            true,
            "Authorization",
            "Bearer",
            "models"),
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
            null),
        new(
            "Gemini",
            "Gemini",
            true,
            "https://generativelanguage.googleapis.com/v1beta/openai",
            "gemini-2.5-flash",
            false,
            true,
            "Authorization",
            "Bearer",
            "models"),
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
            "models"),
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
            "models"),
        new(
            "Ollama",
            "Ollama",
            true,
            "http://localhost:11434/v1",
            "llama3.2",
            false,
            false,
            "Authorization",
            "Bearer",
            null)
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
}
