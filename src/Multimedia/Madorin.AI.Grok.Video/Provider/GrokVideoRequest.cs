using System.Text.Json.Serialization;

namespace Madorin.AI.Grok.Video.Provider;

internal sealed record GrokVideoRequest(
    string Model,
    string Prompt,
    int Duration,
    string AspectRatio,
    string Resolution);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GrokVideoRequest))]
internal sealed partial class GrokVideoJsonContext : JsonSerializerContext;
