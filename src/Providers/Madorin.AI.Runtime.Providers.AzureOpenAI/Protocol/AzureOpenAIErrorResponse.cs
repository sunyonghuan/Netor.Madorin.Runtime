using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Providers.AzureOpenAI;

internal sealed class AzureOpenAIErrorResponse
{
    [JsonPropertyName("error")]
    public AzureOpenAIError? Error { get; init; }

    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }
}

internal sealed class AzureOpenAIError
{
    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
