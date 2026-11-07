namespace Madorin.AI.Runtime.Contracts.Expert;

/// <summary>计划已创建事件</summary>
public sealed record ExpertPlanCreatedEvent(
    string PlanId,
    string SessionId,
    string Goal,
    int StepCount);

/// <summary>计划已从持久化恢复事件</summary>
public sealed record ExpertPlanRestoredEvent(
    string PlanId,
    string Goal,
    int StepCount);

/// <summary>步骤状态更新事件</summary>
public sealed record ExpertStepStatusChangedEvent(
    string PlanId,
    string StepId,
    ExpertStepStatus OldStatus,
    ExpertStepStatus NewStatus,
    int Progress);

/// <summary>计划状态更新事件</summary>
public sealed record ExpertPlanStatusChangedEvent(
    string PlanId,
    ExpertPlanStatus OldStatus,
    ExpertPlanStatus NewStatus);
