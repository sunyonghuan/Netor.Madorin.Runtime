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
    string? ErrorMessage,
    string? ExecutionPolicy = null,
    string[]? ParallelTasks = null,
    string CompletionMode = "tools",
    string? ExpectedOutput = null,
    ExpertToolEvidence[]? Evidence = null);

/// <summary>创建计划的请求参数</summary>
public sealed record CreateExpertPlanRequest(
    string Goal,
    ExpertPlanStepDraft[] Steps);

/// <summary>步骤草稿（创建时使用）</summary>
public sealed record ExpertPlanStepDraft(
    string StepId,
    string Title,
    string? Description = null,
    string? AgentName = null,
    string? ExecutionPolicy = null,
    string[]? ParallelTasks = null,
    string CompletionMode = "tools",
    string? ExpectedOutput = null);

/// <summary>执行阶段的工具调用证据。</summary>
public sealed record ExpertToolEvidence(
    string AgentId,
    string CallId,
    string ToolId,
    bool Success,
    DateTimeOffset ObservedAt);
