using System.Text.Json.Serialization;

namespace Madorin.AI.Aliyun.Image.Provider;

internal sealed record AliyunImageRequest(string Model, AliyunInput Input, AliyunParameters? Parameters);
internal sealed record AliyunInput(AliyunMessage[] Messages);
internal sealed record AliyunMessage(string Role, AliyunMessagePart[] Content);
internal sealed record AliyunMessagePart(string Text);
internal sealed record AliyunParameters(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NegativePrompt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Size,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? N,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? PromptExtend,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Watermark,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Seed);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(AliyunImageRequest))]
internal sealed partial class AliyunJsonContext : JsonSerializerContext;
