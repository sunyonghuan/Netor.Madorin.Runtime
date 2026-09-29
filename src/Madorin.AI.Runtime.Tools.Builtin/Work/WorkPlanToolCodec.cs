using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

        root = NormalizeSetPlanArguments(root);

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
        message = "Provide planVersion, goal, and flat steps[] "
            + "(each step has stepId and goal; a leaf has targetAgentId; a child sets parentStepId), "
            + "or main_steps[].sub_steps[] "
            + "(each sub_step has title, acceptance_criteria, and targetAgentId). "
            + "The agent property name is targetAgentId.";
        return false;
    }

    public static bool TryResolveDispatchTarget(
        WorkPlanDraft plan,
        int stepIndex,
        int subStepIndex,
        out WorkPlanStepDraft? step,
        out string? errorCode,
        out string? message)
    {
        step = null;
        errorCode = null;
        message = null;

        if (stepIndex < 1 || subStepIndex < 1)
        {
            errorCode = "invalid_arguments";
            message = "stepIndex and subStepIndex must be >= 1.";
            return false;
        }

        foreach (var candidate in plan.Steps)
        {
            if (!TryReadIndexPair(candidate, out var main, out var sub))
            {
                continue;
            }

            if (main == stepIndex && sub == subStepIndex)
            {
                step = candidate;
                return true;
            }
        }

        if (subStepIndex == 1 && stepIndex <= plan.Steps.Length)
        {
            step = plan.Steps[stepIndex - 1];
            return true;
        }

        errorCode = "step_not_found";
        message = string.Create(
            CultureInfo.InvariantCulture,
            $"No step matches stepIndex={stepIndex}, subStepIndex={subStepIndex}.");
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

        if (!TryRejectEmptyLeafExecutors(plan, out errorCode, out message))
        {
            plan = null;
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
            ? null
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
        if (!TryRejectEmptyLeafExecutors(plan, out errorCode, out message))
        {
            plan = null;
            return false;
        }

        return true;
    }

    private static bool TryAppendMainStep(
        JsonElement main,
        JsonElement subSteps,
        int mainOrdinal,
        string mainTitle,
        string? fallbackAgent,
        List<WorkPlanStepDraft> steps,
        ref string? previousStepId,
        out string? errorCode,
        out string? message)
    {
        errorCode = null;
        message = null;
        var parentId = string.Create(CultureInfo.InvariantCulture, $"m{mainOrdinal}");
        steps.Add(new WorkPlanStepDraft(
            parentId,
            mainTitle,
            string.Empty,
            Depth: 0,
            Title: mainTitle));

        string? previousChildId = null;
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

            var ephemeralName = ReadOptionalString(sub, "ephemeralName");
            var ephemeralPrompt = ReadOptionalString(sub, "ephemeralSystemPrompt");
            var target = ReadOptionalString(sub, "targetAgentId")
                ?? ReadOptionalString(sub, "target_agent_id")
                ?? ReadOptionalString(main, "targetAgentId")
                ?? fallbackAgent;
            var hasEphemeral = !string.IsNullOrWhiteSpace(ephemeralPrompt) || !string.IsNullOrWhiteSpace(ephemeralName);
            var hasAgent = !string.IsNullOrWhiteSpace(target)
                && !target.StartsWith("ephemeral:", StringComparison.Ordinal);
            if (hasEphemeral && hasAgent)
            {
                errorCode = "invalid_plan";
                message = $"sub_step '{subTitle}' cannot both name an agent and define a temporary specialist.";
                return false;
            }

            if (!hasEphemeral && !hasAgent)
            {
                errorCode = "invalid_plan";
                message = $"sub_step '{subTitle}' needs targetAgentId or ephemeralSystemPrompt.";
                return false;
            }

            var stepId = string.Create(CultureInfo.InvariantCulture, $"m{mainOrdinal}-s{subOrdinal}");
            var dependsOn = previousChildId is not null
                ? new[] { previousChildId }
                : previousStepId is null
                    ? Array.Empty<string>()
                    : new[] { previousStepId };
            steps.Add(new WorkPlanStepDraft(
                stepId,
                string.Create(CultureInfo.InvariantCulture, $"{subTitle}. Acceptance: {acceptance}"),
                hasEphemeral ? "ephemeral:" + stepId : target!,
                DependsOn: dependsOn,
                ParentStepId: parentId,
                Depth: 1,
                Title: subTitle,
                Inputs: BuildIndexInputs(mainOrdinal, subOrdinal, mainTitle, acceptance),
                EphemeralName: ephemeralName,
                EphemeralSystemPrompt: ephemeralPrompt,
                Investigate: ReadOptionalBool(sub, "investigate")));
            previousChildId = stepId;
        }

        previousStepId = previousChildId;
        return true;
    }

    private static bool ReadOptionalBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static string BuildFoldedGoal(string mainTitle, List<PlanGate> gates)
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

    private static bool TryReadIndexPair(WorkPlanStepDraft step, out int mainIndex, out int subIndex)
    {
        mainIndex = 0;
        subIndex = 0;
        if (step.Inputs is not JsonElement inputs || inputs.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!inputs.TryGetProperty("mainIndex", out var main)
            || !inputs.TryGetProperty("subIndex", out var sub)
            || main.ValueKind != JsonValueKind.Number
            || sub.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        mainIndex = main.GetInt32();
        subIndex = sub.GetInt32();
        return mainIndex >= 1 && subIndex >= 1;
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

    private static readonly string[] AgentAliases = ["target_agent_id", "agentId", "assigneeId", "targetAgent"];

    private static readonly string[] NestedChildNames = ["sub_steps", "subSteps", "substeps", "children"];

    /// <summary>
    /// 把模型猜出来的嵌套子步骤和执行人别名收成扁平 steps 或 main_steps.sub_steps。
    /// 未收成的嵌套键在反序列化时会被丢掉，父步骤就会变成没有 targetAgentId 的叶子。
    /// </summary>
    private static JsonElement NormalizeSetPlanArguments(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return root;
        }

        try
        {
            if (JsonNode.Parse(root.GetRawText()) is not JsonObject node)
            {
                return root;
            }

            FlattenRootSteps(node);
            if (node["main_steps"] is JsonArray mainSteps)
            {
                NormalizeMainSteps(mainSteps);
            }

            using var document = JsonDocument.Parse(node.ToJsonString());
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return root;
        }
    }

    private static void FlattenRootSteps(JsonObject root)
    {
        if (root["steps"] is not JsonArray steps || steps.Count == 0)
        {
            return;
        }

        var flat = new JsonArray();
        var ordinal = 0;
        foreach (var item in steps.ToArray())
        {
            if (item is not JsonObject step)
            {
                continue;
            }

            steps.Remove(step);
            ordinal++;
            AppendFlatStep(step, flat, parentStepId: null, siblingOrdinal: ordinal);
        }

        root["steps"] = flat;
        if (!HasText(root, "planVersion"))
        {
            root["planVersion"] = "1";
        }

        if (!HasText(root, "goal"))
        {
            root["goal"] = "Work plan";
        }
    }

    private static void AppendFlatStep(
        JsonObject step,
        JsonArray flat,
        string? parentStepId,
        int siblingOrdinal)
    {
        PromoteAgent(step);
        EnsureGoal(step);
        FoldAcceptanceIntoGoal(step);
        var children = DetachChildArray(step);
        if (!string.IsNullOrWhiteSpace(parentStepId) && !HasText(step, "parentStepId"))
        {
            step["parentStepId"] = parentStepId;
        }

        var stepId = EnsureStepId(step, parentStepId, siblingOrdinal);
        if (!string.IsNullOrWhiteSpace(parentStepId) && step["depth"] is null)
        {
            step["depth"] = 1;
        }

        flat.Add((JsonNode)step);
        if (children is null)
        {
            return;
        }

        var childOrdinal = 0;
        foreach (var child in children.ToArray())
        {
            if (child is not JsonObject childStep)
            {
                continue;
            }

            children.Remove(childStep);
            childOrdinal++;
            if (!HasText(childStep, "targetAgentId"))
            {
                PromoteAgent(childStep);
            }

            if (!HasText(childStep, "targetAgentId"))
            {
                var inherited = ReadJsonText(step["targetAgentId"]);
                if (!string.IsNullOrWhiteSpace(inherited))
                {
                    childStep["targetAgentId"] = inherited;
                }
            }

            AppendFlatStep(childStep, flat, stepId, childOrdinal);
        }
    }

    private static void NormalizeMainSteps(JsonArray mainSteps)
    {
        foreach (var item in mainSteps)
        {
            if (item is not JsonObject main)
            {
                continue;
            }

            PromoteAgent(main);
            PromoteChildProperty(main);
            if (main["sub_steps"] is not JsonArray subSteps)
            {
                continue;
            }

            foreach (var sub in subSteps)
            {
                if (sub is not JsonObject subStep)
                {
                    continue;
                }

                PromoteAgent(subStep);
                PromoteAcceptance(subStep);
                if (HasText(subStep, "targetAgentId") || !HasText(main, "targetAgentId"))
                {
                    continue;
                }

                subStep["targetAgentId"] = ReadJsonText(main["targetAgentId"]);
            }
        }
    }

    private static void PromoteChildProperty(JsonObject main)
    {
        if (main["sub_steps"] is JsonArray)
        {
            return;
        }

        foreach (var name in NestedChildNames)
        {
            if (name == "sub_steps" || main[name] is not JsonArray array || array.Count == 0)
            {
                continue;
            }

            main.Remove(name);
            main["sub_steps"] = array;
            return;
        }

        if (main["steps"] is JsonArray nested
            && nested.Count > 0
            && nested[0] is JsonObject)
        {
            main.Remove("steps");
            main["sub_steps"] = nested;
        }
    }

    private static JsonArray? DetachChildArray(JsonObject step)
    {
        foreach (var name in NestedChildNames)
        {
            if (step[name] is not JsonArray array || array.Count == 0)
            {
                continue;
            }

            step.Remove(name);
            return array;
        }

        if (step["steps"] is JsonArray nested
            && nested.Count > 0
            && nested[0] is JsonObject)
        {
            step.Remove("steps");
            return nested;
        }

        return null;
    }

    private static void PromoteAgent(JsonObject step)
    {
        if (HasText(step, "targetAgentId"))
        {
            CoerceStringProperty(step, "targetAgentId");
            return;
        }

        foreach (var alias in AgentAliases)
        {
            var text = ReadJsonText(step[alias]);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            step["targetAgentId"] = text;
            step.Remove(alias);
            return;
        }
    }

    private static void PromoteAcceptance(JsonObject step)
    {
        if (HasText(step, "acceptance_criteria"))
        {
            return;
        }

        var acceptance = ReadJsonText(step["acceptanceCriteria"]);
        if (!string.IsNullOrWhiteSpace(acceptance))
        {
            step["acceptance_criteria"] = acceptance;
        }
    }

    private static void EnsureGoal(JsonObject step)
    {
        if (HasText(step, "goal"))
        {
            return;
        }

        var title = ReadJsonText(step["title"]);
        if (!string.IsNullOrWhiteSpace(title))
        {
            step["goal"] = title;
        }
    }

    private static void FoldAcceptanceIntoGoal(JsonObject step)
    {
        var acceptance = ReadJsonText(step["acceptance_criteria"])
            ?? ReadJsonText(step["acceptanceCriteria"]);
        if (string.IsNullOrWhiteSpace(acceptance))
        {
            return;
        }

        var goal = ReadJsonText(step["goal"]) ?? string.Empty;
        if (goal.Contains(acceptance, StringComparison.Ordinal))
        {
            return;
        }

        step["goal"] = string.IsNullOrWhiteSpace(goal)
            ? "Acceptance: " + acceptance
            : goal.TrimEnd() + ". Acceptance: " + acceptance;
    }

    private static string EnsureStepId(JsonObject step, string? parentStepId, int siblingOrdinal)
    {
        var existing = ReadJsonText(step["stepId"]);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            step["stepId"] = existing;
            return existing;
        }

        var generated = string.IsNullOrWhiteSpace(parentStepId)
            ? "s" + siblingOrdinal.ToString(CultureInfo.InvariantCulture)
            : parentStepId + "-s" + siblingOrdinal.ToString(CultureInfo.InvariantCulture);
        step["stepId"] = generated;
        return generated;
    }

    private static void CoerceStringProperty(JsonObject step, string name)
    {
        var text = ReadJsonText(step[name]);
        if (!string.IsNullOrWhiteSpace(text))
        {
            step[name] = text;
        }
    }

    private static bool TryRejectEmptyLeafExecutors(
        WorkPlanDraft plan,
        out string? errorCode,
        out string? message)
    {
        errorCode = null;
        message = null;
        var parentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in plan.Steps)
        {
            if (!string.IsNullOrWhiteSpace(step.ParentStepId))
            {
                parentIds.Add(step.ParentStepId);
            }
        }

        foreach (var step in plan.Steps)
        {
            if (parentIds.Contains(step.StepId))
            {
                continue;
            }

            var hasEphemeral = !string.IsNullOrWhiteSpace(step.EphemeralSystemPrompt)
                || !string.IsNullOrWhiteSpace(step.EphemeralName);
            if (hasEphemeral || !string.IsNullOrWhiteSpace(step.TargetAgentId))
            {
                continue;
            }

            errorCode = "invalid_plan";
            message = $"Step '{step.StepId}' is a leaf and targetAgentId is empty. "
                + "Set targetAgentId to a specialist id from AvailableAgents. "
                + "A parent omits targetAgentId only when another step sets parentStepId to this stepId. "
                + "Put child steps in the same steps array, or use main_steps[].sub_steps[] with targetAgentId on every sub_step. "
                + "The agent property name is targetAgentId.";
            return false;
        }

        return true;
    }

    private static bool HasText(JsonObject obj, string name) =>
        !string.IsNullOrWhiteSpace(ReadJsonText(obj[name]));

    private static string? ReadJsonText(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<string>(out var text))
        {
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static bool ContainsPlaceholder(string text) =>
        text.Contains("{{", StringComparison.Ordinal)
        && text.Contains("}}", StringComparison.Ordinal);
}
