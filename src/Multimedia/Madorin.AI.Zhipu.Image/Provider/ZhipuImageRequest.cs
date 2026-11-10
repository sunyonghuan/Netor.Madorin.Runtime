using System.Text.Json.Serialization;

namespace Madorin.AI.Zhipu.Image.Provider;

internal sealed record ZhipuImageRequest(
    string Model,
    string Prompt,
    string Size,
    string Quality,
    bool? WatermarkEnabled,
    string? UserId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ZhipuImageRequest))]
internal sealed partial class ZhipuImageJsonContext : JsonSerializerContext;
