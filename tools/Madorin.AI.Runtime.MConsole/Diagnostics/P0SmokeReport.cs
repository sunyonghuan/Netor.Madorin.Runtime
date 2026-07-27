using Madorin.AI.Runtime.MConsole.Hosting;

namespace Madorin.AI.Runtime.MConsole.Diagnostics;

public sealed record P0SmokeReport(
    LocatedExecutable Executable,
    ExecutionResult Doctor,
    ServeHostSnapshot StartedServe,
    ServeHostSnapshot StoppedServe,
    bool SecretLeakDetected)
{
    public bool Passed =>
        Executable.ValidationResult is { ExitCode: 0, Envelope.Success: true }
        && StartedServe.IsRunning
        && !StoppedServe.IsRunning
        && !SecretLeakDetected;
}
