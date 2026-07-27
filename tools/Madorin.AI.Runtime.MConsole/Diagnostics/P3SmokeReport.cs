using Madorin.AI.Runtime.MConsole.Recovery;

namespace Madorin.AI.Runtime.MConsole.Diagnostics;

public sealed record P3SmokeReport(
    ReconnectDrillResult Reconnect,
    int PersistedRecoveryCount,
    bool RuntimeIdentityPreserved,
    bool CursorPreserved,
    bool OwnedProcessStopped)
{
    public bool Passed =>
        RuntimeIdentityPreserved
        && CursorPreserved
        && PersistedRecoveryCount == 1
        && Reconnect.Timeline.Count >= 3
        && OwnedProcessStopped;
}
