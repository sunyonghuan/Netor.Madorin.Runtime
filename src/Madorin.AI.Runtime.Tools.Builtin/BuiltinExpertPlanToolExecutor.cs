using System.Text.Json;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin;

/// <summary>
/// Validates expert plan tool arguments and returns structured acceptance metadata.
/// The actual plan creation and execution is handled by ExpertModeOrchestrator.
/// </summary>
public sealed class BuiltinExpertPlanToolExecutor : IToolExecutor
{
    public bool CanExecute(string toolId) =>
        string.Equals(toolId, BuiltinToolRegistry.ExpertCreatePlanToolId, StringComparison.Ordinal)
        || string.Equals(toolId, BuiltinToolRegistry.ExpertExecuteStepToolId, StringComparison.Ordinal)
        || string.Equals(toolId, BuiltinToolRegistry.ExpertGetPlanToolId, StringComparison.Ordinal)
        || string.Equals(toolId, BuiltinToolRegistry.ExpertUpdateStepStatusToolId, StringComparison.Ordinal)
        || string.Equals(toolId, BuiltinToolRegistry.ExpertAbandonPlanToolId, StringComparison.Ordinal);

    public Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ct.ThrowIfCancellationRequested();

        if (!CanExecute(invocation.ToolId))
        {
            throw new InvalidOperationException("The expert plan executor cannot execute this tool.");
        }

        return invocation.ToolId switch
        {
            var id when string.Equals(id, BuiltinToolRegistry.ExpertCreatePlanToolId, StringComparison.Ordinal)
                => ValidateCreatePlanAsync(invocation, ct),
            var id when string.Equals(id, BuiltinToolRegistry.ExpertExecuteStepToolId, StringComparison.Ordinal)
                => ValidateExecuteStepAsync(invocation, ct),
            var id when string.Equals(id, BuiltinToolRegistry.ExpertGetPlanToolId, StringComparison.Ordinal)
                => ValidateGetPlanAsync(invocation, ct),
            var id when string.Equals(id, BuiltinToolRegistry.ExpertUpdateStepStatusToolId, StringComparison.Ordinal)
                => ValidateUpdateStepStatusAsync(invocation, ct),
            var id when string.Equals(id, BuiltinToolRegistry.ExpertAbandonPlanToolId, StringComparison.Ordinal)
                => ValidateAbandonPlanAsync(invocation, ct),
            _ => Task.FromResult(CreateErrorResult(invocation, "Unknown tool."))
        };
    }

    private static Task<ToolResult> ValidateCreatePlanAsync(
        ToolInvocation invocation,
        CancellationToken ct)
    {
        try
        {
            using var argsDoc = JsonDocument.Parse(invocation.ArgumentsJson);
            var root = argsDoc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return Task.FromResult(CreateErrorResult(invocation, "Tool arguments must be a JSON object."));
            }

            if (!root.TryGetProperty("title", out var titleElement)
                || titleElement.ValueKind != JsonValueKind.String)
            {
                return Task.FromResult(CreateErrorResult(invocation, "Missing or invalid 'title' parameter."));
            }

            if (!root.TryGetProperty("steps", out var stepsElement)
                || stepsElement.ValueKind != JsonValueKind.Array)
            {
                return Task.FromResult(CreateErrorResult(invocation, "Missing or invalid 'steps' parameter."));
            }

            var title = titleElement.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(title))
            {
                return Task.FromResult(CreateErrorResult(invocation, "Plan title cannot be empty."));
            }

            var stepCount = stepsElement.GetArrayLength();
            if (stepCount == 0)
            {
                return Task.FromResult(CreateErrorResult(invocation, "Plan must contain at least one step."));
            }

            // Validate each step has required fields
            for (var i = 0; i < stepCount; i++)
            {
                var step = stepsElement[i];
                if (!step.TryGetProperty("title", out var stepTitle)
                    || stepTitle.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(stepTitle.GetString()))
                {
                    return Task.FromResult(CreateErrorResult(invocation, $"Step {i} missing valid title."));
                }

                if (!step.TryGetProperty("description", out var stepDesc)
                    || stepDesc.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(stepDesc.GetString()))
                {
                    return Task.FromResult(CreateErrorResult(invocation, $"Step {i} missing valid description."));
                }
            }

            var outputJson = ToolJson.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("success", false);
                writer.WriteString("title", title.Trim());
                writer.WriteNumber("stepCount", stepCount);
                writer.WriteString(
                    "message",
                    $"Plan '{title.Trim()}' with {stepCount} steps accepted. Expert orchestrator will create the plan.");
                writer.WriteEndObject();
            });

            return Task.FromResult(new ToolResult(invocation.CallId, invocation.ToolId, true, outputJson));
        }
        catch (JsonException ex)
        {
            return Task.FromResult(CreateErrorResult(invocation, $"Invalid JSON: {ex.Message}"));
        }
    }

    private static Task<ToolResult> ValidateExecuteStepAsync(
        ToolInvocation invocation,
        CancellationToken ct)
    {
        try
        {
            using var argsDoc = JsonDocument.Parse(invocation.ArgumentsJson);
            var root = argsDoc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return Task.FromResult(CreateErrorResult(invocation, "Tool arguments must be a JSON object."));
            }

            if (!root.TryGetProperty("stepIndex", out var stepIndexElement)
                || stepIndexElement.ValueKind != JsonValueKind.Number)
            {
                return Task.FromResult(CreateErrorResult(invocation, "Missing or invalid 'stepIndex' parameter."));
            }

            var stepIndex = stepIndexElement.GetInt32();
            if (stepIndex < 0)
            {
                return Task.FromResult(CreateErrorResult(invocation, "Step index must be non-negative."));
            }

            var outputJson = ToolJson.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("success", false);
                writer.WriteNumber("stepIndex", stepIndex);
                writer.WriteString(
                    "message",
                    $"Step execution request for index {stepIndex} accepted. Expert orchestrator will execute the step.");
                writer.WriteEndObject();
            });

            return Task.FromResult(new ToolResult(invocation.CallId, invocation.ToolId, true, outputJson));
        }
        catch (JsonException ex)
        {
            return Task.FromResult(CreateErrorResult(invocation, $"Invalid JSON: {ex.Message}"));
        }
    }

    private static Task<ToolResult> ValidateGetPlanAsync(
        ToolInvocation invocation,
        CancellationToken ct)
    {
        // No arguments to validate - this tool takes no parameters
        var outputJson = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", false);
            writer.WriteString(
                "message",
                "Get plan request accepted. Expert orchestrator will return the active plan.");
            writer.WriteEndObject();
        });

        return Task.FromResult(new ToolResult(invocation.CallId, invocation.ToolId, true, outputJson));
    }

    private static Task<ToolResult> ValidateUpdateStepStatusAsync(
        ToolInvocation invocation,
        CancellationToken ct)
    {
        try
        {
            using var argsDoc = JsonDocument.Parse(invocation.ArgumentsJson);
            var root = argsDoc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return Task.FromResult(CreateErrorResult(invocation, "Tool arguments must be a JSON object."));
            }

            if (!root.TryGetProperty("stepIndex", out var stepIndexElement)
                || stepIndexElement.ValueKind != JsonValueKind.Number)
            {
                return Task.FromResult(CreateErrorResult(invocation, "Missing or invalid 'stepIndex' parameter."));
            }

            var stepIndex = stepIndexElement.GetInt32();
            if (stepIndex < 0)
            {
                return Task.FromResult(CreateErrorResult(invocation, "Step index must be non-negative."));
            }

            if (!root.TryGetProperty("newStatus", out var statusElement)
                || statusElement.ValueKind != JsonValueKind.String)
            {
                return Task.FromResult(CreateErrorResult(invocation, "Missing or invalid 'newStatus' parameter."));
            }

            var newStatus = statusElement.GetString();
            var validStatuses = new[] { "pending", "running", "completed", "failed", "skipped" };
            if (string.IsNullOrWhiteSpace(newStatus) || !validStatuses.Contains(newStatus, StringComparer.OrdinalIgnoreCase))
            {
                return Task.FromResult(CreateErrorResult(invocation,
                    $"Invalid status '{newStatus}'. Must be one of: {string.Join(", ", validStatuses)}."));
            }

            var outputJson = ToolJson.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("success", false);
                writer.WriteNumber("stepIndex", stepIndex);
                writer.WriteString("newStatus", newStatus);
                writer.WriteString(
                    "message",
                    $"Update step status request accepted. Expert orchestrator will update step {stepIndex} to '{newStatus}'.");
                writer.WriteEndObject();
            });

            return Task.FromResult(new ToolResult(invocation.CallId, invocation.ToolId, true, outputJson));
        }
        catch (JsonException ex)
        {
            return Task.FromResult(CreateErrorResult(invocation, $"Invalid JSON: {ex.Message}"));
        }
    }

    private static Task<ToolResult> ValidateAbandonPlanAsync(
        ToolInvocation invocation,
        CancellationToken ct)
    {
        // Optional 'reason' parameter - no strict validation needed
        var outputJson = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", false);
            writer.WriteString(
                "message",
                "Abandon plan request accepted. Expert orchestrator will cancel the active plan.");
            writer.WriteEndObject();
        });

        return Task.FromResult(new ToolResult(invocation.CallId, invocation.ToolId, true, outputJson));
    }

    private static ToolResult CreateErrorResult(ToolInvocation invocation, string message)
    {
        var json = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", false);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        });
        return new ToolResult(invocation.CallId, invocation.ToolId, false, json);
    }
}
