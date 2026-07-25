using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Providers.Aliyun;

internal sealed class AliyunModelCatalogResponse
{
    [JsonPropertyName("data")]
    public AliyunModelCatalogItem[]? Data { get; init; }

    [JsonPropertyName("object")]
    public string? Object { get; init; }
}

internal sealed class AliyunModelCatalogItem
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("object")]
    public string? Object { get; init; }

    [JsonPropertyName("created")]
    public long? Created { get; init; }

    [JsonPropertyName("owned_by")]
    public string? OwnedBy { get; init; }
}
