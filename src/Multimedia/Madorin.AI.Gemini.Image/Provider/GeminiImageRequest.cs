using System.Text.Json.Serialization;

namespace Madorin.AI.Gemini.Image.Provider;

internal sealed record GeminiImageRequest(
    GeminiContent[] Contents,
    GeminiGenerationConfig GenerationConfig);

internal sealed record GeminiContent(GeminiPart[] Parts);

internal sealed record GeminiPart(string Text);

internal sealed record GeminiGenerationConfig(
    string[] ResponseModalities,
    int CandidateCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GeminiImageConfig? ImageConfig);

internal sealed record GeminiImageConfig(string AspectRatio);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GeminiImageRequest))]
internal sealed partial class GeminiJsonContext : JsonSerializerContext;
