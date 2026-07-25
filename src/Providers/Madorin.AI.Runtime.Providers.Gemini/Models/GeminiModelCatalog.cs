using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Providers.Gemini;

public static class GeminiModelCatalog
{
    public const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai";
    public const string DefaultModelId = "gemini-2.5-flash";

    public static IReadOnlyList<ProviderModel> All { get; } =
    [
        new(
            DefaultModelId,
            "Gemini 2.5 Flash",
            Capabilities: new ProviderCapabilities(Streaming: true))
    ];
}
