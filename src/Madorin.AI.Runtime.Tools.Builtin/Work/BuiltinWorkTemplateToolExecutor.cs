using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin.Work;

/// <summary>
/// Work 模板三件套 + set_plan/dispatch_step。
/// set_plan 产出规范化 WorkPlanDraft（编排器落盘）；dispatch_step 校验已确认计划并同步调度子 Agent。
/// </summary>
public sealed class BuiltinWorkTemplateToolExecutor : IToolExecutor
{
    private readonly WorkTemplateStore _store;
    private readonly Func<string, CancellationToken, Task<WorkSessionPlanView?>>? _planLookup;
    private readonly Func<WorkDispatchStepRequest, CancellationToken, Task<WorkDispatchStepResult>>? _dispatchRunner;
    private readonly string? _defaultTargetAgentId;

    public BuiltinWorkTemplateToolExecutor(
        string? userRootOverride = null,
        string? systemRootOverride = null,
        Func<string, CancellationToken, Task<WorkSessionPlanView?>>? planLookup = null,
        string? defaultTargetAgentId = null,
        Func<WorkDispatchStepRequest, CancellationToken, Task<WorkDispatchStepResult>>? dispatchRunner = null)
    {
        _store = new WorkTemplateStore(userRootOverride, systemRootOverride);
        _planLookup = planLookup;
        _defaultTargetAgentId = defaultTargetAgentId;
        _dispatchRunner = dispatchRunner;
    }

    public bool CanExecute(string toolId) =>
        toolId is BuiltinToolRegistry.WorkListPlanTemplatesToolId
            or BuiltinToolRegistry.WorkLoadPlanFromTemplateToolId
            or BuiltinToolRegistry.WorkReadTemplateFileToolId
            or BuiltinToolRegistry.WorkSetPlanToolId
            or BuiltinToolRegistry.WorkDispatchStepToolId;

    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ct.ThrowIfCancellationRequested();
        if (!CanExecute(invocation.ToolId))
        {
            return Failure(invocation, "unsupported_tool", "The work executor cannot execute this tool.");
        }

        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(invocation.ArgumentsJson)
                    ? "{}"
                    : invocation.ArgumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Failure(invocation, "invalid_arguments", "Tool arguments must be a JSON object.");
            }

            return invocation.ToolId switch
            {
                BuiltinToolRegistry.WorkListPlanTemplatesToolId => ListTemplates(invocation, document.RootElement),
                BuiltinToolRegistry.WorkLoadPlanFromTemplateToolId => LoadTemplate(invocation, document.RootElement),
                BuiltinToolRegistry.WorkReadTemplateFileToolId => ReadTemplateFile(invocation, document.RootElement),
                BuiltinToolRegistry.WorkSetPlanToolId => SetPlan(invocation, document.RootElement),
                BuiltinToolRegistry.WorkDispatchStepToolId => await DispatchStepAsync(
                    invocation,
                    document.RootElement,
                    ct).ConfigureAwait(false),
                _ => Failure(invocation, "unsupported_tool", "The work tool is not implemented.")
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException ex)
        {
            return Failure(invocation, "invalid_arguments", ex.Message);
        }
    }

    private ToolResult ListTemplates(ToolInvocation invocation, JsonElement arguments)
    {
        var keyword = GetOptionalString(arguments, "keyword");
        var category = GetOptionalString(arguments, "category");
        var templates = _store.List(keyword, category);
        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteStartArray("templates");
            foreach (var template in templates)
            {
                writer.WriteStartObject();
                writer.WriteString("name", template.Name);
                writer.WriteString("description", template.Description);
                writer.WriteString("path", template.Path);
                writer.WriteString("category", template.Category);
                writer.WriteString("version", template.Version);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
        return new ToolResult(invocation.CallId, invocation.ToolId, true, output);
    }

    private ToolResult LoadTemplate(ToolInvocation invocation, JsonElement arguments)
    {
        var path = GetOptionalString(arguments, "path");
        if (string.IsNullOrWhiteSpace(path))
        {
            return Failure(invocation, "path_not_allowed", "path is required.");
        }

        if (!_store.TryLoad(path, out var document, out var absolutePath, out var errorCode, out var message)
            || document is null)
        {
            return Failure(invocation, errorCode, message);
        }

        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteString("path", absolutePath);
            writer.WriteString("name", document.Name);
            writer.WriteString("description", document.Description);
            writer.WriteString("category", document.Category);
            writer.WriteString("version", document.Version);
            writer.WriteString("instructions", document.Instructions);
            writer.WriteString("planSkeleton", document.PlanSkeleton);
            writer.WriteStartArray("files");
            foreach (var file in document.Files)
            {
                writer.WriteStartObject();
                writer.WriteString("relativePath", file.RelativePath);
                writer.WriteString("kind", file.Kind);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString(
                "note",
                "Template materials only. Call mcli_work_set_plan to persist an executable plan. Do not expand {{placeholders}}.");
            writer.WriteEndObject();
        });
        return new ToolResult(invocation.CallId, invocation.ToolId, true, output);
    }

    private ToolResult ReadTemplateFile(ToolInvocation invocation, JsonElement arguments)
    {
        var path = GetOptionalString(arguments, "path");
        var relativePath = GetOptionalString(arguments, "relativePath");
        if (string.IsNullOrWhiteSpace(path))
        {
            return Failure(invocation, "path_not_allowed", "path is required.");
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return Failure(invocation, "path_not_allowed", "relativePath is required.");
        }

        if (!_store.TryReadFile(
                path,
                relativePath,
                out var content,
                out var absolutePath,
                out var normalizedRelative,
                out var errorCode,
                out var message))
        {
            return Failure(invocation, errorCode, message);
        }

        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteString("path", absolutePath);
            writer.WriteString("relativePath", normalizedRelative);
            writer.WriteString("content", content);
            writer.WriteEndObject();
        });
        return new ToolResult(invocation.CallId, invocation.ToolId, true, output);
    }

    private ToolResult SetPlan(ToolInvocation invocation, JsonElement arguments)
    {
        if (!WorkPlanToolCodec.TryParseSetPlan(
                arguments,
                _defaultTargetAgentId,
                out var plan,
                out var errorCode,
                out var message)
            || plan is null)
        {
            return Failure(invocation, errorCode ?? "invalid_plan", message ?? "Invalid plan.");
        }

        var planJson = JsonSerializer.Serialize(plan, RuntimeJsonContext.Default.WorkPlanDraft);
        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteString("planVersion", plan.PlanVersion);
            writer.WriteString("goal", plan.Goal);
            writer.WriteNumber("stepCount", plan.Steps.Length);
            writer.WritePropertyName("plan");
            using (var planDoc = JsonDocument.Parse(planJson))
            {
                planDoc.RootElement.WriteTo(writer);
            }

            writer.WriteString(
                "message",
                "Plan accepted. Runtime will persist it and wait for explicit user confirmation before dispatch.");
            writer.WriteBoolean("requiresConfirmation", true);
            writer.WriteEndObject();
        });
        return new ToolResult(invocation.CallId, invocation.ToolId, true, output);
    }

    private async Task<ToolResult> DispatchStepAsync(
        ToolInvocation invocation,
        JsonElement arguments,
        CancellationToken ct)
    {
        if (!TryReadPositiveInt(arguments, "stepIndex", out var stepIndex, out var indexError)
            || !TryReadPositiveInt(arguments, "subStepIndex", out var subStepIndex, out indexError))
        {
            return Failure(invocation, "invalid_arguments", indexError ?? "Invalid step indices.");
        }

        var instruction = GetOptionalString(arguments, "instruction");
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return Failure(invocation, "invalid_arguments", "instruction is required.");
        }

        if (_planLookup is null)
        {
            return Failure(
                invocation,
                "plan_lookup_unavailable",
                "dispatch_step requires a live Work session plan lookup.");
        }

        var view = await _planLookup(invocation.SessionId, ct).ConfigureAwait(false);
        if (view is null || string.IsNullOrWhiteSpace(view.PlanJson))
        {
            return Failure(invocation, "plan_not_set", "No published work plan for this session. Call mcli_work_set_plan first.");
        }

        if (string.Equals(view.Status, "WaitingForPlanConfirmation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(view.Status, "Planning", StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                invocation,
                "plan_not_confirmed",
                "Plan is not confirmed yet. Wait for user confirmation before dispatch_step.");
        }

        WorkPlanDraft? plan;
        try
        {
            plan = JsonSerializer.Deserialize(view.PlanJson, RuntimeJsonContext.Default.WorkPlanDraft);
        }
        catch (JsonException ex)
        {
            return Failure(invocation, "invalid_plan", ex.Message);
        }

        if (plan is null)
        {
            return Failure(invocation, "invalid_plan", "Published plan JSON could not be parsed.");
        }

        if (!WorkPlanToolCodec.TryResolveDispatchTarget(
                plan,
                stepIndex,
                subStepIndex,
                out var step,
                out var resolveError,
                out var resolveMessage)
            || step is null)
        {
            return Failure(
                invocation,
                resolveError ?? "step_not_found",
                resolveMessage ?? "Step not found.");
        }

        var trimmedInstruction = instruction.Trim();
        if (_dispatchRunner is null)
        {
            var accepted = ToolJson.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("ok", true);
                writer.WriteBoolean("executed", false);
                writer.WriteString("planVersion", plan.PlanVersion);
                writer.WriteNumber("stepIndex", stepIndex);
                writer.WriteNumber("subStepIndex", subStepIndex);
                writer.WriteString("subStepId", step.SubStepId);
                writer.WriteString("targetAgentId", step.TargetAgentId);
                writer.WriteString("goal", step.Goal);
                writer.WriteString("instruction", trimmedInstruction);
                writer.WriteString(
                    "message",
                    "Dispatch validated. Runtime schedules the matched confirmed plan step.");
                writer.WriteEndObject();
            });
            return new ToolResult(invocation.CallId, invocation.ToolId, true, accepted);
        }

        var dispatchResult = await _dispatchRunner(
                new WorkDispatchStepRequest(
                    invocation.SessionId,
                    invocation.RunId,
                    invocation.AgentId,
                    invocation.CallId,
                    plan.PlanVersion,
                    step.SubStepId,
                    step.TargetAgentId,
                    step.Goal ?? string.Empty,
                    trimmedInstruction,
                    stepIndex,
                    subStepIndex,
                    invocation.InvocationId,
                    invocation.ParentInvocationId,
                    invocation.WorkStepId),
                ct)
            .ConfigureAwait(false);

        if (!dispatchResult.Executed)
        {
            return Failure(
                invocation,
                dispatchResult.ErrorCode ?? "dispatch_failed",
                dispatchResult.ErrorMessage ?? "Dispatch runner failed to execute the step.");
        }

        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteBoolean("executed", true);
            writer.WriteString("planVersion", plan.PlanVersion);
            writer.WriteNumber("stepIndex", stepIndex);
            writer.WriteNumber("subStepIndex", subStepIndex);
            writer.WriteString("subStepId", step.SubStepId);
            writer.WriteString("targetAgentId", step.TargetAgentId);
            writer.WriteString("goal", step.Goal);
            writer.WriteString("instruction", trimmedInstruction);
            if (!string.IsNullOrWhiteSpace(dispatchResult.ChildInvocationId))
            {
                writer.WriteString("childInvocationId", dispatchResult.ChildInvocationId);
            }

            if (!string.IsNullOrWhiteSpace(dispatchResult.OutputText))
            {
                writer.WriteString("outputText", dispatchResult.OutputText);
            }

            writer.WriteString(
                "message",
                "Dispatch executed. Child agent completed under orchestrator scheduling.");
            writer.WriteEndObject();
        });
        return new ToolResult(invocation.CallId, invocation.ToolId, true, output);
    }

    private static ToolResult Failure(ToolInvocation invocation, string errorCode, string message)
    {
        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", false);
            writer.WriteString("error", errorCode);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        });
        return new ToolResult(invocation.CallId, invocation.ToolId, false, output, errorCode);
    }

    private static string? GetOptionalString(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Tool argument '{name}' must be a string.");
        }

        return value.GetString();
    }

    private static bool TryReadPositiveInt(
        JsonElement arguments,
        string name,
        out int value,
        out string? error)
    {
        value = 0;
        error = null;
        if (!arguments.TryGetProperty(name, out var element))
        {
            error = $"{name} is required.";
            return false;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value) && value >= 1)
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String
            && int.TryParse(element.GetString(), out value)
            && value >= 1)
        {
            return true;
        }

        error = $"{name} must be an integer >= 1.";
        return false;
    }
}

/// <summary>Session plan snapshot for dispatch_step validation.</summary>
public sealed record WorkSessionPlanView(string PlanJson, string Status);
