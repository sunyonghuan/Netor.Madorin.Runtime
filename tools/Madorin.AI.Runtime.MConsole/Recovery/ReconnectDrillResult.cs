namespace Madorin.AI.Runtime.MConsole.Recovery;

public sealed record RecoveryTimelineEntry(
    string Step,
    DateTimeOffset Timestamp,
    string Detail);

public sealed record ReconnectDrillResult(
    string RuntimeInstanceId,
    int? PreviousProcessId,
    int? CurrentProcessId,
    string? PreviousProtocolVersion,
    string? CurrentProtocolVersion,
    long LastConfirmedGsn,
    IReadOnlyList<RecoveryTimelineEntry> Timeline);
