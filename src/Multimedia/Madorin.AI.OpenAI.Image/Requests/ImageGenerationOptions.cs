using System.Text.Json.Nodes;

namespace Madorin.AI.OpenAI.Image.Requests;

internal sealed record ImageGenerationOptions(
    string RequestId, string Task, string Model, string Prompt,
    string ApiKey, Uri Endpoint, string? OutputDirectory,
    string Size, string Quality, int ImageCount, string OutputFormat,
    int TimeoutSeconds, bool TryRun, bool Verbose,
    JsonObject InputParameters);
