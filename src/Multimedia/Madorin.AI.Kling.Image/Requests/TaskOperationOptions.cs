namespace Madorin.AI.Kling.Image.Requests;

internal sealed record TaskOperationOptions(
    string RequestId,
    string TaskId,
    string ApiKey,
    Uri Endpoint,
    string? OutputDirectory,
    bool Wait,
    int TimeoutSeconds,
    string Model,
    int Count);
