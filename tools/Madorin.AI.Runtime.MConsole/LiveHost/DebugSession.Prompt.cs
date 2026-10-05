using Madorin.AI.Runtime.Client;

namespace Madorin.AI.Runtime.MConsole.LiveHost;

public static partial class DebugSession
{
    private static async Task RunPromptAsync(
        DebugTurnClient turn,
        DebugTranscript transcript,
        DebugEventPump pump,
        DebugViewport viewport,
        string input,
        CancellationToken cancellationToken)
    {
        transcript.Append(DebugOutputChannel.Notice, "你> " + input);
        var outputMark = transcript.OutputText.Length;
        var eventMark = transcript.Events.Count;
        PrepareViewport(viewport);
        transcript.Status = DescribeStatus(turn) + " 运行=提交中";
        Refresh(transcript, viewport);

        using var runCancel = new CancellationTokenSource();
        DebugInterrupt.Bind(runCancel);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runCancel.Token);
        string? runId = null;
        try
        {
            runId = await turn.SendAsync(input, linked.Token);
            transcript.Status = DescribeStatus(turn) + " 运行=" + runId;
            Refresh(transcript, viewport);
            await pump.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (runCancel.IsCancellationRequested && runId is not null)
        {
            transcript.Append(DebugOutputChannel.Notice, "正在取消本轮。");
            Refresh(transcript, viewport);
            await turn.CancelAsync(runId, CancellationToken.None);
            try
            {
                await pump.WaitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
            }
            catch (TimeoutException)
            {
                transcript.Append(DebugOutputChannel.Notice, "取消后没有在 15 秒内收到结束事件。");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or RuntimeClientException or IOException)
        {
            transcript.Append(DebugOutputChannel.Notice, ex.Message);
        }
        finally
        {
            DebugInterrupt.Bind(null);
            turn.SessionId ??= pump.SessionId;
            transcript.Status = DescribeStatus(turn) + " 运行=空闲";
            Refresh(transcript, viewport);
            WriteRecord(transcript, viewport, outputMark, eventMark);
        }
    }

    private static void PrepareViewport(DebugViewport viewport)
    {
        if (Console.IsOutputRedirected)
        {
            viewport.Origin = -1;
            return;
        }

        viewport.OutputRows = DebugLiveView.OutputRows(Console.WindowHeight);
        viewport.EventRows = DebugLiveView.EventRows(Console.WindowHeight);
        viewport.Origin = DebugLiveView.Begin(viewport.OutputRows + viewport.EventRows + 3);
    }

    private static void WriteRecord(
        DebugTranscript transcript,
        DebugViewport viewport,
        int outputMark,
        int eventMark)
    {
        if (viewport.Origin >= 0)
        {
            try
            {
                Console.SetCursorPosition(0, viewport.Origin + viewport.OutputRows + viewport.EventRows + 3);
            }
            catch (ArgumentOutOfRangeException)
            {
                Console.WriteLine();
            }
        }

        var output = transcript.OutputText;
        Console.WriteLine("──── 本轮完整输出 ────");
        Console.WriteLine(output.Length >= outputMark ? output[outputMark..] : output);
        Console.WriteLine("──── 本轮事件 ────");
        var events = transcript.Events;
        for (var index = eventMark; index < events.Count; index++)
        {
            Console.WriteLine(events[index]);
        }
    }

    private static bool SetProvider(string argument, DebugTurnClient turn)
    {
        if (argument.Length == 0)
        {
            Console.WriteLine("当前厂商: " + turn.ProviderId);
            return true;
        }

        turn.ProviderId = argument;
        turn.SelectionDirty = true;
        DebugProviderInfo? known = null;
        for (var index = 0; index < turn.Config.Providers.Count; index++)
        {
            if (turn.Config.Providers[index].Name.Equals(argument, StringComparison.OrdinalIgnoreCase))
            {
                known = turn.Config.Providers[index];
                break;
            }
        }
        if (known is null)
        {
            Console.WriteLine("厂商已设为 " + argument + "。配置里没有同名厂商，调用时可能被 Runtime 拒绝。");
            return true;
        }

        if (known.Models.Count > 0
            && !known.Models.Contains(turn.ModelId, StringComparer.Ordinal))
        {
            turn.ModelId = known.Models[0];
        }

        Console.WriteLine($"厂商: {known.Name}  模型: {turn.ModelId}");
        return true;
    }

    private static bool SetModel(string argument, DebugTurnClient turn)
    {
        if (argument.Length == 0)
        {
            Console.WriteLine("当前模型: " + turn.ModelId);
            return true;
        }

        turn.ModelId = argument;
        turn.SelectionDirty = true;
        Console.WriteLine("模型: " + argument);
        return true;
    }

    private static string DescribeModels(DebugTurnClient turn)
    {
        DebugProviderInfo? provider = null;
        for (var index = 0; index < turn.Config.Providers.Count; index++)
        {
            if (turn.Config.Providers[index].Name.Equals(turn.ProviderId, StringComparison.OrdinalIgnoreCase))
            {
                provider = turn.Config.Providers[index];
                break;
            }
        }
        if (provider is null)
        {
            return "当前厂商没有模型清单。";
        }

        return provider.Models.Count == 0
            ? provider.Name + " 没有配置模型。"
            : provider.Name + ": " + string.Join(", ", provider.Models);
    }

    private static string DescribeStatus(DebugTurnClient turn) =>
        $"模式={turn.Mode} 厂商={turn.ProviderId} 模型={turn.ModelId} 会话={turn.SessionId ?? "-"}";
}
