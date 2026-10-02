using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Modes.Work;

/// <summary>项目组长：负责执行计划、验证结果、汇总上报。</summary>
public interface IWorkSupervisor
{
    /// <summary>执行一个一级步骤（包含所有二级步骤）。</summary>
    Task<WorkStepExecutionResult> ExecuteStepAsync(
        WorkPlanStepDraft stepDraft,
        WorkPlanDraft plan,
        CancellationToken ct = default);

    /// <summary>验证二级步骤执行结果是否符合预期。</summary>
    Task<WorkStepVerificationResult> VerifySubStepAsync(
        WorkPlanSubStepDraft subStepDraft,
        string actualOutput,
        string context,
        CancellationToken ct = default);

    /// <summary>汇总一级步骤的所有二级步骤结果。</summary>
    Task<string> SummarizeStepResultsAsync(
        WorkPlanStepDraft stepDraft,
        WorkSubStepResult[] subStepResults,
        CancellationToken ct = default);
}

/// <summary>一级步骤执行结果。</summary>
public sealed record WorkStepExecutionResult(
    bool Success,
    string Summary,
    WorkSubStepResult[] SubStepResults);

/// <summary>二级步骤执行结果。</summary>
public sealed record WorkSubStepResult(
    string SubStepId,
    bool Success,
    string Output,
    string? ErrorMessage = null);

/// <summary>二级步骤验证结果。</summary>
public sealed record WorkStepVerificationResult(
    bool Passed,
    string Reason,
    bool Retryable = true);
