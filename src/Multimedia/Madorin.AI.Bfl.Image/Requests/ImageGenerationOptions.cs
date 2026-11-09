using System.Text.Json.Nodes;

namespace Madorin.AI.Bfl.Image.Requests;

internal sealed record ImageGenerationOptions(
    string RequestId, string Task, string Model, string Prompt,
    string ApiKey, Uri Endpoint, string? OutputDirectory,
    bool DisablePromptUpsampling, long? Seed, int? Width, int? Height,
    int SafetyTolerance, string ImageFormat, int TimeoutSeconds, bool TryRun, bool Verbose,
    JsonObject InputParameters);
