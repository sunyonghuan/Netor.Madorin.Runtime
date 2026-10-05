using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.MConsole.LiveHost;

public static partial class DebugSession
{
    private static async Task<int> ReadLoopAsync(
        DebugTurnClient turn,
        DebugTranscript transcript,
        DebugEventPump pump,
        DebugViewport viewport,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await Console.In.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return 0;
            }

            var command = DebugCommandParser.Parse(line);
            if (command.Kind == DebugCommandKind.Quit)
            {
                return 0;
            }

            if (command.Kind == DebugCommandKind.Prompt && command.Argument.Length > 0)
            {
                await RunPromptAsync(turn, transcript, pump, viewport, command.Argument, cancellationToken);
                Console.Write("> ");
                continue;
            }

            if (!HandleLocalCommand(command, turn, transcript))
            {
                Console.WriteLine("未知命令。输入 /help 查看可用命令。");
            }

            Console.Write("> ");
        }

        return 130;
    }

    private static bool HandleLocalCommand(
        DebugCommand command,
        DebugTurnClient turn,
        DebugTranscript transcript)
    {
        switch (command.Kind)
        {
            case DebugCommandKind.Help:
                Console.WriteLine(DebugCommandParser.HelpText);
                return true;
            case DebugCommandKind.Mode:
                return SetMode(command.Argument, turn);
            case DebugCommandKind.Provider:
                return SetProvider(command.Argument, turn);
            case DebugCommandKind.Model:
                return SetModel(command.Argument, turn);
            case DebugCommandKind.ListProviders:
                Console.WriteLine(turn.Config.Describe());
                return true;
            case DebugCommandKind.ListModels:
                Console.WriteLine(DescribeModels(turn));
                return true;
            case DebugCommandKind.Status:
                Console.WriteLine(DescribeStatus(turn));
                return true;
            case DebugCommandKind.NewSession:
                turn.SessionId = null;
                turn.SelectionDirty = false;
                Console.WriteLine("已准备新会话。下一条消息按当前模式重新开始。");
                return true;
            case DebugCommandKind.Cancel:
                Console.WriteLine("当前没有正在执行的轮次。");
                return true;
            case DebugCommandKind.Clear:
                transcript.ClearOutput();
                Console.WriteLine("输出区已清空。");
                return true;
            case DebugCommandKind.Prompt:
                return true;
            default:
                return false;
        }
    }

    private static bool SetMode(string argument, DebugTurnClient turn)
    {
        if (argument.Length == 0)
        {
            Console.WriteLine("当前模式: " + turn.Mode);
            return true;
        }

        if (!TryParseMode(argument, out var mode))
        {
            Console.WriteLine("模式只能是 expert、work 或 meeting。");
            return true;
        }

        if (turn.SessionId is not null && mode != turn.Mode)
        {
            Console.WriteLine("当前会话模式已固定。先输入 /new，下一条消息才会改用 " + mode + "。");
            return true;
        }

        turn.Mode = mode;
        Console.WriteLine("模式: " + mode);
        return true;
    }

    private static bool TryParseMode(string value, out RuntimeMode mode)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "expert":
            case "专家":
                mode = RuntimeMode.Expert;
                return true;
            default:
                mode = RuntimeMode.Expert;
                return false;
        }
    }
}
