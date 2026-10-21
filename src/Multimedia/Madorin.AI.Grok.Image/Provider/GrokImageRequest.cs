using System.Text.Json.Serialization;

namespace Madorin.AI.Grok.Image.Provider;

internal sealed record GrokImageRequest(
    string Model, string Prompt, int N, string AspectRatio, string ResponseFormat);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GrokImageRequest))]
internal sealed partial class GrokJsonContext : JsonSerializerContext;
