using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Contracts;

public sealed record RunQueryResult(
    string RunId,
    string SessionId,
    RunStatus Status,
    string? TerminalText)
{
    public RuntimeMode? Mode { get; init; }

    public string? Phase { get; init; }

    public string? CurrentStep { get; init; }

    public long? CompletedUnits { get; init; }

    public long? TotalUnits { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public long? LastRunSequence { get; init; }

    public RuntimeError? Error { get; init; }
}
