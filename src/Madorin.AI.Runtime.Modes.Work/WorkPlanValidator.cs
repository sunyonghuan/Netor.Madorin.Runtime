using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;

namespace Madorin.AI.Runtime.Modes.Work;

/// <summary>Validates structured work plans before any step enters Running.</summary>
public static class WorkPlanValidator
{
    public static WorkPlanDraft ValidateOrThrow(
        WorkPlanDraft plan,
        WorkModeOptions options)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        var policy = options.EffectiveWorkflowPolicy;
        policy.Validate();

        if (string.IsNullOrWhiteSpace(plan.PlanVersion))
        {
            throw new ArgumentException("PlanVersion is required.", nameof(plan));
        }

        if (string.IsNullOrWhiteSpace(plan.Goal))
        {
            throw new ArgumentException("Plan goal is required.", nameof(plan));
        }

        ArgumentNullException.ThrowIfNull(plan.Steps);
        if (plan.Steps.Length == 0)
        {
            throw new ArgumentException("Work plan must contain at least one step.", nameof(plan));
        }

        if (plan.Steps.Length > policy.MaxStepsPerRun)
        {
            throw new ArgumentException(
                $"Plan has {plan.Steps.Length} steps which exceeds MaxStepsPerRun={policy.MaxStepsPerRun}.",
                nameof(plan));
        }

        if (plan.Steps.Length > policy.HardMaxStepsPerRun)
        {
            throw new ArgumentException(
                $"Plan exceeds HardMaxStepsPerRun={policy.HardMaxStepsPerRun}.",
                nameof(plan));
        }

        var agentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var agent in options.AvailableAgents)
        {
            agentIds.Add(agent.AgentId);
        }

        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        var deps = new Dictionary<string, string[]>(StringComparer.Ordinal);

        for (var index = 0; index < plan.Steps.Length; index++)
        {
            var step = plan.Steps[index]
                ?? throw new ArgumentException($"Step at index {index} is null.", nameof(plan));
            if (string.IsNullOrWhiteSpace(step.StepId))
            {
                throw new ArgumentException($"Step at index {index} is missing stepId.", nameof(plan));
            }

            if (!stepIds.Add(step.StepId))
            {
                throw new ArgumentException($"Duplicate stepId '{step.StepId}'.", nameof(plan));
            }

            if (string.IsNullOrWhiteSpace(step.Title))
            {
                throw new ArgumentException($"Step '{step.StepId}' is missing a title.", nameof(plan));
            }

            if (string.IsNullOrWhiteSpace(step.Description))
            {
                throw new ArgumentException($"Step '{step.StepId}' is missing a description.", nameof(plan));
            }

            // Validate SubSteps
            if (step.SubSteps is null or { Length: 0 })
            {
                throw new ArgumentException($"Step '{step.StepId}' has no sub-steps. Each top-level step must contain at least one sub-step.", nameof(plan));
            }

            var subStepIds = new HashSet<string>(StringComparer.Ordinal);
            for (var subIndex = 0; subIndex < step.SubSteps.Length; subIndex++)
            {
                var subStep = step.SubSteps[subIndex]
                    ?? throw new ArgumentException($"Step '{step.StepId}' has null sub-step at index {subIndex}.", nameof(plan));

                if (string.IsNullOrWhiteSpace(subStep.SubStepId))
                {
                    throw new ArgumentException($"Step '{step.StepId}' sub-step at index {subIndex} is missing subStepId.", nameof(plan));
                }

                if (!subStepIds.Add(subStep.SubStepId))
                {
                    throw new ArgumentException($"Step '{step.StepId}' has duplicate subStepId '{subStep.SubStepId}'.", nameof(plan));
                }

                if (string.IsNullOrWhiteSpace(subStep.Goal))
                {
                    throw new ArgumentException($"SubStep '{subStep.SubStepId}' is missing a goal.", nameof(plan));
                }

                if (string.IsNullOrWhiteSpace(subStep.Instruction))
                {
                    throw new ArgumentException($"SubStep '{subStep.SubStepId}' is missing an instruction.", nameof(plan));
                }

                if (string.IsNullOrWhiteSpace(subStep.TargetAgentId)
                    || !agentIds.Contains(subStep.TargetAgentId))
                {
                    throw new ArgumentException(
                        $"SubStep '{subStep.SubStepId}' targets unknown agent '{subStep.TargetAgentId}'.",
                        nameof(plan));
                }
            }

            deps[step.StepId] = step.DependsOn ?? [];
        }

        foreach (var step in plan.Steps)
        {
            foreach (var dependency in step.DependsOn ?? [])
            {
                if (string.IsNullOrWhiteSpace(dependency) || !stepIds.Contains(dependency))
                {
                    throw new ArgumentException(
                        $"Step '{step.StepId}' depends on unknown step '{dependency}'.",
                        nameof(plan));
                }

                if (string.Equals(dependency, step.StepId, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Step '{step.StepId}' cannot depend on itself.",
                        nameof(plan));
                }
            }
        }

        DetectCycle(deps);
        EnsureReferencedDependencies(plan);
        return plan;
    }

    /// <summary>
    /// 后一步写明要使用前一步结果时，必须声明 dependsOn，避免并行开跑。
    /// </summary>
    private static void EnsureReferencedDependencies(WorkPlanDraft plan)
    {
        for (var index = 0; index < plan.Steps.Length; index++)
        {
            var step = plan.Steps[index];
            var dependsOn = new HashSet<string>(step.DependsOn ?? [], StringComparer.Ordinal);
            var text = string.Concat(step.Title, "\n", step.Description);
            for (var priorIndex = 0; priorIndex < index; priorIndex++)
            {
                var prior = plan.Steps[priorIndex];
                if (!ReferencesPriorStep(text, prior.StepId, priorIndex))
                {
                    continue;
                }

                if (!dependsOn.Contains(prior.StepId))
                {
                    throw new ArgumentException(
                        $"Step '{step.StepId}' uses the result of '{prior.StepId}' but does not depend on it.",
                        nameof(plan));
                }
            }
        }
    }

    private static bool ReferencesPriorStep(string text, string priorStepId, int priorIndex)
    {
        if (text.Contains(priorStepId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var number = (priorIndex + 1).ToString(CultureInfo.InvariantCulture);
        return text.Contains("步骤" + number, StringComparison.Ordinal)
            || text.Contains("步骤 " + number, StringComparison.Ordinal);
    }

    public static WorkPlanDraft CreateFallbackPlan(
        string goal,
        WorkModeOptions options,
        string planVersion = "1",
        string stepId = "step-1")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        if (options.AvailableAgents.Length == 0)
        {
            throw new ArgumentException("At least one available agent is required.", nameof(options));
        }

        var agent = options.AvailableAgents[0];
        return new WorkPlanDraft(
            planVersion,
            goal,
            [
                new WorkPlanStepDraft(
                    stepId,
                    "Execute Task",
                    goal,
                    [
                        new WorkPlanSubStepDraft(
                            $"{stepId}.1",
                            goal,
                            goal,
                            agent.AgentId)
                    ],
                    DependsOn: [],
                    IsBackground: false)
            ]);
    }

    /// <summary>
    /// Aligns GM-produced plan versions with already-published revisions.
    /// First publish is forced to version "1"; later publishes auto-increment
    /// and set <see cref="WorkPlanDraft.PreviousPlanVersion"/> when the model omits them.
    /// </summary>
    public static WorkPlanDraft NormalizePlanRevision(
        WorkPlanDraft plan,
        string? latestPublishedPlanVersion)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (string.IsNullOrWhiteSpace(latestPublishedPlanVersion))
        {
            if (string.Equals(plan.PlanVersion, "1", StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(plan.PreviousPlanVersion))
            {
                return plan;
            }

            return plan with
            {
                PlanVersion = "1",
                PreviousPlanVersion = null
            };
        }

        if (!int.TryParse(
                latestPublishedPlanVersion,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var current))
        {
            throw new ArgumentException(
                $"Published plan version '{latestPublishedPlanVersion}' must be a non-negative integer.",
                nameof(latestPublishedPlanVersion));
        }

        var expectedNext = (current + 1).ToString(CultureInfo.InvariantCulture);
        if (string.Equals(plan.PlanVersion, expectedNext, StringComparison.Ordinal)
            && string.Equals(
                plan.PreviousPlanVersion,
                latestPublishedPlanVersion,
                StringComparison.Ordinal))
        {
            return plan;
        }

        return plan with
        {
            PlanVersion = expectedNext,
            PreviousPlanVersion = latestPublishedPlanVersion
        };
    }

    public static string ComputeStepInputHash(
        string stepId,
        string planVersion,
        string serializedStepInputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(planVersion);
        ArgumentNullException.ThrowIfNull(serializedStepInputs);
        var material = $"{stepId}\n{planVersion}\n{serializedStepInputs}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

    public static string SerializeStepInputs(WorkPlanStepDraft step)
    {
        ArgumentNullException.ThrowIfNull(step);

        var parts = new List<string>
        {
            step.Title,
            step.Description,
            step.StepId
        };

        foreach (var subStep in step.SubSteps)
        {
            parts.Add(subStep.SubStepId);
            parts.Add(subStep.Goal);
            parts.Add(subStep.Instruction);
            parts.Add(subStep.TargetAgentId);
            if (subStep.Inputs is { } inputs && inputs.ValueKind is not JsonValueKind.Undefined
                and not JsonValueKind.Null)
            {
                parts.Add(inputs.GetRawText());
            }
        }

        return string.Join("\n", parts);
    }

    private static void DetectCycle(Dictionary<string, string[]> deps)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in deps.Keys)
        {
            Visit(node);
        }

        void Visit(string node)
        {
            if (visited.Contains(node))
            {
                return;
            }

            if (!visiting.Add(node))
            {
                throw new ArgumentException($"Work plan contains a dependency cycle at '{node}'.");
            }

            foreach (var next in deps[node])
            {
                Visit(next);
            }

            visiting.Remove(node);
            visited.Add(node);
        }
    }
}
