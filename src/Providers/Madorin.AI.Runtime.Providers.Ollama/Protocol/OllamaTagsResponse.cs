using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Providers.Ollama;

internal sealed class OllamaTagsResponse
{
    [JsonPropertyName("models")]
    public OllamaTagModel[]? Models { get; init; }
}

internal sealed class OllamaTagModel
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("details")]
    public OllamaTagDetails? Details { get; init; }
}

internal sealed class OllamaTagDetails
{
    [JsonPropertyName("family")]
    public string? Family { get; init; }
}
