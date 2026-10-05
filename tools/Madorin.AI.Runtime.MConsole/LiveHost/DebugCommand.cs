namespace Madorin.AI.Runtime.MConsole.LiveHost;

/// <summary>调试台命令种类。斜杠命令控制宿主，其余文本发给当前模型。</summary>
public enum DebugCommandKind
{
    Help,
    Mode,
    Provider,
    Model,
    ListProviders,
    ListModels,
    Status,
    NewSession,
    Cancel,
    Clear,
    Quit,
    Prompt,
    Unknown,
}

/// <summary>解析后的一行调试台输入。</summary>
public sealed record DebugCommand(DebugCommandKind Kind, string Argument);

/// <summary>把控制台输入解析成调试命令。不访问 Runtime。</summary>
public static class DebugCommandParser
{
    public const string HelpText =
        """
        直接输入文字，发送给当前模式的模型。
        /mode [expert|work|meeting]   查看或设定下一会话的模式
        /provider [id]                查看或切换厂商
        /model [id]                   查看或切换模型
        /providers                    列出配置里的厂商和模型
        /models                       列出当前厂商的模型
        /status                       显示连接、模式、厂商和模型
        /new                          结束当前会话，下一条消息按新模式开始
        /cancel                       取消正在执行的一轮
        /clear                        清空输出区
        /help                         显示本帮助
        /quit                         断开并退出调试台
        运行中按 Ctrl+C 取消本轮；空闲时按 Ctrl+C 退出。
        同一会话内不能改模式，改模式后要先 /new。
        """;

    public static DebugCommand Parse(string? line)
    {
        var text = line?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return new DebugCommand(DebugCommandKind.Prompt, string.Empty);
        }

        if (!text.StartsWith('/'))
        {
            return new DebugCommand(DebugCommandKind.Prompt, text);
        }

        var split = text.IndexOf(' ');
        var name = (split < 0 ? text : text[..split]).ToLowerInvariant();
        var argument = split < 0 ? string.Empty : text[(split + 1)..].Trim();
        var kind = name switch
        {
            "/help" or "/?" => DebugCommandKind.Help,
            "/mode" => DebugCommandKind.Mode,
            "/provider" => DebugCommandKind.Provider,
            "/model" => DebugCommandKind.Model,
            "/providers" => DebugCommandKind.ListProviders,
            "/models" => DebugCommandKind.ListModels,
            "/status" => DebugCommandKind.Status,
            "/new" => DebugCommandKind.NewSession,
            "/cancel" => DebugCommandKind.Cancel,
            "/clear" => DebugCommandKind.Clear,
            "/quit" or "/exit" => DebugCommandKind.Quit,
            _ => DebugCommandKind.Unknown,
        };
        return new DebugCommand(kind, argument);
    }
}
