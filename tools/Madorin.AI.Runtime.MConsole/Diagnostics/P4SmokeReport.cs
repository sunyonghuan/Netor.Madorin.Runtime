namespace Madorin.AI.Runtime.MConsole.Diagnostics;

public sealed record P4CommandSmokeResult(string CommandId, int ExitCode);

public sealed record P4SmokeReport(
    int GroupCount,
    int CommandCount,
    IReadOnlyList<P4CommandSmokeResult> Commands,
    bool RunConflictBlocked,
    bool SessionDeleteBlocked,
    bool StorageConflictBlocked)
{
    public bool Passed =>
        GroupCount == 8
        && CommandCount >= 41
        && Commands.Count == 9
        && Commands.All(static item => item.ExitCode is not -1 and not 2)
        && RunConflictBlocked
        && SessionDeleteBlocked
        && StorageConflictBlocked;
}
