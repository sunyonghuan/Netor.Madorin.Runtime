using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;

internal static class SmokeTurns
{
    public static ToolGrant CreateGrantFromApproval(string workspace, ApprovalRequest request)
    {
        var grantId = "host-smoke-" + Guid.NewGuid().ToString("N");
        return new ToolGrant(
            grantId,
            request.RunId,
            workspace,
            [workspace],
            [workspace],
            true,
            true,
            true,
            ["dotnet"],
            false,
            new NetworkPolicy(true, []),
            DateTimeOffset.UtcNow.AddHours(2),
            false,
            [],
            AllowedToolIds: [request.ToolId],
            AllowedCallIds: [request.CallId],
            MaximumRisk: request.Risk,
            AgentId: request.AgentId,
            RootGrantId: grantId,
            ApprovalRequestId: request.ApprovalRequestId);
    }

    public static async Task<TurnCapture> CaptureAsync(
        RuntimeClient client,
        HostProviderConfig provider,
        NextTurnSelection selection,
        string prompt,
        string? sessionId,
        long fromGsn,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        string runId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            runId = await client.StartNewSessionRunAsync(
                new NewSessionRunRequest(
                    Guid.NewGuid().ToString("N"),
                    Guid.NewGuid().ToString("N"),
                    RuntimeMode.Expert,
                    selection,
                    [new TextContentBlock(prompt)],
                    ProviderConfigs: [provider]),
                cts.Token);
        }
        else
        {
            runId = await client.StartExistingSessionRunAsync(
                new ExistingSessionRunRequest(
                    sessionId,
                    Guid.NewGuid().ToString("N"),
                    selection,
                    [new TextContentBlock(prompt)],
                    ProviderConfigs: [provider]),
                cts.Token);
        }

        var text = new StringBuilder();
        var tools = new List<string>();
        var failed = new List<string>();
        string? capturedSession = sessionId;
        string? terminal = null;
        var lastGsn = fromGsn;
        await foreach (var envelope in client.ReadEventsAsync(fromGsn, cts.Token))
        {
            if (envelope.Gsn > lastGsn)
            {
                await client.AcknowledgeEventsAsync(envelope.Gsn, cts.Token);
                lastGsn = envelope.Gsn;
            }

            if (!string.Equals(envelope.RunId, runId, StringComparison.Ordinal))
            {
                continue;
            }

            CaptureEvent(envelope, text, tools, failed, ref capturedSession, ref terminal);
            if (terminal is not null)
            {
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(text.ToString()) && !string.IsNullOrWhiteSpace(runId))
        {
            var run = await client.QueryRunAsync(runId, cts.Token);
            capturedSession ??= run.SessionId;
            if (!string.IsNullOrWhiteSpace(run.TerminalText))
            {
                text.Append(run.TerminalText);
            }
        }

        return new TurnCapture(
            capturedSession ?? string.Empty,
            text.ToString(),
            terminal ?? "missing-terminal",
            lastGsn,
            tools,
            failed);
    }

    public static string Preview(TurnCapture turn)
    {
        var text = turn.Text.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        if (text.Length > 140)
        {
            text = text[..140] + "...";
        }

        return $"terminal={turn.Terminal}; tools={string.Join(',', turn.ToolIds)}; failed={string.Join(',', turn.FailedTools)}; text={text}";
    }

    private static void CaptureEvent(
        RuntimeEventEnvelope envelope,
        StringBuilder text,
        List<string> tools,
        List<string> failed,
        ref string? sessionId,
        ref string? terminal)
    {
        if (envelope.MessageType == MessageTypes.RunAccepted)
        {
            var accepted = envelope.Payload.Deserialize(RuntimeJsonContext.Default.RunAcceptedEvent);
            sessionId = accepted?.SessionId ?? sessionId;
            return;
        }

        if (envelope.MessageType == MessageTypes.TextDelta)
        {
            var delta = envelope.Payload.Deserialize(RuntimeJsonContext.Default.TextDeltaEvent);
            if (!string.IsNullOrEmpty(delta?.Delta))
            {
                text.Append(delta.Delta);
            }

            return;
        }

        if (envelope.MessageType == MessageTypes.ToolCallCompleted)
        {
            if (envelope.Payload.TryGetProperty("toolId", out var toolId)
                && toolId.ValueKind == JsonValueKind.String)
            {
                var id = toolId.GetString()!;
                tools.Add(id);
                var failedByFlag = envelope.Payload.TryGetProperty("success", out var success)
                    && success.ValueKind == JsonValueKind.False;
                var failedByStatus = envelope.Payload.TryGetProperty("status", out var status)
                    && status.ValueKind == JsonValueKind.String
                    && status.GetString() is "Failed" or "Cancelled";
                if (failedByFlag || failedByStatus)
                {
                    failed.Add(id);
                }
            }

            return;
        }

        if (envelope.MessageType is MessageTypes.RunCompleted
            or MessageTypes.RunFailed
            or MessageTypes.RunCancelled)
        {
            terminal = envelope.MessageType;
        }
    }
}
