using System.Text.Json.Serialization;

namespace Madorin.AI.Kling.Image.Provider;

internal sealed record KlingWatermark(bool Enabled);

internal sealed record KlingImageRequest(
    string ModelName,
    string Prompt,
    string? NegativePrompt,
    string Resolution,
    [property: JsonPropertyName("n")] int Count,
    string AspectRatio,
    KlingWatermark? WatermarkInfo);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(KlingImageRequest))]
[JsonSerializable(typeof(KlingWatermark))]
internal sealed partial class KlingImageJsonContext : JsonSerializerContext;
