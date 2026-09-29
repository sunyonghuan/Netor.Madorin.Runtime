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

        var parentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in plan.Steps)
        {
            if (!string.IsNullOrWhiteSpace(step?.ParentStepId))
            {
                parentIds.Add(step.ParentStepId);
            }
        }

        var blockedExecutors = new HashSet<string>(StringComparer.Ordinal)
        {
            "general-manager",
            "project-lead",
            options.GeneralManager.AgentId,
        };

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

            if (string.IsNullOrWhiteSpace(step.Goal))
            {
                throw new ArgumentException($"Step '{step.StepId}' is missing a goal.", nameof(plan));
            }

            if (!parentIds.Contains(step.StepId))
            {
                EnsureLeafExecutor(plan, index, step, agentIds, blockedExecutors);
                step = plan.Steps[index];
            }

            if (step.Depth < 0 || step.Depth > policy.MaxAgentDepth)
            {
                throw new ArgumentException(
                    $"Step '{step.StepId}' depth {step.Depth} exceeds MaxAgentDepth={policy.MaxAgentDepth}.",
                    nameof(plan));
            }

            if (step.Depth > policy.HardMaxAgentDepth)
            {
                throw new ArgumentException(
                    $"Step '{step.StepId}' depth exceeds HardMaxAgentDepth.",
                    nameof(plan));
            }

            if (string.Equals(step.ParentStepId, step.StepId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Step '{step.StepId}' cannot be its own parent.",
                    nameof(plan));
            }

            if (step.ParentStepId is { Length: > 0 }
                && !string.Equals(step.ParentStepId, step.StepId, StringComparison.Ordinal)
                && !stepIds.Contains(step.ParentStepId)
                && !(step.DependsOn?.Contains(step.ParentStepId, StringComparer.Ordinal) ?? false))
            {
                // Parent may appear later in the list; final check after full scan.
            }

            deps[step.StepId] = step.DependsOn ?? [];
        }

        foreach (var step in plan.Steps)
        {
            if (step.ParentStepId is { Length: > 0 }
                && !stepIds.Contains(step.ParentStepId))
            {
                throw new ArgumentException(
                    $"Step '{step.StepId}' references unknown parent '{step.ParentStepId}'.",
                    nameof(plan));
            }

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
            var text = string.Concat(step.Goal, "\n", step.Title);
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
                    goal,
                    agent.AgentId,
                    DependsOn: [],
                    Depth: 0,
                    Title: "Execute")
            ]);
    }

    /// <summary>
    /// Aligns GM-produced plan versions with already-published revisions.
    /// First publish is forced to version "1"; later publishes auto-increment
    /// and set <see cref="WorkPlanDraft.PreviousPlanVersion"/> when the model omits them.
    /// </summary>
    private static void EnsureLeafExecutor(
        WorkPlanDraft plan,
        int index,
        WorkPlanStepDraft step,
        HashSet<string> agentIds,
        HashSet<string> blockedExecutors)
    {
        var hasEphemeral = !string.IsNullOrWhiteSpace(step.EphemeralSystemPrompt)
            || !string.IsNullOrWhiteSpace(step.EphemeralName);
        var hasAgent = !string.IsNullOrWhiteSpace(step.TargetAgentId)
            && !step.TargetAgentId.StartsWith("ephemeral:", StringComparison.Ordinal);
        if (hasEphemeral && hasAgent)
        {
            throw new ArgumentException(
                $"Step '{step.StepId}' cannot both name an agent and define a temporary specialist.",
                nameof(plan));
        }

        if (hasEphemeral)
        {
            if (string.IsNullOrWhiteSpace(step.EphemeralName) || string.IsNullOrWhiteSpace(step.EphemeralSystemPrompt))
            {
                throw new ArgumentException(
                    $"Step '{step.StepId}' temporary specialist needs ephemeralName and ephemeralSystemPrompt.",
                    nameof(plan));
            }

            plan.Steps[index] = step with { TargetAgentId = "ephemeral:" + step.StepId.Trim() };
            return;
        }

        if (string.IsNullOrWhiteSpace(step.TargetAgentId)
            || !agentIds.Contains(step.TargetAgentId)
            || blockedExecutors.Contains(step.TargetAgentId))
        {
            throw new ArgumentException(
                $"Step '{step.StepId}' targets unknown agent '{step.TargetAgentId}'.",
                nameof(plan));
        }
    }

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
        if (step.Inputs is { } inputs && inputs.ValueKind is not JsonValueKind.Undefined
            and not JsonValueKind.Null)
        {
            return inputs.GetRawText();
        }

        return string.Join(
            "\n",
            step.Goal,
            step.Title ?? string.Empty,
            step.TargetAgentId,
            step.Depth.ToString(CultureInfo.InvariantCulture),
            step.ParentStepId ?? string.Empty);
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
