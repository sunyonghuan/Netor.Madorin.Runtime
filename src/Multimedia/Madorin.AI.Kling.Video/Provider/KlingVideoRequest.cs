using System.Text.Json.Serialization;

namespace Madorin.AI.Kling.Video.Provider;

internal sealed record KlingWatermark(bool Enabled);

internal sealed record KlingVideoSettings(int? Duration, string Resolution, string? AspectRatio);

internal sealed record KlingVideoOptions(KlingWatermark? WatermarkInfo);

internal sealed record KlingContent(string Type, string? Text, string? Url);

internal sealed record KlingTextVideoRequest(string Prompt, KlingVideoSettings Settings, KlingVideoOptions? Options);

internal sealed record KlingImageVideoRequest(KlingContent[] Contents, KlingVideoSettings Settings, KlingVideoOptions? Options);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(KlingTextVideoRequest))]
[JsonSerializable(typeof(KlingImageVideoRequest))]
[JsonSerializable(typeof(KlingVideoSettings))]
[JsonSerializable(typeof(KlingVideoOptions))]
[JsonSerializable(typeof(KlingWatermark))]
[JsonSerializable(typeof(KlingContent))]
[JsonSerializable(typeof(KlingContent[]))]
internal sealed partial class KlingVideoJsonContext : JsonSerializerContext;
