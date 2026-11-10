using System.Text.Json.Serialization;

namespace Madorin.AI.Zhipu.Video.Provider;

internal sealed record ZhipuVideoRequest(
    string Model,
    string? Prompt,
    string? ImageUrl,
    string? Quality,
    bool? WithAudio,
    string? Size,
    int? Fps,
    int? Duration,
    bool? WatermarkEnabled,
    string? RequestId,
    string? UserId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ZhipuVideoRequest))]
internal sealed partial class ZhipuVideoJsonContext : JsonSerializerContext;
