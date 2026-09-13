namespace Madorin.AI.Runtime.Contracts;

public sealed record ToolCallDeltaEvent(
    string InvocationId,
    string CallId,
    string Name,
    string Delta);
