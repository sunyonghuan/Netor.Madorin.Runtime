using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Modes.Work;

/// <summary>项目组长实现：循环执行、验证、汇总。</summary>
public sealed class WorkSupervisor : IWorkSupervisor
{
    private readonly Func<WorkPlanSubStepDraft, CancellationToken, Task<string>> _workerDispatcher;
    private readonly Func<string, CancellationToken, Task<string>> _supervisorInvoker;
    private readonly WorkflowPolicy _policy;

    public WorkSupervisor(
        Func<WorkPlanSubStepDraft, CancellationToken, Task<string>> workerDispatcher,
        Func<string, CancellationToken, Task<string>> supervisorInvoker,
        WorkflowPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(workerDispatcher);
        ArgumentNullException.ThrowIfNull(supervisorInvoker);
        ArgumentNullException.ThrowIfNull(policy);

        _workerDispatcher = workerDispatcher;
        _supervisorInvoker = supervisorInvoker;
        _policy = policy;
    }

    public async Task<WorkStepExecutionResult> ExecuteStepAsync(
        WorkPlanStepDraft stepDraft,
        WorkPlanDraft plan,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stepDraft);
        ArgumentNullException.ThrowIfNull(plan);

        var subStepResults = new List<WorkSubStepResult>();

        // 按顺序执行二级步骤
        foreach (var subStep in stepDraft.SubSteps)
        {
            var retryCount = 0;

            while (retryCount <= _policy.MaxRetriesPerStep)
            {
                // 1. 派发 Worker Agent
                var output = await _workerDispatcher(subStep, ct).ConfigureAwait(false);

                // 2. 验证结果
                var verification = await VerifySubStepAsync(
                    subStep,
                    output,
                    context: BuildVerificationContext(stepDraft, subStepResults),
                    ct).ConfigureAwait(false);

                if (verification.Passed)
                {
                    subStepResults.Add(new WorkSubStepResult(
                        subStep.SubStepId,
                        Success: true,
                        Output: output));
                    break;
                }

                // 验证失败
                retryCount++;
                if (retryCount > _policy.MaxRetriesPerStep)
                {
                    subStepResults.Add(new WorkSubStepResult(
                        subStep.SubStepId,
                        Success: false,
                        Output: output,
                        ErrorMessage: verification.Reason));

                    // 根据失败策略决定是否继续
                    if (_policy.FailurePolicy == WorkStepFailurePolicy.Stop)
                    {
                        return new WorkStepExecutionResult(
                            Success: false,
                            Summary: $"SubStep '{subStep.SubStepId}' failed after {_policy.MaxRetriesPerStep} retries.",
                            SubStepResults: subStepResults.ToArray());
                    }

                    break;
                }

                // 重试前等待
                await Task.Delay(
                    ComputeRetryDelay(_policy, retryCount),
                    ct).ConfigureAwait(false);
            }
        }

        // 3. 汇总一级步骤结果
        var summary = await SummarizeStepResultsAsync(stepDraft, subStepResults.ToArray(), ct)
            .ConfigureAwait(false);

        return new WorkStepExecutionResult(
            Success: subStepResults.All(r => r.Success),
            Summary: summary,
            SubStepResults: subStepResults.ToArray());
    }

    public async Task<WorkStepVerificationResult> VerifySubStepAsync(
        WorkPlanSubStepDraft subStepDraft,
        string actualOutput,
        string context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subStepDraft);

        if (string.IsNullOrWhiteSpace(subStepDraft.ExpectedOutput))
        {
            return new WorkStepVerificationResult(
                Passed: !string.IsNullOrWhiteSpace(actualOutput),
                Reason: string.IsNullOrWhiteSpace(actualOutput)
                    ? "输出为空，但未指定期望产出，无法判断是否合格"
                    : "有输出且未指定期望产出，默认通过",
                Retryable: true);
        }

        var prompt = $$"""
            你是项目组长，现在要验证二级步骤的执行结果是否符合预期。

            步骤目标：{{subStepDraft.Goal}}
            期望产出：{{subStepDraft.ExpectedOutput}}
            实际产出：{{actualOutput}}
            上下文：{{context}}

            请判断：
            1. 实际产出是否达到步骤目标？
            2. 实际产出是否符合期望产出的要求？
            3. 实际产出是否与上下文一致？
            4. 如果不符合，是否可以重试？

            返回 JSON：
            {
              "passed": true/false,
              "reason": "通过原因 或 失败原因",
              "retryable": true/false
            }
            """;

        var verificationJson = await _supervisorInvoker(prompt, ct).ConfigureAwait(false);

        try
        {
            using var doc = JsonDocument.Parse(verificationJson);
            var root = doc.RootElement;

            return new WorkStepVerificationResult(
                Passed: root.GetProperty("passed").GetBoolean(),
                Reason: root.GetProperty("reason").GetString() ?? "未提供原因",
                Retryable: root.TryGetProperty("retryable", out var retryable)
                    ? retryable.GetBoolean()
                    : true);
        }
        catch (JsonException)
        {
            return new WorkStepVerificationResult(
                Passed: false,
                Reason: "验证器返回格式错误",
                Retryable: false);
        }
    }

    public async Task<string> SummarizeStepResultsAsync(
        WorkPlanStepDraft stepDraft,
        WorkSubStepResult[] subStepResults,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stepDraft);
        ArgumentNullException.ThrowIfNull(subStepResults);

        var prompt = $"""
            你是项目组长，现在要汇总一级步骤的所有二级步骤结果。

            一级步骤：{stepDraft.Title}
            描述：{stepDraft.Description}

            二级步骤结果：
            {string.Join("\n", subStepResults.Select(r =>
                $"- {r.SubStepId}: {(r.Success ? "成功" : "失败")} - {r.Output}"))}

            请生成汇总报告，说明：
            1. 这个一级步骤的整体完成情况
            2. 各二级步骤的关键产出
            3. 是否达到一级步骤的目标
            """;

        return await _supervisorInvoker(prompt, ct).ConfigureAwait(false);
    }

    private static string BuildVerificationContext(
        WorkPlanStepDraft stepDraft,
        List<WorkSubStepResult> completedSubSteps)
    {
        if (completedSubSteps.Count == 0)
        {
            return $"一级步骤：{stepDraft.Title}\n尚未完成任何二级步骤。";
        }

        return $"一级步骤：{stepDraft.Title}\n" +
               $"已完成的二级步骤：\n" +
               string.Join("\n", completedSubSteps.Select(r =>
                   $"- {r.SubStepId}: {r.Output}"));
    }

    private static TimeSpan ComputeRetryDelay(WorkflowPolicy policy, int attemptCount)
    {
        if (policy.RetryBackoffMilliseconds <= 0)
        {
            return TimeSpan.Zero;
        }

        var exponent = Math.Clamp(attemptCount, 0, 6);
        var milliseconds = Math.Min(
            60_000L,
            (long)policy.RetryBackoffMilliseconds * (1L << exponent));
        return TimeSpan.FromMilliseconds(milliseconds);
    }
}

