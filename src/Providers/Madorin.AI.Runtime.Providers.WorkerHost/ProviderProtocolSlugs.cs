using System.Diagnostics.CodeAnalysis;

namespace Madorin.AI.Runtime.Providers.WorkerHost;

/// <summary>
/// Frozen protocol → slug map from scheme 12 §7. Custom shares openai-compatible.
/// </summary>
public static class ProviderProtocolSlugs
{
    public static bool TryMap(string? protocol, [NotNullWhen(true)] out string? slug)
    {
        slug = protocol?.Trim() switch
        {
            "OpenAI" => "openai",
            "Anthropic" => "anthropic",
            "OpenAI Compatible" => "openai-compatible",
            "Custom" => "openai-compatible",
            "DeepSeek" => "deepseek",
            "Kimi" => "kimi",
            "Aliyun" => "aliyun",
            "Azure OpenAI" => "azure-openai",
            "Gemini" => "gemini",
            "GLM" => "glm",
            "Ollama" => "ollama",
            _ => null
        };
        return slug is not null;
    }
}
