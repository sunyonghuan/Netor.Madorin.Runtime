namespace Madorin.AI.Runtime.MConsole.Diagnostics;

public sealed record P5SmokeReport(
    int AssertionPassed,
    int AssertionFailed,
    int HistoryCount,
    int BaselineDifferenceCount,
    bool ReplayPassed,
    bool ReportExported,
    bool MonitorCaptured,
    bool FixtureCleaned)
{
    public bool Passed =>
        AssertionPassed >= 4
        && AssertionFailed == 0
        && HistoryCount >= 1
        && BaselineDifferenceCount == 0
        && ReplayPassed
        && ReportExported
        && MonitorCaptured
        && FixtureCleaned;
}
