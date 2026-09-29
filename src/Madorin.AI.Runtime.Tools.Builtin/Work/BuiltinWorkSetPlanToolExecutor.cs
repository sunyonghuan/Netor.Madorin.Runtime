using System.Text.Json;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin.Work;

/// <summary>
/// 校验 mcli_work_set_plan 并回传计划。不落盘，由 Work 编排器发布 work.plan.updated。
/// </summary>
public sealed class BuiltinWorkSetPlanToolExecutor : IToolExecutor
{
    private readonly string? _defaultTargetAgentId;
    private readonly int? _maxStepsPerRun;

    public BuiltinWorkSetPlanToolExecutor(string? defaultTargetAgentId = null, int? maxStepsPerRun = null)
    {
        if (maxStepsPerRun is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxStepsPerRun));
        }

        _defaultTargetAgentId = defaultTargetAgentId;
        _maxStepsPerRun = maxStepsPerRun;
    }

    public bool CanExecute(string toolId) =>
        string.Equals(toolId, BuiltinToolRegistry.WorkSetPlanToolId, StringComparison.Ordinal);

    public Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ct.ThrowIfCancellationRequested();
        if (!CanExecute(invocation.ToolId))
        {
            return Task.FromResult(Failure(
                invocation,
                "unsupported_tool",
                "The work executor cannot execute this tool."));
        }

        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(invocation.ArgumentsJson)
                    ? "{}"
                    : invocation.ArgumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Task.FromResult(Failure(
                    invocation,
                    "invalid_arguments",
                    "Tool arguments must be a JSON object."));
            }

            return Task.FromResult(SetPlan(invocation, document.RootElement));
        }
        catch (JsonException ex)
        {
            return Task.FromResult(Failure(invocation, "invalid_arguments", ex.Message));
        }
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

        if (_maxStepsPerRun is int maxSteps && plan.Steps.Length > maxSteps)
        {
            return Failure(
                invocation,
                "too_many_steps",
                $"Plan has {plan.Steps.Length} steps which exceeds MaxStepsPerRun={maxSteps}. "
                + "One plan step is one agent dispatch. Put each deliverable's create/verify gates in that step's goal. "
                + "Ten documents are about ten to twelve steps, not document-count times gate-count. "
                + "Call mcli_work_set_plan again with the collapsed plan.");
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
                "Plan accepted. One step is one agent dispatch; create/verify gates stay inside the step goal. Runtime is dispatching the steps now. Do not ask the user to confirm again.");
            writer.WriteBoolean("requiresConfirmation", false);
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
        return new ToolResult(invocation.CallId, invocation.ToolId, false, output, message);
    }
}
