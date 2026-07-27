using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.MConsole.Diagnostics;

public sealed record P2SmokeReport(
    ToolAuthorizationDecision PermissionDecision,
    ToolAuthorizationDecision DangerousApprovalDecision,
    int EchoExecutionCount,
    ToolCallStatus FirstCallStatus,
    ToolCallStatus ReplayedCallStatus,
    ToolCallStatus QueryStatus,
    bool CancellationAccepted,
    ToolCallStatus CancelledCallStatus,
    int HistoryCount)
{
    public bool Passed =>
        PermissionDecision is ToolAuthorizationDecision.Granted
        && DangerousApprovalDecision is ToolAuthorizationDecision.Denied
        && EchoExecutionCount == 1
        && FirstCallStatus is ToolCallStatus.Succeeded
        && ReplayedCallStatus is ToolCallStatus.Succeeded
        && QueryStatus is ToolCallStatus.Succeeded
        && CancellationAccepted
        && CancelledCallStatus is ToolCallStatus.Cancelled
        && HistoryCount >= 7;
}
