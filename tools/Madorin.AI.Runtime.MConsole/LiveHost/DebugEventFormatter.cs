using System.Text.Json;

using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.MConsole.LiveHost;

/// <summary>一段事件要落到哪个可见区域。</summary>
public enum DebugOutputChannel
{
    None,
    Assistant,
    Reasoning,
    Notice,
}

/// <summary>一条 Runtime 事件的展示结果。正文进输出区，摘要进事件区。</summary>
public readonly record struct DebugEventView(
    string EventLine,
    DebugOutputChannel Channel,
    string? Text,
    bool Terminal,
    string? SessionId);

/// <summary>按 CLI 使用的同一套事件类型，把事件拆成输出文本和事件摘要。</summary>
public static class DebugEventFormatter
{
    public static DebugEventView Format(RuntimeEventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var payload = envelope.Payload;
        var summary = Summarize(payload);
        var line = string.IsNullOrEmpty(summary)
            ? $"{envelope.Timestamp:HH:mm:ss} gsn={envelope.Gsn} {envelope.MessageType}"
            : $"{envelope.Timestamp:HH:mm:ss} gsn={envelope.Gsn} {envelope.MessageType} {summary}";
        var (channel, text) = Project(envelope.MessageType, payload);
        var sessionId = envelope.MessageType == MessageTypes.RunAccepted
            ? ReadString(payload, "sessionId")
            : null;
        var terminal = envelope.MessageType is MessageTypes.RunCompleted
            or MessageTypes.RunFailed
            or MessageTypes.RunCancelled;
        return new DebugEventView(line, channel, text, terminal, sessionId);
    }

    private static (DebugOutputChannel Channel, string? Text) Project(
        string messageType,
        JsonElement payload) => messageType switch
    {
        MessageTypes.TextDelta => (DebugOutputChannel.Assistant, ReadString(payload, "delta")),
        MessageTypes.ReasoningDelta => (DebugOutputChannel.Reasoning, ReadString(payload, "delta")),
        MessageTypes.RunFailed => (DebugOutputChannel.Notice, "Run failed: " + ReadError(payload)),
        MessageTypes.RunCancelled => (DebugOutputChannel.Notice, "Run cancelled."),
        MessageTypes.ExpertPlanRestored => (DebugOutputChannel.Notice, DescribeRestoredPlan(payload)),
        MessageTypes.RunCompleted => (DebugOutputChannel.Notice, "[完成]"),
        _ => (DebugOutputChannel.None, null),
    };

    private static string Summarize(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var parts = new List<string>(4);
        Add(parts, payload, "name");
        Add(parts, payload, "status");
        Add(parts, payload, "phase");
        Add(parts, payload, "currentStep");
        Add(parts, payload, "goal");
        if (payload.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            Add(parts, error, "code");
            Add(parts, error, "message");
        }

        if (payload.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.String)
        {
            var text = delta.GetString() ?? string.Empty;
            parts.Add($"+{text.Length} \"{Snippet(text)}\"");
        }

        return string.Join(' ', parts);
    }

    private static void Add(List<string> parts, JsonElement payload, string name)
    {
        var value = ReadString(payload, name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(value);
        }
    }

    private static string DescribeRestoredPlan(JsonElement payload)
    {
        var goal = ReadString(payload, "goal");
        var stepCount = ReadInt(payload, "stepCount");
        var steps = stepCount is null ? string.Empty : $"（{stepCount} 步）";
        return string.IsNullOrWhiteSpace(goal)
            ? $"[已恢复未完成计划]{steps}"
            : $"[已恢复未完成计划]{steps} {goal}";
    }

    private static string ReadError(JsonElement payload)
    {
        if (payload.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var message = ReadString(error, "message");
            if (!string.IsNullOrWhiteSpace(message))
            {
                return message;
            }
        }

        return "unknown error";
    }

    private static string? ReadString(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    private static string Snippet(string text)
    {
        var compact = text.ReplaceLineEndings(" ");
        return compact.Length <= 24 ? compact : compact[..24];
    }
}
