using Madorin.AI.Runtime.Cli.Config;

namespace Madorin.AI.Runtime.Cli.Commands;

/// <summary>交互帮助的显示语言。默认中文，本次会话内可切换。</summary>
internal enum ReplHelpLanguage
{
    Chinese,
    English
}

/// <summary>
/// 交互会话里的 /help。默认中文，/help en 切换英文，/help zh 切回中文。
/// </summary>
internal static class ReplHelpWriter
{
    public static bool Write(
        ICliTerminal terminal,
        string? argument,
        ReplHelpLanguage current,
        out ReplHelpLanguage language)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        var topic = SplitLanguage(argument, current, out language);
        if (language != current)
        {
            terminal.WriteLine(
                language == ReplHelpLanguage.English
                    ? "Switched to English."
                    : "已切换为中文。",
                CliTextStyle.Muted);
        }

        if (string.IsNullOrWhiteSpace(topic))
        {
            WriteOverview(terminal, language);
            return true;
        }

        var key = topic.Trim().TrimStart('/');
        foreach (var group in Groups)
        {
            foreach (var entry in group.Entries)
            {
                if (entry.Topics.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    WriteDetail(terminal, entry, language);
                    return true;
                }
            }
        }

        return false;
    }

    private static string? SplitLanguage(
        string? argument,
        ReplHelpLanguage current,
        out ReplHelpLanguage language)
    {
        language = current;
        if (string.IsNullOrWhiteSpace(argument))
        {
            return null;
        }

        var trimmed = argument.Trim();
        var splitAt = trimmed.IndexOf(' ');
        var first = splitAt < 0 ? trimmed : trimmed[..splitAt];
        var rest = splitAt < 0 ? null : trimmed[(splitAt + 1)..].Trim();
        if (IsEnglish(first))
        {
            language = ReplHelpLanguage.English;
            return string.IsNullOrWhiteSpace(rest) ? null : rest;
        }

        if (IsChinese(first))
        {
            language = ReplHelpLanguage.Chinese;
            return string.IsNullOrWhiteSpace(rest) ? null : rest;
        }

        return trimmed;
    }

    private static bool IsEnglish(string token) =>
        token.Equals("en", StringComparison.OrdinalIgnoreCase)
        || token.Equals("english", StringComparison.OrdinalIgnoreCase);

    private static bool IsChinese(string token) =>
        token.Equals("zh", StringComparison.OrdinalIgnoreCase)
        || token.Equals("cn", StringComparison.OrdinalIgnoreCase)
        || token.Equals("chinese", StringComparison.OrdinalIgnoreCase)
        || token.Equals("中文", StringComparison.Ordinal);

    private static void WriteOverview(ICliTerminal terminal, ReplHelpLanguage language)
    {
        terminal.WriteLine(Text(language, "交互命令", "Interactive commands"), CliTextStyle.Title);
        terminal.WriteLine(
            Text(
                language,
                "直接输入内容并回车就会发送。以 / 开头的是交互命令。",
                "Type a message and press Enter to send it. A leading / starts a command."),
            CliTextStyle.Muted);
        terminal.WriteLine(
            Text(
                language,
                "[] 可以不写，<> 必须写。",
                "[] is optional. <> is required."),
            CliTextStyle.Muted);
        terminal.WriteLine();
        terminal.WriteLine(Text(language, "怎么查命令", "How to look up a command"), CliTextStyle.Title);
        WriteUsage(
            terminal,
            "/help",
            Text(language, "列出全部命令。", "List every command."));
        WriteUsage(
            terminal,
            "/help <command>",
            Text(language, "查看某一个命令，例如 /help history。", "Show one command, for example /help history."));
        WriteUsage(
            terminal,
            "/help 历史",
            Text(language, "也可以用中文查。历史对话是 /history，以前的会话是 /resume。", "Chinese names work too. Conversation history is /history. Past sessions are /resume."));
        WriteUsage(
            terminal,
            "/help en",
            Text(language, "把帮助切换成英文，直到退出。", "Switch help to English until you exit."));
        WriteUsage(
            terminal,
            "/help zh",
            Text(language, "把帮助切回中文。", "Switch help back to Chinese."));
        foreach (var group in Groups)
        {
            terminal.WriteLine();
            terminal.WriteLine(group.Title(language), CliTextStyle.Title);
            foreach (var entry in group.Entries)
            {
                terminal.Write("  ");
                terminal.Write(entry.Command, CliTextStyle.Command);
                var parameter = entry.Parameter(language);
                if (parameter is not null)
                {
                    terminal.Write(" ");
                    terminal.Write(parameter, CliTextStyle.Parameter);
                }

                terminal.Write("  ");
                terminal.WriteLine(entry.Summary(language), CliTextStyle.Muted);
            }
        }
    }

    private static void WriteDetail(
        ICliTerminal terminal,
        HelpEntry entry,
        ReplHelpLanguage language)
    {
        terminal.Write(entry.Command, CliTextStyle.Command);
        var parameter = entry.Parameter(language);
        if (parameter is not null)
        {
            terminal.Write(" ");
            terminal.Write(parameter, CliTextStyle.Parameter);
        }

        terminal.Write("  ");
        terminal.WriteLine(entry.Summary(language));
        var detail = entry.Detail(language);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            terminal.WriteLine(detail, CliTextStyle.Muted);
        }

        terminal.WriteLine(Text(language, "示例：", "Examples:"), CliTextStyle.Muted);
        foreach (var example in entry.Examples)
        {
            terminal.Write("  ");
            terminal.WriteLine(example, CliTextStyle.Command);
        }
    }

    private static string Text(ReplHelpLanguage language, string chinese, string english) =>
        language == ReplHelpLanguage.English ? english : chinese;

    private static void WriteUsage(ICliTerminal terminal, string command, string description)
    {
        terminal.Write("  ");
        terminal.Write(command, CliTextStyle.Command);
        terminal.Write("  ");
        terminal.WriteLine(description, CliTextStyle.Muted);
    }

    private static HelpGroup[] Groups { get; } =
    [
        new(
            "对话",
            "Chat",
            [
                Entry(
                    ["help", "帮助", "命令"],
                    "/help",
                    "[命令]",
                    "[command]",
                    "查看命令，或查看某一个命令怎么用。",
                    "Show the commands, or show how one command works.",
                    "默认中文。/help en 切换英文并保持到退出。/help zh 切回中文。/help en history 用英文查看某一个命令。中文也可以查，例如 /help 历史。",
                    "Chinese is the default. /help en switches to English until you exit. /help zh switches back. /help en history shows one command in English. Chinese names work too, for example /help 历史.",
                    ["/help", "/help en", "/help zh", "/help history", "/help 历史"]),
                Entry(
                    ["exit", "quit", "退出", "结束"],
                    "/exit",
                    null,
                    null,
                    "保存当前会话并退出。/quit 相同。",
                    "Save the current session and exit. /quit does the same.",
                    null,
                    null,
                    ["/exit"])
            ]),
        new(
            "添加供应商和模型",
            "Add a provider and models",
            [
                Entry(
                    ["config", "provider-add", "model-add", "add", "添加供应商"],
                    "madorin config edit",
                    null,
                    null,
                    "添加供应商，并给它登记模型。",
                    "Add a provider and register its models.",
                    "先用 /exit 离开交互界面。执行后选 1，再选 Add Provider，或输入 new。按提示填写协议、名称、地址和 API key。模型用英文逗号分隔；输入 discover 可从供应商拉取。选 2 则修改默认供应商、默认模型和默认智能体。/provider 和 /model 只能切换已经添加的项，不能新增。",
                    "Leave the interactive screen with /exit first. Choose 1, then Add Provider, or type new. Enter the protocol, name, address, and API key. Separate model ids with commas, or type discover to fetch them. Choose 2 to change the default provider, model, and agent. /provider and /model only switch items that already exist.",
                    ["madorin config edit", "madorin config show"]),
                Entry(
                    ["config-init", "init", "初始化"],
                    "madorin config init",
                    null,
                    null,
                    "第一次使用时创建配置。",
                    "Create the configuration the first time you use Madorin.",
                    "会依次询问供应商、API key 和模型。配置保存在用户目录的 .madorin/config.json。已有配置时会先询问是否重建。",
                    "Asks for the provider, API key, and models. The file is .madorin/config.json in your user profile. If a configuration already exists, it asks before replacing it.",
                    ["madorin config init"]),
                Entry(
                    ["agent-create", "agent-add", "新建智能体"],
                    "madorin agent create",
                    "[智能体编号]",
                    "[agent-id]",
                    "新建一个智能体。",
                    "Create an agent.",
                    "在交互界面外执行。不写编号时会询问。已有智能体用 madorin agent list 查看，用 madorin agent edit 编号 修改。",
                    "Run this outside the interactive screen. It asks for the id when you omit it. Use madorin agent list to see agents and madorin agent edit id to change one.",
                    ["madorin agent list", "madorin agent create reviewer"])
            ]),
        new(
            "下一轮使用哪一个",
            "Choose the next turn",
            [
                Entry(
                    ["provider", "供应商"],
                    "/provider",
                    "[供应商名称]",
                    "[provider-name]",
                    "切换下一轮使用的供应商。",
                    "Switch the provider used for the next turn.",
                    "不写名称时从已添加的供应商里选择。这里不能添加新供应商，添加请看 madorin config edit。",
                    "Omit the name to choose from providers that already exist. This does not add a provider. Use madorin config edit for that.",
                    ["/provider", "/provider deepseek"]),
                Entry(
                    ["model", "模型"],
                    "/model",
                    "[模型编号]",
                    "[model-id]",
                    "切换下一轮使用的模型。",
                    "Switch the model used for the next turn.",
                    "不写编号时从当前供应商已登记的模型里选择。模型必须属于当前供应商。新增模型请看 madorin config edit。",
                    "Omit the id to choose from models registered for the current provider. The model must belong to that provider. Add models with madorin config edit.",
                    ["/model", "/model deepseek-v4-pro"]),
                Entry(
                    ["agent", "智能体"],
                    "/agent",
                    "[智能体编号]",
                    "[agent-id]",
                    "切换下一轮使用的智能体。",
                    "Switch the agent used for the next turn.",
                    "不写编号时从已有智能体里选择。新建智能体用 madorin agent create。",
                    "Omit the id to choose from existing agents. Create one with madorin agent create.",
                    ["/agent", "/agent default"])
            ]),
        new(
            "会话",
            "Sessions",
            [
                Entry(
                    ["session", "当前会话"],
                    "/session",
                    null,
                    null,
                    "查看当前会话和下一轮选择。",
                    "Show the current session and the next-turn selection.",
                    "还没有发送内容时显示为新会话。",
                    "Shows a new session before you send anything.",
                    ["/session"]),
                Entry(
                    ["new", "新会话", "新对话"],
                    "/new",
                    null,
                    null,
                    "下一次发送内容时新建会话。",
                    "Start a new session on the next message.",
                    "当前会话会保留。",
                    "The current session is kept.",
                    ["/new"]),
                Entry(
                    ["resume", "恢复", "以前的会话"],
                    "/resume",
                    "[会话编号]",
                    "[session-id]",
                    "查询并继续以前的会话。",
                    "List past sessions, or continue one.",
                    "不写编号时列出最近 10 个同模式会话。编号是列表第一列。",
                    "Omit the id to list the 10 most recent sessions in this mode. The id is the first column.",
                    ["/resume", "/resume 0123abcd"]),
                Entry(
                    ["history", "历史", "历史对话", "历史消息"],
                    "/history",
                    "[条数 | all]",
                    "[count | all]",
                    "查询当前会话的历史对话。",
                    "Show this session's conversation history.",
                    "不写参数显示最近 5 条。/history 20 指定条数，/history all 显示全部。要回到以前的会话，用 /resume。",
                    "The default is the last 5 messages. /history 20 sets the count, and /history all shows every message. Use /resume to return to a past session.",
                    ["/history", "/history 20", "/history all"]),
                Entry(
                    ["export", "导出"],
                    "/export",
                    "[文件路径]",
                    "[file]",
                    "把当前会话导出为 Markdown。",
                    "Export the current session as Markdown.",
                    "不写路径时打印到屏幕。",
                    "Omit the path to print it on screen.",
                    ["/export", "/export session.md"]),
                Entry(
                    ["compact", "压缩"],
                    "/compact",
                    null,
                    null,
                    "压缩当前会话，生成上下文摘要。",
                    "Compact the session into a context summary.",
                    "先发送至少一条内容。历史仍保留，后续请求使用摘要。",
                    "Send at least one message first. History is kept, and later requests use the summary.",
                    ["/compact"]),
                Entry(
                    ["context", "上下文"],
                    "/context",
                    null,
                    null,
                    "查看完整历史和实际送给模型的上下文。",
                    "Show the full history and the context sent to the model.",
                    "分别显示消息数和大约的 token 数。",
                    "Shows message counts and approximate token counts.",
                    ["/context"])
            ]),
        new(
            "工作区",
            "Workspace",
            [
                Entry(
                    ["cd", "工作区", "切换目录"],
                    "/cd",
                    "[目录]",
                    "[directory]",
                    "查看或切换当前工作区。",
                    "Show or change the current workspace.",
                    "不写目录时显示当前路径。可以使用相对路径，~ 表示用户主目录。目录必须已经存在。",
                    "Omit the directory to show the current path. Relative paths work, and ~ is your user profile. The directory must exist.",
                    ["/cd", "/cd ../other-project"]),
                Entry(
                    ["status", "状态"],
                    "/status",
                    null,
                    null,
                    "查看运行实例、工作区、活动任务、内存和工具数量。",
                    "Show the runtime, workspace, active runs, memory, and tool count.",
                    null,
                    null,
                    ["/status"]),
                Entry(
                    ["mode", "模式"],
                    "/mode",
                    null,
                    null,
                    "查看当前模式。",
                    "Show the current mode.",
                    "交互界面里不能改模式。启动时使用 madorin run --mode expert。",
                    "The mode cannot be changed here. Choose it with madorin run --mode expert.",
                    ["/mode"]),
                Entry(
                    ["clear", "清屏"],
                    "/clear",
                    null,
                    null,
                    "清屏。不删除会话和历史。",
                    "Clear the screen. Sessions and history stay.",
                    null,
                    null,
                    ["/clear"])
            ]),
        new(
            "记忆、工具和技能",
            "Memory, tools, and skills",
            [
                Entry(
                    ["memory", "记忆"],
                    "/memory",
                    "[global | project | effective]",
                    "[global | project | effective]",
                    "查看记忆。",
                    "Show memory.",
                    "global 是用户级，project 是当前工作区，effective 是合并后实际生效的内容。",
                    "global is user memory, project is this workspace, and effective is the merged result.",
                    ["/memory", "/memory project"]),
                Entry(
                    ["remember", "记住"],
                    "/remember",
                    "<global | project>",
                    "<global | project>",
                    "追加一条记忆。写入前会要求确认。",
                    "Append one memory entry after confirmation.",
                    "必须写明 global 或 project。",
                    "global or project is required.",
                    ["/remember project"]),
                Entry(
                    ["tools", "工具"],
                    "/tools",
                    null,
                    null,
                    "查看当前工具，以及哪些需要批准。",
                    "Show the tools and which ones require approval.",
                    null,
                    null,
                    ["/tools"]),
                Entry(
                    ["permission", "权限"],
                    "/permission",
                    null,
                    null,
                    "查看或切换本次 CLI 会话的权限模式。",
                    "Show or change the permission mode for this CLI session.",
                    "可切换 ask-all、auto-tools、auto-all。模式仅在当前 CLI 进程内有效，退出后恢复 ask-all。",
                    "Switch between ask-all, auto-tools, and auto-all. The mode is local to this CLI process and resets to ask-all when it exits.",
                    ["/permission"]),
                Entry(
                    ["instructions", "说明"],
                    "/instructions",
                    null,
                    null,
                    "查看或切换工作区 AGENTS.md 的自动加载。",
                    "Show or change automatic loading of the workspace AGENTS.md.",
                    "开启后，每次运行都会读取工作区根目录的 AGENTS.md 并追加到系统提示词。该开关只在当前 CLI 进程内有效。",
                    "When enabled, each run reads AGENTS.md from the workspace root and appends it to the system prompt. The switch is local to this CLI process.",
                    ["/instructions"]),
                Entry(
                    ["skill", "技能"],
                    "/skill",
                    "<list | show 名称 | refresh>",
                    "<list | show name | refresh>",
                    "查看或刷新本地技能。",
                    "Show or refresh local skills.",
                    "list 列出技能。show 后面写名称。refresh 重新扫描技能目录。",
                    "list shows skills. show takes a name. refresh scans the skill directories again.",
                    ["/skill list", "/skill show repl-demo", "/skill refresh"])
            ])
    ];

    private static HelpEntry Entry(
        string[] topics,
        string command,
        string? chineseParameter,
        string? englishParameter,
        string chineseSummary,
        string englishSummary,
        string? chineseDetail,
        string? englishDetail,
        string[] examples) =>
        new(
            topics,
            command,
            chineseParameter,
            englishParameter,
            chineseSummary,
            englishSummary,
            chineseDetail,
            englishDetail,
            examples);

    private sealed record HelpGroup(string ChineseTitle, string EnglishTitle, HelpEntry[] Entries)
    {
        public string Title(ReplHelpLanguage language) =>
            language == ReplHelpLanguage.English ? EnglishTitle : ChineseTitle;
    }

    private sealed record HelpEntry(
        string[] Topics,
        string Command,
        string? ChineseParameter,
        string? EnglishParameter,
        string ChineseSummary,
        string EnglishSummary,
        string? ChineseDetail,
        string? EnglishDetail,
        string[] Examples)
    {
        public string? Parameter(ReplHelpLanguage language) =>
            language == ReplHelpLanguage.English ? EnglishParameter : ChineseParameter;

        public string Summary(ReplHelpLanguage language) =>
            language == ReplHelpLanguage.English ? EnglishSummary : ChineseSummary;

        public string? Detail(ReplHelpLanguage language) =>
            language == ReplHelpLanguage.English ? EnglishDetail : ChineseDetail;
    }
}
