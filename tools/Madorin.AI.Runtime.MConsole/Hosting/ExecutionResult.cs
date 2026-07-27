namespace Madorin.AI.Runtime.MConsole.Hosting;

public sealed record ExecutionResult(
    string Command,
    IReadOnlyList<string> Arguments,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    JsonEnvelope? Envelope,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    bool TimedOut,
    bool Canceled)
{
    public bool IsSuccess => !TimedOut && !Canceled && ExitCode == 0;
}
