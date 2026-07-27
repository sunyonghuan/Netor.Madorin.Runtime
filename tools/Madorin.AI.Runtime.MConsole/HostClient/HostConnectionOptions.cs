using Madorin.AI.Runtime.Client;

namespace Madorin.AI.Runtime.MConsole.HostClient;

public sealed record HostConnectionOptions(
    string HostInstanceId,
    string WorkspaceDirectory,
    string RuntimeExecutablePath,
    RuntimeStartPolicy StartPolicy = RuntimeStartPolicy.StartIfMissing,
    OwnedProcessClosePolicy OwnedProcessClosePolicy = OwnedProcessClosePolicy.Shutdown,
    string? RuntimeInstanceId = null,
    string PipePrefix = "madorin.ai.runtime",
    string? PipeName = null,
    string? DataDirectory = null,
    string? LogDirectory = null,
    string? HandshakeSecret = null,
    string? ExpectedRuntimeVersion = null,
    string? ExpectedRuntimeInstanceId = null,
    int? ExpectedServerProcessId = null,
    TimeSpan StartupTimeout = default,
    TimeSpan ReconnectWindow = default,
    TimeSpan ReconnectDelay = default)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(HostInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(WorkspaceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(RuntimeExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(PipePrefix);

        if (!Directory.Exists(WorkspaceDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Host workspace '{WorkspaceDirectory}' does not exist.");
        }

        if (StartPolicy == RuntimeStartPolicy.AttachExisting
            && string.IsNullOrWhiteSpace(HandshakeSecret))
        {
            throw new ArgumentException(
                "AttachExisting requires the target Runtime handshake secret.",
                nameof(HandshakeSecret));
        }

        if (StartPolicy == RuntimeStartPolicy.AttachExisting
            && string.IsNullOrWhiteSpace(PipeName)
            && string.IsNullOrWhiteSpace(RuntimeInstanceId))
        {
            throw new ArgumentException(
                "AttachExisting requires PipeName or RuntimeInstanceId.",
                nameof(PipeName));
        }
    }
}
