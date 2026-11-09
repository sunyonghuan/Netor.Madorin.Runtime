using System.Text.Json.Nodes;

namespace Madorin.AI.Aliyun.Image.Requests;

internal sealed record ImageGenerationOptions(
    string RequestId, string Task, string Model, string Prompt,
    string ApiKey, Uri Endpoint, string? OutputDirectory,
    string AspectRatio, string Quality, int ImageCount, string OutputFormat,
    string? NegativePrompt, string? Size, bool? PromptExtend, bool? Watermark, int? Seed,
    int TimeoutSeconds, bool TryRun, bool Verbose, JsonObject InputParameters);
