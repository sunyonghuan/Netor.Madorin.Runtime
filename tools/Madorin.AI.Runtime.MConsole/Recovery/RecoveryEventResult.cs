namespace Madorin.AI.Runtime.MConsole.Recovery;

public sealed record RecoveryEventResult(
    string RuntimeInstanceId,
    long InitialGsn,
    long LastConfirmedGsn,
    int ProjectedCount,
    int DuplicateCount,
    string? TerminalMessageType);
