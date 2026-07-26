namespace Madorin.AI.Runtime.Entities;

/// <summary>Represents the persisted query view of a Runtime Run.</summary>
public sealed record RunSnapshot(
    string RunId,
    string SessionId,
    RunStatus Status,
    string? TerminalText)
{
    public RuntimeMode? Mode { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public string? Phase { get; init; }

    public string? CurrentStep { get; init; }

    public long? CompletedUnits { get; init; }

    public long? TotalUnits { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public long? LastRunSequence { get; init; }

    public string? ErrorJson { get; init; }
}
