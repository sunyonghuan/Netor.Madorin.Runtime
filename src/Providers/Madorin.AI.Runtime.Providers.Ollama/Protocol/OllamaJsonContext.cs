using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Providers.Ollama;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(OllamaTagsResponse))]
[JsonSerializable(typeof(OllamaTagModel))]
[JsonSerializable(typeof(OllamaTagDetails))]
internal sealed partial class OllamaJsonContext : JsonSerializerContext
{
}
