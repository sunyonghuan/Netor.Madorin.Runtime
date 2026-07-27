namespace Madorin.AI.Runtime.MConsole.HostClient;

public sealed record HostBindingSnapshot(
    string HostInstanceId,
    string? RuntimeInstanceId,
    string? WorkspaceDirectory,
    int? ServerProcessId,
    string? ProtocolVersion,
    string? ProgramVersion,
    string? Capabilities,
    bool OwnsProcess,
    string ActualStartBranch);
