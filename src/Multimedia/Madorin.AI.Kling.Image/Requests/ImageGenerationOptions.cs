using System.Text.Json.Nodes;

namespace Madorin.AI.Kling.Image.Requests;

internal sealed record ImageGenerationOptions(
    string RequestId,
    string Task,
    string Model,
    string Prompt,
    string? NegativePrompt,
    string Resolution,
    int Count,
    string AspectRatio,
    bool? WatermarkEnabled,
    string ApiKey,
    Uri Endpoint,
    string? OutputDirectory,
    bool NoWait,
    int TimeoutSeconds,
    bool TryRun,
    bool Verbose,
    JsonObject InputParameters);
