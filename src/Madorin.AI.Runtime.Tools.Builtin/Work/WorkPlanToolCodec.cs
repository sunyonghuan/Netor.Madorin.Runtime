using System.Globalization;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;

namespace Madorin.AI.Runtime.Tools.Builtin.Work;

/// <summary>
/// 把 mcli_work_set_plan 参数解析成 WorkPlanDraft。
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

    private static bool TryParseNativeDraft(
        JsonElement root,
        out WorkPlanDraft? plan,
        out string? errorCode,
        out string? message)
    {
        plan = null;
        errorCode = null;
        message = null;
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

            if (!TryAppendSubSteps(
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

    private static bool TryAppendSubSteps(
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
            var stepId = ReadOptionalString(sub, "stepId")
                ?? string.Create(CultureInfo.InvariantCulture, $"m{mainOrdinal}-s{subOrdinal}");
            var goalText = string.Create(
                CultureInfo.InvariantCulture,
                $"{subTitle}. Acceptance: {acceptance}");
            var dependsOn = previousStepId is null ? Array.Empty<string>() : new[] { previousStepId };
            steps.Add(new WorkPlanStepDraft(
                stepId,
                goalText,
                target,
                DependsOn: dependsOn,
                Depth: 0,
                Title: subTitle,
                Inputs: BuildIndexInputs(mainOrdinal, subOrdinal, mainTitle, acceptance)));
            previousStepId = stepId;
        }

        return true;
    }

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

