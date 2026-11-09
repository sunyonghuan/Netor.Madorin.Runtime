using System.Text.Json.Serialization;

namespace Madorin.AI.Bfl.Image.Provider;

internal sealed record BflImageRequest(
    string Prompt,
    bool DisablePup,
    long? Seed,
    int? Width,
    int? Height,
    int SafetyTolerance,
    string OutputFormat);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(BflImageRequest))]
internal sealed partial class BflJsonContext : JsonSerializerContext;
