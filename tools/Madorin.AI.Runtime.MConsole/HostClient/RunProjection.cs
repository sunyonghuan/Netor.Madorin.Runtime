using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.MConsole.HostClient;

public sealed record RunProjection(
    string RuntimeInstanceId,
    string RunId,
    RuntimeMode? Mode,
    string? Phase,
    string? CurrentStep,
    long CompletedUnits,
    long? TotalUnits,
    DateTimeOffset UpdatedAt,
    long LastRunSequence,
    string? TerminalMessageType,
    string? Error);
