using Madorin.AI.Runtime.Providers.WorkerHost;

namespace Madorin.AI.Runtime.Server;

/// <summary>
/// Protocol → slug mapping shared by host Registry and standalone provider factory.
/// Adapter construction lives in each Worker process.
/// </summary>
public static class RuntimeProviderAdapterFactory
{
    private static readonly HashSet<string> KnownProtocols = new(StringComparer.OrdinalIgnoreCase)
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

    public static bool TryMapProtocolToSlug(string protocol, out string slug)
    {
        if (ProviderProtocolSlugs.TryMap(protocol, out var mapped) && mapped is not null)
        {
            slug = mapped;
            return true;
        }

        slug = string.Empty;
        return false;
    }
}
