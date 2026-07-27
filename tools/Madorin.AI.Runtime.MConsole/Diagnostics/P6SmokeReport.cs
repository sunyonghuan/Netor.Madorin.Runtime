namespace Madorin.AI.Runtime.MConsole.Diagnostics;

public sealed record P6SmokeReport(
    int RunningInstanceCount,
    bool RuntimeBindingsIsolated,
    int? LockConflictExitCode,
    bool LockConflictNoDataChange,
    int? AliasConflictExitCode,
    bool ReadOnlyCommandsPassed,
    int MaximumObservedRunningRuns,
    int RejectedRunCount,
    bool RunLimitRespected,
    bool CrossInstanceCancelIsolated,
    bool ReportExported,
    bool OwnedProcessesStopped,
    bool FixturesCleaned)
{
    public bool Passed => RunningInstanceCount == 2
        && RuntimeBindingsIsolated
        && LockConflictExitCode == 4
        && LockConflictNoDataChange
        && AliasConflictExitCode == 4
        && ReadOnlyCommandsPassed
        && RunLimitRespected
        && CrossInstanceCancelIsolated
        && ReportExported
        && OwnedProcessesStopped
        && FixturesCleaned;
}
