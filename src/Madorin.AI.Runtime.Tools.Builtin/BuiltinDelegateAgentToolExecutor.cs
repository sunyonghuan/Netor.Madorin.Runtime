using System.Text.Json;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin;

/// <summary>
/// Executes agent delegation by parsing the request and returning structured metadata.
/// </summary>
/// <remarks>
/// This tool returns a lightweight response indicating the delegation intent.
/// The actual sub-agent invocation is handled by the orchestrator layer
/// (Work/Meeting mode) which has access to the full agent pool and execution context.
///
/// Design rationale:
/// - Keeps tool executor stateless and testable
/// - Avoids coupling with ModeOptions persistence layer
/// - Allows orchestrator to apply its own policies (rate limits, quotas, etc.)
/// </remarks>
public sealed class BuiltinDelegateAgentToolExecutor : IToolExecutor
{
    public bool CanExecute(string toolId) => string.Equals(
        toolId,
        BuiltinToolRegistry.DelegateToAgentToolId,
        StringComparison.Ordinal);

    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ct.ThrowIfCancellationRequested();

        if (!CanExecute(invocation.ToolId))
        {
            throw new InvalidOperationException("The delegate agent executor cannot execute this tool.");
        }

        // Parse input arguments
        using var argsDoc = JsonDocument.Parse(invocation.ArgumentsJson);
        var root = argsDoc.RootElement;

        if (!root.TryGetProperty("agentName", out var agentNameElement) ||
            agentNameElement.ValueKind != JsonValueKind.String)
        {
            return CreateErrorResult(invocation, "Missing or invalid 'agentName' parameter");
        }

        if (!root.TryGetProperty("task", out var taskElement) ||
            taskElement.ValueKind != JsonValueKind.String)
        {
            return CreateErrorResult(invocation, "Missing or invalid 'task' parameter");
        }

        var agentName = agentNameElement.GetString()!;
        var task = taskElement.GetString()!;

        if (string.IsNullOrWhiteSpace(agentName))
        {
            return CreateErrorResult(invocation, "Agent name cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(task))
        {
            return CreateErrorResult(invocation, "Task description cannot be empty");
        }

        // Return delegation metadata for orchestrator processing
        // The orchestrator will:
        // 1. Validate agentName exists in AvailableAgents
        // 2. Create sub-invocation with proper context
        // 3. Stream results back through the conversation
        var outputJson = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("delegated", true);
            writer.WriteString("agentName", agentName);
            writer.WriteString("task", task);
            writer.WriteString("message", $"Delegation request received for agent '{agentName}'");
            writer.WriteEndObject();
        });

        return new ToolResult(invocation.CallId, invocation.ToolId, true, outputJson);
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
