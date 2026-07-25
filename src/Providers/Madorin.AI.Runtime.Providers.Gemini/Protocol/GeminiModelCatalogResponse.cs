using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Providers.Gemini;

internal sealed class GeminiModelCatalogResponse
{
    [JsonPropertyName("data")]
    public GeminiModelCatalogItem[]? Data { get; init; }
}

internal sealed class GeminiModelCatalogItem
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("owned_by")]
    public string? OwnedBy { get; init; }
}
