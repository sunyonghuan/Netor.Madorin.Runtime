using System.Text.Json.Serialization;

namespace Madorin.AI.OpenAI.Video.Provider;

internal sealed record OpenAiVideoJob(string? Id, string? Model, string? Status, OpenAiVideoError? Error);
internal sealed record OpenAiVideoError(string? Code);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(OpenAiVideoJob))]
internal sealed partial class VideoJsonContext : JsonSerializerContext;
