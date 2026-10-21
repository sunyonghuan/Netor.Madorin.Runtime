using System.Text.Json.Serialization;

namespace Madorin.AI.OpenAI.Image.Provider;

internal sealed record OpenAiImageRequest(
    string Model, string Prompt, int N, string Size, string Quality, string OutputFormat);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(OpenAiImageRequest))]
internal sealed partial class OpenAiJsonContext : JsonSerializerContext;
