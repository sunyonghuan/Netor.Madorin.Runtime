using System.Text.Json.Nodes;

namespace Madorin.AI.Grok.Video.Requests;

internal sealed record VideoGenerationOptions(
    string RequestId,
    string Task,
    string Model,
    string Prompt,
    string ApiKey,
    Uri Endpoint,
    string? OutputDirectory,
    int DurationSeconds,
    string AspectRatio,
    string Resolution,
    int TimeoutSeconds,
    bool TryRun,
    bool Verbose,
    bool NoWait,
    JsonObject InputParameters);
