namespace Madorin.AI.Runtime.Contracts;

public sealed record ReasoningDeltaEvent(
    string InvocationId,
    string Delta);
