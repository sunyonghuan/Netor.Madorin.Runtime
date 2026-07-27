using Madorin.AI.Runtime.MConsole.HostClient;

namespace Madorin.AI.Runtime.MConsole.Diagnostics;

public sealed record P1SmokeReport(
    HostBindingSnapshot AttachExisting,
    HostBindingSnapshot StartIfMissingAttach,
    HostBindingSnapshot StartIfMissingStart,
    HostBindingSnapshot AlwaysStart,
    bool OwnedProcessesStopped)
{
    public bool Passed =>
        AttachExisting.ActualStartBranch == "attach"
        && !AttachExisting.OwnsProcess
        && StartIfMissingAttach.ActualStartBranch == "attach"
        && !StartIfMissingAttach.OwnsProcess
        && StartIfMissingStart.ActualStartBranch == "start"
        && StartIfMissingStart.OwnsProcess
        && AlwaysStart.ActualStartBranch == "start"
        && AlwaysStart.OwnsProcess
        && OwnedProcessesStopped;
}
