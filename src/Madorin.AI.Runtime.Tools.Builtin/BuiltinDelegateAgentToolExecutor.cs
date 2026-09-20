using System.Text.Json;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin;

/// <summary>
/// Validates agent-delegation tool arguments and returns structured metadata.
/// Actual sub-agent invocation stays in the mode orchestrator (Work/Meeting/Expert).
/// </summary>
public sealed class BuiltinDelegateAgentToolExecutor : IToolExecutor
{
    public bool CanExecute(string toolId) =>
        string.Equals(toolId, BuiltinToolRegistry.DelegateToAgentToolId, StringComparison.Ordinal);

    public Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ct.ThrowIfCancellationRequested();

        if (!CanExecute(invocation.ToolId))
        {
            throw new InvalidOperationException("The delegate agent executor cannot execute this tool.");
        }

        try
        {
            using var argsDoc = JsonDocument.Parse(invocation.ArgumentsJson);
            var root = argsDoc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Task.FromResult(CreateErrorResult(invocation, "Tool arguments must be a JSON object."));
            }

            if (!root.TryGetProperty("agentName", out var agentNameElement)
                || agentNameElement.ValueKind != JsonValueKind.String)
            {
                return Task.FromResult(CreateErrorResult(invocation, "Missing or invalid 'agentName' parameter."));
            }

            if (!root.TryGetProperty("task", out var taskElement)
                || taskElement.ValueKind != JsonValueKind.String)
            {
                return Task.FromResult(CreateErrorResult(invocation, "Missing or invalid 'task' parameter."));
            }

            var agentName = agentNameElement.GetString() ?? string.Empty;
            var task = taskElement.GetString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(agentName))
            {
                return Task.FromResult(CreateErrorResult(invocation, "Agent name cannot be empty."));
            }

            if (string.IsNullOrWhiteSpace(task))
            {
                return Task.FromResult(CreateErrorResult(invocation, "Task description cannot be empty."));
            }

            var trimmedName = agentName.Trim();
            var trimmedTask = task.Trim();
            var outputJson = ToolJson.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("delegated", true);
                writer.WriteString("agentName", trimmedName);
                writer.WriteString("task", trimmedTask);
                writer.WriteString(
                    "message",
                    $"Task delegation request accepted. The orchestrator will route to agent '{trimmedName}'.");
                writer.WriteEndObject();
            });

            return Task.FromResult(new ToolResult(invocation.CallId, invocation.ToolId, true, outputJson));
        }
        catch (JsonException ex)
        {
            return Task.FromResult(CreateErrorResult(invocation, ex.Message));
        }
    }

    private static ToolResult CreateErrorResult(ToolInvocation invocation, string errorMessage)
    {
        var outputJson = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("delegated", false);
            writer.WriteString("message", errorMessage);
            writer.WriteEndObject();
        });

        return new ToolResult(invocation.CallId, invocation.ToolId, false, outputJson, errorMessage);
    }
}
