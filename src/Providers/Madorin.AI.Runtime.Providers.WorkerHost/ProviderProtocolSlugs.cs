using System.Diagnostics.CodeAnalysis;

namespace Madorin.AI.Runtime.Providers.WorkerHost;

/// <summary>
/// Frozen protocol → slug map from scheme 12 §7. Custom shares openai-compatible.
/// </summary>
public static class ProviderProtocolSlugs
{
    public static bool TryMap(string? protocol, [NotNullWhen(true)] out string? slug)
    {
        slug = protocol?.Trim().ToLowerInvariant() switch
        {
            "openai" => "openai",
            "anthropic" => "anthropic",
            "openai compatible" => "openai-compatible",
            "custom" => "openai-compatible",
            "deepseek" => "deepseek",
            "kimi" => "kimi",
            "aliyun" => "aliyun",
            "azure openai" => "azure-openai",
            "gemini" => "gemini",
            "glm" => "glm",
            "ollama" => "ollama",
            _ => null
        };
        return slug is not null;
    }
}
