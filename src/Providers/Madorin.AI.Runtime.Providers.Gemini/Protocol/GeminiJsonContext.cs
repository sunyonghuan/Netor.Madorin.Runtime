using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Providers.Gemini;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(GeminiModelCatalogResponse))]
internal sealed partial class GeminiJsonContext : JsonSerializerContext
{
}
