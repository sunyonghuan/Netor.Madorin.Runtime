using System.Globalization;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;

namespace Madorin.AI.Runtime.Tools.Builtin.Work;

/// <summary>
/// 把 mcli_work_set_plan / dispatch_step 参数解析成 WorkPlanDraft。
/// 同时接受原生 planVersion/goal/steps，以及提示词里的 main_steps/sub_steps。
/// </summary>
internal static class WorkPlanToolCodec
{
    public static bool TryParseSetPlan(
        JsonElement root,
        string? defaultTargetAgentId,
        out WorkPlanDraft? plan,
        out string? errorCode,
        out string? message)
    {
        plan = null;
        errorCode = null;
        message = null;

        if (root.ValueKind != JsonValueKind.Object)
        {
            errorCode = "invalid_arguments";
            message = "set_plan arguments must be a JSON object.";
            return false;
        }

        if (ContainsPlaceholder(root.GetRawText()))
        {
            errorCode = "invalid_plan";
            message = "Plan arguments must not contain {{placeholders}}.";
            return false;
        }

        if (root.TryGetProperty("steps", out _)
            && root.TryGetProperty("goal", out _)
            && root.TryGetProperty("planVersion", out _))
        {
            return TryParseNativeDraft(root, out plan, out errorCode, out message);
        }

        if (root.TryGetProperty("main_steps", out var mainSteps)
            && mainSteps.ValueKind == JsonValueKind.Array)
        {
            return TryParseMainSteps(
                root,
                mainSteps,
                defaultTargetAgentId,
                out plan,
                out errorCode,
                out message);
        }

        if (root.TryGetProperty("plan", out var nested)
            && nested.ValueKind == JsonValueKind.Object)
        {
            return TryParseNativeDraft(nested, out plan, out errorCode, out message);
        }

        errorCode = "invalid_plan";
        message = "Provide WorkPlanDraft fields (planVersion/goal/steps) or main_steps[].sub_steps[].";
        return false;
    }

    public static bool TryResolveDispatchTarget(
        WorkPlanDraft plan,
        int stepIndex,
        int subStepIndex,
        out WorkPlanSubStepDraft? subStep,
        out string? errorCode,
        out string? message)
    {
        subStep = null;
        errorCode = null;
        message = null;

        if (stepIndex < 1 || subStepIndex < 1)
        {
            errorCode = "invalid_arguments";
            message = "stepIndex and subStepIndex must be >= 1.";
            return false;
        }

        if (stepIndex > plan.Steps.Length)
        {
            errorCode = "step_not_found";
            message = string.Create(
                CultureInfo.InvariantCulture,
                $"stepIndex={stepIndex} exceeds total steps count={plan.Steps.Length}.");
            return false;
        }

        var step = plan.Steps[stepIndex - 1];
        var subSteps = step.SubSteps ?? [];
        if (subSteps.Length == 0 || subStepIndex > subSteps.Length)
        {
            errorCode = "substep_not_found";
            message = string.Create(
                CultureInfo.InvariantCulture,
                $"step {stepIndex} has no resolvable sub-steps; subStepIndex={subStepIndex} exceeds count={subSteps.Length}.");
            return false;
        }

        subStep = subSteps[subStepIndex - 1];
        return true;
    }

    private static bool TryParseNativeDraft(
        JsonElement root,
        out WorkPlanDraft? plan,
        out string? errorCode,
        out string? message)
    {
        plan = null;
        errorCode = null;
        message = null;

        // Check if this is legacy format (steps have 'goal' and 'targetAgentId' instead of 'subSteps')
        if (root.TryGetProperty("steps", out var stepsElement)
            && stepsElement.ValueKind == JsonValueKind.Array
            && stepsElement.GetArrayLength() > 0)
        {
            var firstStep = stepsElement[0];
            if (firstStep.ValueKind == JsonValueKind.Object
                && firstStep.TryGetProperty("goal", out _)
                && firstStep.TryGetProperty("targetAgentId", out _)
                && !firstStep.TryGetProperty("subSteps", out _))
            {
                // Legacy format detected - convert to new format
                return TryConvertLegacyFormat(root, out plan, out errorCode, out message);
            }
        }

        try
        {
            plan = JsonSerializer.Deserialize(
                root.GetRawText(),
                RuntimeJsonContext.Default.WorkPlanDraft);
        }
        catch (JsonException ex)
        {
            errorCode = "invalid_plan";
            message = ex.Message;
            return false;
        }

        if (plan is null || plan.Steps is null || plan.Steps.Length == 0)
        {
            errorCode = "invalid_plan";
            message = "WorkPlanDraft must include at least one step.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(plan.PlanVersion) || string.IsNullOrWhiteSpace(plan.Goal))
        {
            errorCode = "invalid_plan";
            message = "WorkPlanDraft requires planVersion and goal.";
            return false;
        }

        return true;
    }

    private static bool TryConvertLegacyFormat(
        JsonElement root,
        out WorkPlanDraft? plan,
        out string? errorCode,
        out string? message)
    {
        plan = null;
        errorCode = null;
        message = null;

        var planVersion = ReadOptionalString(root, "planVersion") ?? "1";
        var goal = ReadOptionalString(root, "goal") ?? "Work plan";
        var summary = ReadOptionalString(root, "summary");
        var previousPlanVersion = ReadOptionalString(root, "previousPlanVersion");

        if (!root.TryGetProperty("steps", out var stepsElement)
            || stepsElement.ValueKind != JsonValueKind.Array)
        {
            errorCode = "invalid_plan";
            message = "Legacy plan must have steps array.";
            return false;
        }

        var steps = new List<WorkPlanStepDraft>();
        var stepIndex = 0;

        foreach (var legacyStep in stepsElement.EnumerateArray())
        {
            stepIndex++;
            if (legacyStep.ValueKind != JsonValueKind.Object)
            {
                errorCode = "invalid_plan";
                message = $"Step at index {stepIndex} must be an object.";
                return false;
            }

            var stepId = ReadOptionalString(legacyStep, "stepId");
            if (string.IsNullOrWhiteSpace(stepId))
            {
                errorCode = "invalid_plan";
                message = $"Step at index {stepIndex} is missing stepId.";
                return false;
            }

            var legacyGoal = ReadOptionalString(legacyStep, "goal");
            var targetAgentId = ReadOptionalString(legacyStep, "targetAgentId");
            var title = ReadOptionalString(legacyStep, "title") ?? legacyGoal ?? $"Step {stepIndex}";
            var description = ReadOptionalString(legacyStep, "description") ?? legacyGoal ?? title;

            if (string.IsNullOrWhiteSpace(targetAgentId))
            {
                errorCode = "invalid_plan";
                message = $"Step '{stepId}' is missing targetAgentId.";
                return false;
            }

            // Parse dependsOn array
            string[]? dependsOn = null;
            if (legacyStep.TryGetProperty("dependsOn", out var dependsOnElement)
                && dependsOnElement.ValueKind == JsonValueKind.Array)
            {
                var deps = new List<string>();
                foreach (var dep in dependsOnElement.EnumerateArray())
                {
                    if (dep.ValueKind == JsonValueKind.String)
                    {
                        var depValue = dep.GetString();
                        if (!string.IsNullOrWhiteSpace(depValue))
                        {
                            deps.Add(depValue);
                        }
                    }
                }
                dependsOn = deps.Count > 0 ? deps.ToArray() : null;
            }

            // Convert legacy step to new format with one SubStep
            var subStepId = $"{stepId}.1";
            var subStep = new WorkPlanSubStepDraft(
                subStepId,
                legacyGoal ?? title,
                description,
                targetAgentId,
                ExpectedOutput: null,
                Inputs: null);

            var newStep = new WorkPlanStepDraft(
                stepId,
                title,
                description,
                [subStep],
                DependsOn: dependsOn,
                IsBackground: false,
                ReusesStepId: ReadOptionalString(legacyStep, "reusesStepId"),
                ReplacesStepId: ReadOptionalString(legacyStep, "replacesStepId"));

            steps.Add(newStep);
        }

        plan = new WorkPlanDraft(
            planVersion,
            goal,
            steps.ToArray(),
            Summary: summary,
            PreviousPlanVersion: previousPlanVersion);

        return true;
    }

    private static bool TryParseMainSteps(
        JsonElement root,
        JsonElement mainSteps,
        string? defaultTargetAgentId,
        out WorkPlanDraft? plan,
        out string? errorCode,
        out string? message)
    {
        plan = null;
        errorCode = null;
        message = null;

        var goal = ReadOptionalString(root, "goal")
            ?? ReadOptionalString(root, "title")
            ?? "Work plan";
        var planVersion = ReadOptionalString(root, "planVersion") ?? "1";
        var previous = ReadOptionalString(root, "previousPlanVersion");
        var summary = ReadOptionalString(root, "summary");
        var fallbackAgent = string.IsNullOrWhiteSpace(defaultTargetAgentId)
            ? "project-lead"
            : defaultTargetAgentId.Trim();

        var steps = new List<WorkPlanStepDraft>();
        string? previousStepId = null;
        var mainOrdinal = 0;
        foreach (var main in mainSteps.EnumerateArray())
        {
            mainOrdinal++;
            if (main.ValueKind != JsonValueKind.Object)
            {
                errorCode = "invalid_plan";
                message = $"main_steps[{mainOrdinal}] must be an object.";
                return false;
            }

            var mainTitle = ReadOptionalString(main, "title")
                ?? ReadOptionalString(main, "name")
                ?? $"main-{mainOrdinal}";
            if (!main.TryGetProperty("sub_steps", out var subSteps)
                || subSteps.ValueKind != JsonValueKind.Array
                || subSteps.GetArrayLength() == 0)
            {
                errorCode = "invalid_plan";
                message = $"main_steps[{mainOrdinal}] ('{mainTitle}') requires non-empty sub_steps.";
                return false;
            }

            if (!TryAppendMainStep(
                    main,
                    subSteps,
                    mainOrdinal,
                    mainTitle,
                    fallbackAgent,
                    steps,
                    ref previousStepId,
                    out errorCode,
                    out message))
            {
                return false;
            }
        }

        if (steps.Count == 0)
        {
            errorCode = "invalid_plan";
            message = "main_steps produced no executable sub-steps.";
            return false;
        }

        plan = new WorkPlanDraft(
            planVersion,
            goal,
            [.. steps],
            Summary: summary,
            PreviousPlanVersion: previous);
        return true;
    }

    private static bool TryAppendMainStep(
        JsonElement main,
        JsonElement subSteps,
        int mainOrdinal,
        string mainTitle,
        string fallbackAgent,
        List<WorkPlanStepDraft> steps,
        ref string? previousStepId,
        out string? errorCode,
        out string? message)
    {
        errorCode = null;
        message = null;
        var gates = new List<PlanGate>();
        var subOrdinal = 0;
        foreach (var sub in subSteps.EnumerateArray())
        {
            subOrdinal++;
            if (sub.ValueKind != JsonValueKind.Object)
            {
                errorCode = "invalid_plan";
                message = $"main_steps[{mainOrdinal}].sub_steps[{subOrdinal}] must be an object.";
                return false;
            }

            var subTitle = ReadOptionalString(sub, "title")
                ?? ReadOptionalString(sub, "name");
            if (string.IsNullOrWhiteSpace(subTitle))
            {
                errorCode = "invalid_plan";
                message = $"main_steps[{mainOrdinal}].sub_steps[{subOrdinal}] requires title.";
                return false;
            }

            var acceptance = ReadOptionalString(sub, "acceptance_criteria")
                ?? ReadOptionalString(sub, "acceptanceCriteria");
            if (string.IsNullOrWhiteSpace(acceptance))
            {
                errorCode = "invalid_plan";
                message = $"sub_step '{subTitle}' requires acceptance_criteria.";
                return false;
            }

            var target = ReadOptionalString(sub, "targetAgentId")
                ?? ReadOptionalString(sub, "target_agent_id")
                ?? ReadOptionalString(main, "targetAgentId")
                ?? fallbackAgent;
            gates.Add(new PlanGate(subTitle, acceptance, target));
        }

        var distinctAgents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gate in gates)
        {
            distinctAgents.Add(gate.TargetAgentId);
        }

        // 同一主步骤、同一执行人：门禁留在 goal 里，只派发一次。
        // 主步骤里换了执行人时才拆成多个派发步骤。
        var stepId = string.Create(CultureInfo.InvariantCulture, $"m{mainOrdinal}");
        var dependsOn = previousStepId is null ? Array.Empty<string>() : new[] { previousStepId };

        var subStepDrafts = new List<WorkPlanSubStepDraft>();
        for (var index = 0; index < gates.Count; index++)
        {
            var gate = gates[index];
            var subStepId = string.Create(CultureInfo.InvariantCulture, $"{stepId}.{index + 1}");
            subStepDrafts.Add(new WorkPlanSubStepDraft(
                subStepId,
                gate.Title,
                $"{gate.Title}. Acceptance: {gate.Acceptance}",
                gate.TargetAgentId,
                ExpectedOutput: gate.Acceptance,
                Inputs: BuildIndexInputs(mainOrdinal, index + 1, mainTitle, gate.Acceptance)));
        }

        steps.Add(new WorkPlanStepDraft(
            stepId,
            mainTitle,
            BuildFoldedDescription(mainTitle, gates),
            subStepDrafts.ToArray(),
            DependsOn: dependsOn,
            IsBackground: false));
        previousStepId = stepId;
        return true;
    }

    private static string BuildFoldedDescription(string mainTitle, List<PlanGate> gates)
    {
        var builder = new StringBuilder(mainTitle);
        builder.AppendLine();
        builder.AppendLine("Complete every gate in order. Redo the current gate when verification fails.");
        for (var index = 0; index < gates.Count; index++)
        {
            var gate = gates[index];
            builder.Append(CultureInfo.InvariantCulture, $"{index + 1}. {gate.Title}. Acceptance: {gate.Acceptance}");
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private readonly record struct PlanGate(string Title, string Acceptance, string TargetAgentId);

    private static JsonElement BuildIndexInputs(
        int mainIndex,
        int subIndex,
        string mainTitle,
        string acceptance)
    {
        var json = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("mainIndex", mainIndex);
            writer.WriteNumber("subIndex", subIndex);
            writer.WriteString("mainTitle", mainTitle);
            writer.WriteString("acceptanceCriteria", acceptance);
            writer.WriteEndObject();
        });
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string? ReadOptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    private static bool ContainsPlaceholder(string text) =>
        text.Contains("{{", StringComparison.Ordinal)
        && text.Contains("}}", StringComparison.Ordinal);
}
