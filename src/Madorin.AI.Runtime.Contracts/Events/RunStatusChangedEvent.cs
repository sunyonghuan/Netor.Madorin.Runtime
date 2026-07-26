using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Contracts;

public sealed record RunStatusChangedEvent(
    string RunId,
    string Status)
{
    public RuntimeMode? Mode { get; init; }

    public string? Phase { get; init; }

    public string? CurrentStep { get; init; }

    public long? CompletedUnits { get; init; }

    public long? TotalUnits { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public RuntimeError? Error { get; init; }
}
