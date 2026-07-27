using Madorin.AI.Runtime.Client;

namespace Madorin.AI.Runtime.MConsole.HostClient;

public sealed record ClientStateSnapshot(
    RuntimeClientState State,
    DateTimeOffset ObservedAt,
    string Reason,
    int? ProcessId = null,
    int? ExitCode = null,
    DateTimeOffset? ExitedAt = null,
    long LastConfirmedGsn = 0);
