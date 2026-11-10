using System.Text.Json.Nodes;

namespace Madorin.AI.Zhipu.Video.Requests;

internal sealed record VideoGenerationOptions(
    string RequestId,
    string Task,
    string Model,
    string? Prompt,
    string ApiKey,
    Uri Endpoint,
    string? OutputDirectory,
    int Duration,
    int Fps,
    string? Size,
    string? Quality,
    bool? WithAudio,
    bool? WatermarkEnabled,
    string? UserId,
    string? ImageKind,
    string? ImageValue,
    string? ImageBaseDirectory,
    bool NoWait,
    int TimeoutSeconds,
    bool TryRun,
    bool Verbose,
    JsonObject InputParameters)
{
    public string? RemoteImageUrl => ImageKind == "uri" ? ImageValue : null;
}
