namespace Madorin.AI.Runtime.MConsole.HostClient;

public sealed record RuntimeCursorState(
    string RuntimeInstanceId,
    long LastConfirmedGsn,
    DateTimeOffset UpdatedAt);
