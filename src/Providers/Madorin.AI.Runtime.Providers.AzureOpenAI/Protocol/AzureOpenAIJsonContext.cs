using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Providers.AzureOpenAI;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(AzureOpenAIErrorResponse))]
[JsonSerializable(typeof(AzureOpenAIError))]
internal sealed partial class AzureOpenAIJsonContext : JsonSerializerContext
{
}
