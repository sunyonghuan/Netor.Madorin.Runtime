namespace Madorin.AI.Runtime.MConsole.Testing;

public sealed record ExecutionHistoryEntry(
    string Id,
    string Kind,
    string CommandOrHostAction,
    string[] Arguments,
    DateTimeOffset StartedAt,
    long DurationMilliseconds,
    int? ExitCode,
    int AssertionPassed,
    int AssertionFailed,
    string? Workspace,
    string? RuntimeInstanceId,
    string? RunId,
    string? DiagnosticId,
    string? DetailFile);

public sealed record ExecutionHistoryFilter(
    string? CommandPrefix = null,
    bool? Passed = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);
