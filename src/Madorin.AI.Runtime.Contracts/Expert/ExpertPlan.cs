namespace Madorin.AI.Runtime.Contracts.Expert;

/// <summary>Expert 模式轻量计划（持久化到文件）</summary>
public sealed record ExpertPlan(
    string PlanId,
    string SessionId,
    string Goal,
    ExpertPlanStatus Status,
    ExpertPlanStep[] Steps,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>计划中的一个步骤</summary>
public sealed record ExpertPlanStep(
    string StepId,
    string Title,
    string? Description,
    string? AgentName,
    ExpertStepStatus Status,
    int Progress,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Result,
    string? ErrorMessage);

/// <summary>创建计划的请求参数</summary>
public sealed record CreateExpertPlanRequest(
    string Goal,
    ExpertPlanStepDraft[] Steps);

/// <summary>步骤草稿（创建时使用）</summary>
public sealed record ExpertPlanStepDraft(
    string StepId,
    string Title,
    string? Description = null,
    string? AgentName = null);
