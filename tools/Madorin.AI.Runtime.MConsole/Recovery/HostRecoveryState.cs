namespace Madorin.AI.Runtime.MConsole.Recovery;

public sealed record HostRecoveryState(
    string RuntimeInstanceId,
    string HostInstanceId,
    string Workspace,
    int? RuntimeProcessId,
    string? ProtocolVersion,
    long LastConfirmedGsn,
    string? ActiveRunId,
    string? ActiveSessionId,
    DateTimeOffset UpdatedAt);
