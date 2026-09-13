namespace Madorin.AI.Runtime.Contracts;

public sealed record InvocationCompletedEvent(
    string InvocationId,
    string? WorkStepId = null);
