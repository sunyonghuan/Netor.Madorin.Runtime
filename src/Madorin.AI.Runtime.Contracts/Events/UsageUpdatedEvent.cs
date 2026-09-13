namespace Madorin.AI.Runtime.Contracts;

public sealed record UsageUpdatedEvent(
    string InvocationId,
    int? InputTokens,
    int? OutputTokens);
