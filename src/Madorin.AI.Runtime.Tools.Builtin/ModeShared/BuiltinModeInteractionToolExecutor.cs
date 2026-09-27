using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin.ModeShared;

/// <summary>
/// Shared Work/Meeting interaction tools: ask_user + check_pending_user_input.
/// Work pending queue is injected; ask_user blocks on host approval channel when available.
/// </summary>
public sealed class BuiltinModeInteractionToolExecutor : IToolExecutor
{
    private readonly Func<string, string, CancellationToken, Task<WorkPendingInputsView>>? _consumePending;
    private readonly Func<AskUserToolRequest, CancellationToken, Task<AskUserToolResponse>>? _askUser;

    public BuiltinModeInteractionToolExecutor(
        Func<string, string, CancellationToken, Task<WorkPendingInputsView>>? consumePending = null,
        Func<AskUserToolRequest, CancellationToken, Task<AskUserToolResponse>>? askUser = null)
    {
        _consumePending = consumePending;
        _askUser = askUser;
    }

    public bool CanExecute(string toolId) =>
        toolId is BuiltinToolRegistry.AskUserToolId
            or BuiltinToolRegistry.CheckPendingUserInputToolId;

    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ct.ThrowIfCancellationRequested();
        if (!CanExecute(invocation.ToolId))
        {
            return Failure(invocation, "unsupported_tool", "The mode interaction executor cannot execute this tool.");
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
                BuiltinToolRegistry.CheckPendingUserInputToolId => await CheckPendingAsync(
                    invocation,
                    ct).ConfigureAwait(false),
                BuiltinToolRegistry.AskUserToolId => await AskUserAsync(
                    invocation,
                    document.RootElement,
                    ct).ConfigureAwait(false),
                _ => Failure(invocation, "unsupported_tool", "The mode interaction tool is not implemented.")
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

    private async Task<ToolResult> CheckPendingAsync(
        ToolInvocation invocation,
        CancellationToken ct)
    {
        if (_consumePending is null)
        {
            return Failure(
                invocation,
                "pending_queue_unavailable",
                "check_pending_user_input requires a live Work pending-input queue.");
        }

        var view = await _consumePending(invocation.SessionId, invocation.RunId, ct)
            .ConfigureAwait(false);
        var emptyMessage = view.EmptyMessage
            ?? (view.Consumed ? "没有新的用户输入" : "没有未处理的用户插话");
        var pendingMessage = view.PendingMessage
            ?? (view.Consumed
                ? "已消费未读用户插话，请先处理这些输入再继续规划或提问。"
                : "存在未处理的用户插话，请先处理插话后再继续或提问。");
        if (view.Texts.Count == 0)
        {
            var empty = ToolJson.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("ok", true);
                writer.WriteBoolean("hasPending", false);
                writer.WriteBoolean("consumed", view.Consumed);
                writer.WriteStartArray("inputs");
                writer.WriteEndArray();
                writer.WriteString("message", emptyMessage);
                writer.WriteEndObject();
            });
            return new ToolResult(invocation.CallId, invocation.ToolId, true, empty);
        }

        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteBoolean("hasPending", true);
            writer.WriteBoolean("consumed", view.Consumed);
            writer.WriteStartArray("inputs");
            foreach (var text in view.Texts)
            {
                writer.WriteStringValue(text);
            }

            writer.WriteEndArray();
            writer.WriteNumber("count", view.Texts.Count);
            writer.WriteString("message", pendingMessage);
            writer.WriteEndObject();
        });
        return new ToolResult(invocation.CallId, invocation.ToolId, true, output);
    }

    private async Task<ToolResult> AskUserAsync(
        ToolInvocation invocation,
        JsonElement arguments,
        CancellationToken ct)
    {
        if (!arguments.TryGetProperty("question", out var questionElement)
            || questionElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(questionElement.GetString()))
        {
            return Failure(invocation, "invalid_arguments", "question is required.");
        }

        var question = questionElement.GetString()!.Trim();
        if (_askUser is null)
        {
            return Failure(
                invocation,
                "ask_user_unavailable",
                "ask_user requires a host approval/HITL channel.");
        }

        var response = await _askUser(
                new AskUserToolRequest(
                    invocation.SessionId,
                    invocation.RunId,
                    invocation.AgentId,
                    invocation.CallId,
                    question,
                    invocation.InvocationId,
                    invocation.WorkStepId,
                    invocation.PlanVersion),
                ct)
            .ConfigureAwait(false);

        if (!response.Answered)
        {
            return Failure(
                invocation,
                response.ErrorCode ?? "ask_user_rejected",
                response.ErrorMessage ?? "User did not answer.");
        }

        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteBoolean("answered", true);
            writer.WriteString("question", question);
            writer.WriteString("answer", response.Answer ?? string.Empty);
            writer.WriteString("message", "用户已回复。");
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

/// <summary>
/// Pending-input snapshot for check_pending_user_input.
/// Work sets <see cref="Consumed"/> true (FIFO consume); Meeting peeks with false.
/// </summary>
public sealed record WorkPendingInputsView(
    IReadOnlyList<string> Texts,
    bool Consumed = true,
    string? EmptyMessage = null,
    string? PendingMessage = null);

/// <summary>ask_user request forwarded to the host approval channel.</summary>
public sealed record AskUserToolRequest(
    string SessionId,
    string RunId,
    string AgentId,
    string CallId,
    string Question,
    string? InvocationId = null,
    string? WorkStepId = null,
    string? PlanVersion = null);

/// <summary>ask_user host response.</summary>
public sealed record AskUserToolResponse(
    bool Answered,
    string? Answer = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);
