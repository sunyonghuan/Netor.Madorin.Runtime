using System.Text.Json.Nodes;

namespace Madorin.AI.Kling.Video.Requests;

internal sealed record VideoGenerationOptions(
    string RequestId,
    string Task,
    string Model,
    string? Prompt,
    int? Duration,
    string Resolution,
    string? AspectRatio,
    bool? WatermarkEnabled,
    string? ImageKind,
    string? ImageValue,
    string? ImageBaseDirectory,
    string ApiKey,
    Uri Endpoint,
    string? OutputDirectory,
    bool NoWait,
    int TimeoutSeconds,
    bool TryRun,
    bool Verbose,
    JsonObject InputParameters)
{
    public string? RemoteImageUrl => ImageKind == "uri" ? ImageValue : null;
}
