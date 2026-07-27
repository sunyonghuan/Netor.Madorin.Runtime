namespace Madorin.AI.Runtime.MConsole.Hosting;

public sealed record ServeHostSnapshot(
    int ProcessId,
    bool IsRunning,
    int? ExitCode,
    DateTimeOffset StartedAt,
    DateTimeOffset? ExitedAt,
    IReadOnlyList<string> StandardOutputTail,
    IReadOnlyList<string> StandardErrorTail);
