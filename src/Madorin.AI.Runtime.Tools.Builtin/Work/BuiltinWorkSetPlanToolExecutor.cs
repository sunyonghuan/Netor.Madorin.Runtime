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

    public BuiltinWorkSetPlanToolExecutor(string? defaultTargetAgentId = null)
    {
        _defaultTargetAgentId = defaultTargetAgentId;
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
