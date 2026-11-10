using System.Text.Json.Serialization;

namespace Madorin.AI.Aliyun.Video.Provider;

internal sealed record AliyunVideoRequest(string Model, AliyunVideoInput Input, AliyunVideoParameters Parameters);

internal sealed record AliyunVideoInput(string? Prompt, string? NegativePrompt, string? AudioUrl, AliyunMedia[]? Media);

internal sealed record AliyunMedia(string Type, string Url);

internal sealed record AliyunVideoParameters(string Resolution, string? Ratio, int Duration, bool PromptExtend, bool Watermark, int? Seed);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AliyunVideoRequest))]
[JsonSerializable(typeof(AliyunVideoInput))]
[JsonSerializable(typeof(AliyunVideoParameters))]
[JsonSerializable(typeof(AliyunMedia))]
[JsonSerializable(typeof(AliyunMedia[]))]
internal sealed partial class AliyunVideoJsonContext : JsonSerializerContext;
