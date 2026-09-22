using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;

internal static partial class Smoke
{
    private static async Task AppendEventAsync(string path, string line, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                await file.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                await Task.Delay(50, ct);
            }
        }
    }

    private static RuntimeHostCallbacks Callbacks() => new()
    {
        ToolPermissionRequestedAsync = (request, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var grant = Grant(request.RunId!, request.AgentId, request.ToolId, request.CallId, request.ApprovalRequestId);
            File.AppendAllText(Path.Combine(_root, "permissions.jsonl"), JsonSerializer.Serialize(request) + "\n");
            return ValueTask.FromResult(new ToolPermissionResponse(request.CorrelationId ?? request.ApprovalRequestId,
                request.CallId, ToolAuthorizationDecision.Granted, grant));
        },
        ApprovalRequestedAsync = (request, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            File.AppendAllText(Path.Combine(_root, "approvals.jsonl"), JsonSerializer.Serialize(request) + "\n");
            var grant = Grant(request.RunId, request.AgentId, request.ToolId, request.CallId, request.ApprovalRequestId);
            // ask_user 约定：宿主把用户答案放在 ApprovalResponse.Reason。
            var reason = string.Equals(request.ToolId, "mcli_ask_user", StringComparison.Ordinal)
                ? "冒烟默认答复：按当前目标继续，不做额外限制。"
                : "本次冒烟测试的工作区工具授权";
            return ValueTask.FromResult(new ApprovalResponse(request.CorrelationId, request.ApprovalRequestId,
                ToolAuthorizationDecision.Granted, grant, reason));
        }
    };

    private static ToolGrant Grant(string runId, string agentId, string toolId, string callId, string approvalId) =>
        new(Guid.NewGuid().ToString("N"), runId, _workspace, [_workspace], [_workspace], true, false, false,
            ["dotnet"], false, new NetworkPolicy(true, []), DateTimeOffset.UtcNow.AddHours(3), false, [],
            AllowedToolIds: [toolId], AllowedCallIds: [callId], MaximumRisk: ToolRiskLevel.Process,
            AgentId: agentId, ApprovalRequestId: approvalId);

    private static async Task ReadEventsAsync(CancellationToken ct)
    {
        await foreach (var e in _client.ReadEventsAsync(0, ct))
        {
            Events.GetOrAdd(e.RunId, _ => []).Add(e);
            await AppendEventAsync(Path.Combine(_root, e.RunId + ".events.jsonl"),
                JsonSerializer.Serialize(e, RuntimeJsonContext.Default.RuntimeEventEnvelope), ct);
            if (e.Gsn % 64 == 0 || e.MessageType is "run.completed" or "run.failed" or "run.cancelled" or "run.interrupted")
            {
                await _client.AcknowledgeEventsAsync(e.Gsn, ct);
            }
            if (e.Payload.TryGetProperty("sessionId", out var sid) && sid.ValueKind == JsonValueKind.String)
            {
                SessionIds[e.RunId] = sid.GetString()!;
            }
            if (e.MessageType == MessageTypes.MeetingHitlRequest)
            {
                var request = e.Payload.Deserialize(RuntimeJsonContext.Default.MeetingHitlRequest)!;
                var context = string.Join("\n", (request.Context ?? []).OfType<TextContentBlock>().Select(block => block.Text));
                // 首轮门禁/继续讨论：Supplement；用户明确收口或主持人征询结束且无“先不要结束”：Approve。
                var ending = (context.Contains("可以结束会议", StringComparison.Ordinal)
                        || context.Contains("认可修订后的结论，可以结束", StringComparison.Ordinal)
                        || (context.Contains("可以结束", StringComparison.Ordinal)
                            && !context.Contains("当前没有同意结束", StringComparison.Ordinal)
                            && !context.Contains("先不要结束", StringComparison.Ordinal)))
                    && !context.Contains("先不要结束", StringComparison.Ordinal);
                var changed = context.Contains("50元", StringComparison.Ordinal);
                var response = new MeetingHitlResponse(request.ApprovalRequestId,
                    ending ? MeetingHitlAction.Approve : MeetingHitlAction.Supplement,
                    Supplement: ending ? null : changed
                        ? "老板补充确认：预算上限50元，退款按250元情景继续讨论；尚未同意结束，修订总结后请再次征询。"
                        : "老板补充：预算上限100元。请先讨论收益与退款风险，主持人总结后必须问我能否结束，现在没有批准结束。",
                    Reason: ending ? "老板已明确同意结束会议" : "测试会前补充与最终结束确认的区别");
                Save(e.RunId + ".hitl-response.json", response);
                await _client.SubmitMeetingHitlResponseAsync(response, ct);
            }
            if (_interruptMeeting && e.MessageType == "invocation.started"
                && e.Payload.TryGetProperty("snapshot", out var snapshot)
                && snapshot.TryGetProperty("role", out var role) && role.GetString() == "Participant")
            {
                _interruptMeeting = false;
                var meetingSessionId = SessionIds[e.RunId];
                const string input = "老板运行中插话：先暂停讨论，预算调整为50元，退款按增加100元评估；重新讨论后再征询我是否结束。";
                Save("MI-live-input.json", new { input, sessionId = meetingSessionId, activeRunId = e.RunId });
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    var accepted = await _client.StartExistingSessionRunAsync(new(meetingSessionId, Guid.NewGuid().ToString("N"),
                        InputOverride: [new TextContentBlock(input)]), requestTimeout.Token);
                    Save("MI-live-input-response.json", new { acceptedRunId = accepted });
                    await _client.CancelRunAsync(accepted, "结束运行中插话探测产生的额外Run", ct);
                }
                catch (Exception ex)
                {
                    Save("MI-live-input-response.json", new { type = ex.GetType().Name, ex.Message });
                }
                var cancelled = await _client.CancelRunAsync(e.RunId, "测试当前API可提供的取消后再讨论路径", ct);
                Save("MI-cancel-response.json", new { cancelled });
            }
            if (CancelOnStep.ContainsKey(e.RunId) && e.MessageType == "invocation.completed"
                && e.Payload.TryGetProperty("workStepId", out var step) && step.ValueKind == JsonValueKind.String)
            {
                CancelOnStep.TryRemove(e.RunId, out _);
                var cancelled = await _client.CancelRunAsync(e.RunId, "冒烟测试：步骤完成后用户要求中断并修改需求", ct);
                Save(e.RunId + ".cancel-response.json", new { cancelled, trigger = e });
            }
            if (e.MessageType is "run.completed" or "run.failed" or "run.cancelled" or "run.interrupted")
            {
                Ends.GetOrAdd(e.RunId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(e);
            }
        }
    }

    private static async Task<string?> TurnAsync(string label, NextTurnSelection selection, string input,
        string? session = null, bool cancelAfterStep = false)
    {
        Save(label + ".input.json", new { input, session, selection });
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] START {label}");
        string runId;
        try
        {
            runId = session is null
                ? await _client.StartNewSessionRunAsync(new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                    selection.Mode, selection, [new TextContentBlock(input)]))
                : await _client.StartExistingSessionRunAsync(new(session, Guid.NewGuid().ToString("N"),
                    InputOverride: [new TextContentBlock(input)]));
            Labels[runId] = label;
            if (cancelAfterStep) { CancelOnStep[runId] = 0; }
            Save(label + ".run.json", new { runId, session });
        }
        catch (Exception ex)
        {
            Save(label + ".submit-error.json", new { type = ex.GetType().Name, ex.Message });
            Console.WriteLine($"SUBMIT ERROR {label}: {ex.Message}");
            return session;
        }
        try
        {
            var completion = Ends.GetOrAdd(runId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            // 会议多轮点名 + reasoning 流事件量大；Work/Expert 通常远短于此。
            var timeout = Task.Delay(TimeSpan.FromMinutes(20));
            var completed = await Task.WhenAny(completion, _eventReader, timeout);
            if (completed == _eventReader) { await _eventReader; throw new IOException("事件读取循环意外结束"); }
            if (completed == timeout) { throw new TimeoutException(); }
            var end = await completion;
            var output = new StringBuilder();
            foreach (var e in Events[runId])
            {
                if (e.MessageType == "output.delta" && e.Payload.TryGetProperty("delta", out var delta))
                {
                    output.Append(delta.GetString());
                }
            }
            File.WriteAllText(Path.Combine(_root, label + ".assistant.txt"), output.ToString());
            Save(label + ".result.json", new { runId, terminal = end.MessageType, end.Payload, eventCount = Events[runId].Count });
            Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] END {label}: {end.MessageType}, events={Events[runId].Count}");
        }
        catch (TimeoutException)
        {
            await _client.CancelRunAsync(runId, "冒烟测试回合超时");
            Save(label + ".timeout.json", new { runId, timeoutSeconds = 1200 });
            Console.WriteLine($"TIMEOUT {label}");
            try { await Ends[runId].Task.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException) { Save(label + ".cancel-timeout.json", new { runId }); }
        }
        if (SessionIds.TryGetValue(runId, out var found)) { session = found; }
        if (session is not null)
        {
            Save(label + ".resume.json", await _client.ResumeSessionAsync(session));
        }
        return session;
    }
}
