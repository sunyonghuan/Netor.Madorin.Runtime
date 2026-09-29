using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Modes.Work;

/// <summary>Selects runnable work steps under dependency and concurrency constraints.</summary>
public static class WorkStepScheduler
{
    public static IReadOnlyList<WorkStepSnapshot> SelectReadySteps(
        IReadOnlyList<WorkStepSnapshot> steps,
        WorkflowPolicy policy,
        int currentlyRunningCount = 0)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();

        var byId = steps.ToDictionary(static s => s.StepId, StringComparer.Ordinal);
        var parentIds = steps
            .Select(static s => s.ParentStepId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var completed = steps
            .Where(static s => s.Status is WorkStepLifecycleStatus.Completed
                or WorkStepLifecycleStatus.Skipped)
            .Select(static s => s.StepId)
            .ToHashSet(StringComparer.Ordinal);
        var phaseOrder = PhaseOrder(steps, parentIds);

        var capacity = Math.Max(0, policy.MaxConcurrentSteps - currentlyRunningCount);
        if (capacity == 0)
        {
            return [];
        }

        var ready = new List<WorkStepSnapshot>();
        foreach (var step in steps
            .Where(step => step.Status == WorkStepLifecycleStatus.Pending
                && !parentIds.Contains(step.StepId))
            .OrderBy(static s => s.Depth)
            .ThenBy(static s => s.StepId, StringComparer.Ordinal))
        {
            var deps = step.DependsOn ?? [];
            if (deps.Any(dep => !completed.Contains(dep)))
            {
                continue;
            }

            if (!PreviousPhaseSettled(steps, phaseOrder, step))
            {
                continue;
            }

            if (step.ParentStepId is { Length: > 0 }
                && byId.TryGetValue(step.ParentStepId, out var parent)
                && parent.Status is not (WorkStepLifecycleStatus.Completed
                    or WorkStepLifecycleStatus.Skipped
                    or WorkStepLifecycleStatus.Running
                    or WorkStepLifecycleStatus.Pending))
            {
                // Parent failed/interrupted: skip unless failure policy handled elsewhere.
                continue;
            }

            ready.Add(step);
            if (ready.Count >= capacity)
            {
                break;
            }
        }

        return ready;
    }

    /// <summary>阶段按计划里第一次出现的顺序排列。</summary>
    private static List<string> PhaseOrder(IReadOnlyList<WorkStepSnapshot> steps, HashSet<string?> parentIds)
    {
        var order = new List<string>();
        foreach (var step in steps)
        {
            if (parentIds.Contains(step.StepId) && !order.Contains(step.StepId))
            {
                order.Add(step.StepId);
            }

            if (!string.IsNullOrWhiteSpace(step.ParentStepId) && !order.Contains(step.ParentStepId))
            {
                order.Add(step.ParentStepId);
            }
        }

        return order;
    }

    /// <summary>后一阶段的子步骤，要等前一阶段的子步骤完成或跳过。</summary>
    private static bool PreviousPhaseSettled(
        IReadOnlyList<WorkStepSnapshot> steps,
        List<string> phaseOrder,
        WorkStepSnapshot step)
    {
        if (string.IsNullOrWhiteSpace(step.ParentStepId))
        {
            return true;
        }

        var phaseIndex = phaseOrder.IndexOf(step.ParentStepId);
        if (phaseIndex <= 0)
        {
            return true;
        }

        var previousPhaseId = phaseOrder[phaseIndex - 1];
        foreach (var earlier in steps)
        {
            if (!string.Equals(earlier.ParentStepId, previousPhaseId, StringComparison.Ordinal))
            {
                continue;
            }

            if (earlier.Status is not (WorkStepLifecycleStatus.Completed or WorkStepLifecycleStatus.Skipped))
            {
                return false;
            }
        }

        return true;
    }
}
