namespace Madorin.AI.Runtime.MConsole.Hosting;

public sealed record ServeHostOptions(
    string ExecutablePath,
    string WorkspaceDirectory,
    string? InstanceId = null,
    string? PipePrefix = null,
    string? DataDirectory = null,
    string? LogDirectory = null,
    int? MaxRuns = null,
    TimeSpan StartupTimeout = default,
    TimeSpan ShutdownTimeout = default,
    int RetainedLogLines = 200)
{
    public TimeSpan ResolvedStartupTimeout => StartupTimeout == default
        ? TimeSpan.FromSeconds(30)
        : StartupTimeout;

    public TimeSpan ResolvedShutdownTimeout => ShutdownTimeout == default
        ? TimeSpan.FromSeconds(5)
        : ShutdownTimeout;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(WorkspaceDirectory);
        if (!Directory.Exists(WorkspaceDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Serve workspace '{WorkspaceDirectory}' does not exist.");
        }

        if (MaxRuns is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRuns), "MaxRuns must be positive.");
        }

        if (RetainedLogLines is < 10 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RetainedLogLines),
                "RetainedLogLines must be between 10 and 10000.");
        }
    }
}
