using System.Text.Json;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin;

/// <summary>Executes Meeting-mode orchestration tools.</summary>
public sealed class BuiltinMeetingToolExecutor(
    IMeetingModeToolStore? meetingStore,
    string workspaceRoot) : IToolExecutor
{
    private readonly IMeetingModeToolStore? _meetingStore = meetingStore;
    private readonly string _workspaceRoot = workspaceRoot
        ?? throw new ArgumentNullException(nameof(workspaceRoot));

    public bool CanExecute(string toolId) =>
        toolId is BuiltinToolRegistry.MeetingOutputSummaryToolId
            or BuiltinToolRegistry.MeetingConfirmEndToolId
            or BuiltinToolRegistry.MeetingListAttachmentsToolId
            or BuiltinToolRegistry.AskUserToolId
            or BuiltinToolRegistry.CheckPendingUserInputToolId;

    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!CanExecute(invocation.ToolId))
        {
            return Failure(invocation, "The meeting executor cannot execute this tool.");
        }

        if (_meetingStore is null
            && invocation.ToolId is not BuiltinToolRegistry.MeetingListAttachmentsToolId)
        {
            return Failure(invocation, "Meeting tool store is not available in this runtime.");
        }

        try
        {
            using var arguments = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(invocation.ArgumentsJson)
                    ? "{}"
                    : invocation.ArgumentsJson);
            if (arguments.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Tool arguments must be a JSON object.");
            }

            return invocation.ToolId switch
            {
                BuiltinToolRegistry.MeetingListAttachmentsToolId =>
                    ListAttachments(invocation),
                BuiltinToolRegistry.MeetingOutputSummaryToolId =>
                    await OutputSummaryAsync(invocation, arguments.RootElement, ct).ConfigureAwait(false),
                BuiltinToolRegistry.MeetingConfirmEndToolId =>
                    await ConfirmEndAsync(invocation, ct).ConfigureAwait(false),
                BuiltinToolRegistry.AskUserToolId =>
                    await AskUserAsync(invocation, arguments.RootElement, ct).ConfigureAwait(false),
                BuiltinToolRegistry.CheckPendingUserInputToolId =>
                    await CheckPendingAsync(invocation, ct).ConfigureAwait(false),
                _ => Failure(invocation, "The meeting tool is not implemented.")
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException
            or ArgumentException
            or InvalidOperationException
            or IOException
            or KeyNotFoundException)
        {
            return Failure(invocation, ex.Message);
        }
    }

    private ToolResult ListAttachments(ToolInvocation invocation)
    {
        var attachmentRoot = Path.Combine(
            _workspaceRoot,
            ".madorin",
            "meetings",
            invocation.SessionId,
            "attachments");
        Directory.CreateDirectory(attachmentRoot);
        var files = Directory.EnumerateFiles(attachmentRoot, "*", SearchOption.AllDirectories)
            .Select(path => new
            {
                path,
                relativePath = Path.GetRelativePath(attachmentRoot, path).Replace('\\', '/'),
                length = new FileInfo(path).Length
            })
            .OrderBy(item => item.relativePath, StringComparer.Ordinal)
            .ToArray();

        var json = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteString("attachmentRoot", attachmentRoot);
            writer.WritePropertyName("attachments");
            writer.WriteStartArray();
            foreach (var file in files)
            {
                writer.WriteStartObject();
                writer.WriteString("relativePath", file.relativePath);
                writer.WriteNumber("byteLength", file.length);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteBoolean("empty", files.Length == 0);
            writer.WriteEndObject();
        });

        return Success(invocation, json);
    }

    private async Task<ToolResult> OutputSummaryAsync(
        ToolInvocation invocation,
        JsonElement arguments,
        CancellationToken ct)
    {
        var summary = arguments.TryGetProperty("summary", out var summaryElement)
            ? summaryElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(summary))
        {
            return Failure(invocation, "summary is required.");
        }

        var summaryDir = Path.Combine(
            _workspaceRoot,
            ".madorin",
            "meetings",
            invocation.SessionId);
        Directory.CreateDirectory(summaryDir);
        var summaryPath = Path.Combine(summaryDir, "final-summary.md");
        await File.WriteAllTextAsync(summaryPath, summary, ct).ConfigureAwait(false);

        var summaryMessageId = $"meeting-tool-summary:{invocation.SessionId}:{invocation.CallId}";
        var summaryInvocationId = string.IsNullOrWhiteSpace(invocation.InvocationId)
            ? $"meeting-tool-invocation:{invocation.SessionId}:{invocation.CallId}"
            : invocation.InvocationId;
        await _meetingStore!.PersistFinalSummaryMessageAsync(
            invocation.SessionId,
            invocation.RunId,
            summaryInvocationId,
            invocation.AgentId,
            summary,
            summaryMessageId,
            ct).ConfigureAwait(false);

        var json = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteString("summaryMessageId", summaryMessageId);
            writer.WriteString("summaryPath", summaryPath);
            writer.WriteBoolean("canonicalHistoryWritten", true);
            writer.WriteString(
                "next",
                "Call mcli_check_pending_user_input, then mcli_ask_user to confirm ending, or mcli_meeting_confirm_end after explicit approval.");
            writer.WriteEndObject();
        });
        return Success(invocation, json);
    }

    private async Task<ToolResult> ConfirmEndAsync(
        ToolInvocation invocation,
        CancellationToken ct)
    {
        var state = await _meetingStore!.GetEndConfirmationStateAsync(invocation.SessionId, ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(state.FinalSummaryMessageId))
        {
            return Failure(
                invocation,
                "No meeting summary is available. Call mcli_meeting_output_summary first.");
        }

        await _meetingStore.MarkEndConfirmedAsync(
            invocation.SessionId,
            state.FinalSummaryMessageId,
            ct).ConfigureAwait(false);

        var json = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteBoolean("endConfirmed", true);
            writer.WriteString("finalSummaryMessageId", state.FinalSummaryMessageId);
            writer.WriteEndObject();
        });
        return Success(invocation, json);
    }

    private static Task<ToolResult> AskUserAsync(
        ToolInvocation invocation,
        JsonElement arguments,
        CancellationToken ct)
    {
        _ = ct;
        var question = arguments.TryGetProperty("question", out var questionElement)
            ? questionElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(question))
        {
            return Task.FromResult(Failure(invocation, "question is required."));
        }

        // Do not enqueue into the interrupt queue. The meeting tool loop blocks on HITL
        // and rewrites this result with the user answer before the model continues.
        var requestId = $"ask-user:{invocation.SessionId}:{invocation.CallId}";
        var json = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteBoolean("waitingForUser", true);
            writer.WriteString("requestId", requestId);
            writer.WriteString("question", question);
            writer.WriteString(
                "message",
                "Waiting for the user to answer. The runtime will resume this invocation after HITL.");
            writer.WriteEndObject();
        });
        return Task.FromResult(Success(invocation, json));
    }

    private async Task<ToolResult> CheckPendingAsync(
        ToolInvocation invocation,
        CancellationToken ct)
    {
        var pending = await _meetingStore!.PeekPendingInputsAsync(invocation.SessionId, ct)
            .ConfigureAwait(false);
        var json = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteBoolean("hasPending", pending.Count > 0);
            writer.WritePropertyName("pending");
            writer.WriteStartArray();
            foreach (var item in pending)
            {
                writer.WriteStartObject();
                writer.WriteString("requestId", item.RequestId);
                writer.WriteString("enqueuedAt", item.EnqueuedAt.ToString("O"));
                writer.WriteString("contentJson", item.ContentJson);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString(
                "message",
                pending.Count == 0
                    ? "没有未处理的用户插话。"
                    : "存在未处理的用户插话，请先处理后再继续。");
            writer.WriteEndObject();
        });
        return Success(invocation, json);
    }

    private static ToolResult Success(ToolInvocation invocation, string resultJson) =>
        new(invocation.CallId, invocation.ToolId, true, resultJson);

    private static ToolResult Failure(ToolInvocation invocation, string message) =>
        new(invocation.CallId, invocation.ToolId, false, Error: message);
}
