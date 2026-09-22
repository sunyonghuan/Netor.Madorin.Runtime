using System.Globalization;
using System.Text.Json;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin.Work;

/// <summary>
/// Work history material tools. get_recent reads the current session plan when completed;
/// chat_history / groupchat read conversation transcripts as planning materials.
/// </summary>
public sealed class BuiltinWorkHistoryToolExecutor : IToolExecutor
{
    private const int DefaultTranscriptLimit = 40;
    private const int MaxTranscriptLimit = 200;

    private readonly Func<string, CancellationToken, Task<WorkRecentPlanView?>>? _recentPlanLookup;
    private readonly Func<WorkTranscriptQuery, CancellationToken, Task<WorkTranscriptView>>? _transcriptLookup;

    public BuiltinWorkHistoryToolExecutor(
        Func<string, CancellationToken, Task<WorkRecentPlanView?>>? recentPlanLookup = null,
        Func<WorkTranscriptQuery, CancellationToken, Task<WorkTranscriptView>>? transcriptLookup = null)
    {
        _recentPlanLookup = recentPlanLookup;
        _transcriptLookup = transcriptLookup;
    }

    public bool CanExecute(string toolId) =>
        toolId is BuiltinToolRegistry.WorkGetRecentCompletedTaskPlanToolId
            or BuiltinToolRegistry.WorkLoadPlanFromChatHistoryToolId
            or BuiltinToolRegistry.WorkLoadPlanFromGroupchatToolId;

    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ct.ThrowIfCancellationRequested();
        if (!CanExecute(invocation.ToolId))
        {
            return Failure(invocation, "unsupported_tool", "The work history executor cannot execute this tool.");
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
                BuiltinToolRegistry.WorkGetRecentCompletedTaskPlanToolId => await GetRecentAsync(
                    invocation,
                    ct).ConfigureAwait(false),
                BuiltinToolRegistry.WorkLoadPlanFromChatHistoryToolId => await LoadTranscriptAsync(
                    invocation,
                    document.RootElement,
                    source: "chat_history",
                    sessionIdProperty: "sessionId",
                    requireExplicitSessionId: false,
                    ct).ConfigureAwait(false),
                BuiltinToolRegistry.WorkLoadPlanFromGroupchatToolId => await LoadTranscriptAsync(
                    invocation,
                    document.RootElement,
                    source: "groupchat",
                    sessionIdProperty: "meetingId",
                    requireExplicitSessionId: true,
                    ct).ConfigureAwait(false),
                _ => Failure(invocation, "unsupported_tool", "The work history tool is not implemented.")
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

    private async Task<ToolResult> GetRecentAsync(ToolInvocation invocation, CancellationToken ct)
    {
        if (_recentPlanLookup is null)
        {
            return Failure(
                invocation,
                "recent_plan_unavailable",
                "get_recent_completed_task_plan requires a live Work session lookup.");
        }

        var view = await _recentPlanLookup(invocation.SessionId, ct).ConfigureAwait(false);
        if (view is null || string.IsNullOrWhiteSpace(view.PlanJson))
        {
            var empty = ToolJson.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("ok", true);
                writer.WriteBoolean("found", false);
                writer.WriteString("message", "没有可复用的已完成任务计划，请直接 mcli_work_set_plan。");
                writer.WriteEndObject();
            });
            return new ToolResult(invocation.CallId, invocation.ToolId, true, empty);
        }

        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteBoolean("found", true);
            writer.WriteString("status", view.Status);
            writer.WriteString("planVersion", view.PlanVersion);
            writer.WritePropertyName("plan");
            using (var planDoc = JsonDocument.Parse(view.PlanJson))
            {
                planDoc.RootElement.WriteTo(writer);
            }

            writer.WriteString(
                "note",
                "Template/history materials only. Call mcli_work_set_plan to publish a new executable plan.");
            writer.WriteEndObject();
        });
        return new ToolResult(invocation.CallId, invocation.ToolId, true, output);
    }

    private async Task<ToolResult> LoadTranscriptAsync(
        ToolInvocation invocation,
        JsonElement arguments,
        string source,
        string sessionIdProperty,
        bool requireExplicitSessionId,
        CancellationToken ct)
    {
        if (_transcriptLookup is null)
        {
            return Failure(
                invocation,
                "transcript_unavailable",
                $"{invocation.ToolId} requires a live conversation transcript lookup.");
        }

        string? sessionId = null;
        if (arguments.TryGetProperty(sessionIdProperty, out var sessionElement)
            && sessionElement.ValueKind == JsonValueKind.String)
        {
            sessionId = sessionElement.GetString();
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            if (requireExplicitSessionId)
            {
                return Failure(
                    invocation,
                    "invalid_arguments",
                    $"{sessionIdProperty} is required.");
            }

            sessionId = invocation.SessionId;
        }

        var limit = DefaultTranscriptLimit;
        if (arguments.TryGetProperty("limit", out var limitElement))
        {
            if (limitElement.ValueKind == JsonValueKind.Number
                && limitElement.TryGetInt32(out var parsed)
                && parsed >= 1)
            {
                limit = Math.Min(parsed, MaxTranscriptLimit);
            }
            else if (limitElement.ValueKind == JsonValueKind.String
                && int.TryParse(
                    limitElement.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out parsed)
                && parsed >= 1)
            {
                limit = Math.Min(parsed, MaxTranscriptLimit);
            }
            else
            {
                return Failure(invocation, "invalid_arguments", "limit must be an integer >= 1.");
            }
        }

        WorkTranscriptView view;
        try
        {
            view = await _transcriptLookup(
                    new WorkTranscriptQuery(sessionId.Trim(), source, limit),
                    ct)
                .ConfigureAwait(false);
        }
        catch (KeyNotFoundException ex)
        {
            return Failure(invocation, "session_not_found", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Failure(invocation, "transcript_unavailable", ex.Message);
        }

        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteString("source", view.Source);
            writer.WriteString("sessionId", view.SessionId);
            writer.WriteNumber("count", view.Messages.Count);
            writer.WriteStartArray("messages");
            foreach (var message in view.Messages)
            {
                writer.WriteStartObject();
                writer.WriteString("role", message.Role);
                writer.WriteString("agentId", message.AgentId);
                writer.WriteString("text", message.Text);
                writer.WriteString("timestamp", message.Timestamp.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString(
                "note",
                "Transcript materials only. Call mcli_work_set_plan to publish an executable plan.");
            writer.WriteString(
                "message",
                view.Messages.Count == 0
                    ? "没有可读的历史消息，请直接根据当前任务 mcli_work_set_plan。"
                    : $"已加载 {view.Messages.Count} 条历史消息作为规划材料。");
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
}

/// <summary>Recent completed/current plan snapshot for history tools.</summary>
public sealed record WorkRecentPlanView(string PlanJson, string PlanVersion, string Status);

/// <summary>Transcript lookup request for chat_history / groupchat tools.</summary>
public sealed record WorkTranscriptQuery(string SessionId, string Source, int Limit);

/// <summary>One transcript message projected for planning materials.</summary>
public sealed record WorkTranscriptMessage(
    string Role,
    string AgentId,
    string Text,
    DateTimeOffset Timestamp);

/// <summary>Transcript materials returned to the model.</summary>
public sealed record WorkTranscriptView(
    string SessionId,
    string Source,
    IReadOnlyList<WorkTranscriptMessage> Messages);
