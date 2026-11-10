using System.Text.Json.Nodes;

namespace Madorin.AI.Zhipu.Image.Requests;

internal sealed record ImageGenerationOptions(
    string RequestId,
    string Task,
    string Model,
    string Prompt,
    string ApiKey,
    Uri Endpoint,
    string? OutputDirectory,
    string Size,
    string Quality,
    bool? WatermarkEnabled,
    string? UserId,
    int TimeoutSeconds,
    bool TryRun,
    bool Verbose,
    JsonObject InputParameters);
