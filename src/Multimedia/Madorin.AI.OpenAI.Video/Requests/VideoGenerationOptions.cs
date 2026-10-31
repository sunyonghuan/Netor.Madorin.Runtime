using System.Text.Json.Nodes;

namespace Madorin.AI.OpenAI.Video.Requests;

internal sealed record VideoGenerationOptions(string RequestId, string Task, string Model, string Prompt,
    string ApiKey, Uri Endpoint, string? OutputDirectory, string Size, int Duration, int Timeout,
    bool TryRun, bool Verbose, bool NoWait, string? ReferencePath, JsonObject InputParameters);
