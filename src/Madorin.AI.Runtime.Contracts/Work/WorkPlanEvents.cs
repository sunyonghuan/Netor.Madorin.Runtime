namespace Madorin.AI.Runtime.Contracts.Work;

/// <summary>Work plan updated event payload.</summary>
public sealed record WorkPlanUpdatedEvent(
    int WorkPlanVersion,
    string Goal,
    WorkPlanStepSnapshot[] Steps);

/// <summary>Work step skipped event.</summary>
public sealed record WorkStepSkippedEvent(
    string StepId,
    string Reason);

/// <summary>Step snapshot in plan updated event.</summary>
public sealed record WorkPlanStepSnapshot(
    string StepId,
    string Title,
    string Goal,
    string TargetAgentId,
    string? ParentStepId,
    int Depth,
    string[] DependsOn,
    WorkStepStatus Status);

/// <summary>Work step status enumeration.</summary>
public enum WorkStepStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped,
    WaitingForApproval,
    WaitingForCredentials,
    Interrupted
}
