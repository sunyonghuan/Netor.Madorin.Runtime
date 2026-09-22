using Madorin.AI.Runtime.Cli.Config;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class ReplHelpWriter
{
    private const int DescriptionGap = 4;

    private static readonly HelpEntry[] Entries =
    [
        new(["MODEL"], "/model", "[modelId]", "选择或更新下一轮使用的模型。"),
        new(["PROVIDER"], "/provider", "[providerId]", "选择或更新下一轮使用的模型服务商。"),
        new(["AGENT"], "/agent", "[agentId]", "选择或更新下一轮使用的智能体。"),
        new(["MEMORY"], "/memory", "[global|project|effective]", "查看记忆内容及其来源。"),
        new(["REMEMBER"], "/remember", "<global|project>", "经交互确认后追加一条记忆。"),
        new(["MODE"], "/mode", null, "查看当前交互会话中不可变更的工作模式。"),
        new(["COMPACT"], "/compact", null, "创建或刷新当前会话的上下文摘要。"),
        new(["CONTEXT"], "/context", null, "查看规范历史记录和有效上下文投影的统计信息。"),
        new(["SESSION"], "/session", null, "查看当前会话及下一轮选择。"),
        new(["NEW"], "/new", null, "在下次提交内容时新建会话。"),
        new(["RESUME"], "/resume", "[sessionId]", "列出可恢复的会话，或切换到指定会话；Meeting 暂停后校验续跑。"),
        new(["PAUSE"], "/pause", null, "Meeting：请求协作式暂停（运行中优先 Ctrl+C 一次）。"),
        new(["INTERRUPT"], "/interrupt", "<text>", "Meeting：入队用户插话，下一安全点/续跑由主持人吸收。"),
        new(["EXPORT"], "/export", "[path]", "将当前会话导出为 Markdown 文件。"),
        new(["TOOLS"], "/tools", null, "查看工具及其权限状态。"),
        new(["SKILL"], "/skill", "<list|show <name>|refresh>", "查看或刷新本地技能目录。"),
        new(["HISTORY"], "/history", "[positive-count|all]", "查看最近的规范化消息。"),
        new(["CLEAR"], "/clear", null, "清空当前交互式控制台，不删除历史记录。"),
        new(["CD"], "/cd", "[path]", "查看或切换当前工作区。"),
        new(["STATUS"], "/status", null, "查看运行时、工作区、活动、记忆和工具数量。"),
        new(["HELP"], "/help", "[command]", "显示命令列表或指定命令的详细帮助。"),
        new(["EXIT", "QUIT"], "/exit | /quit", null, "保存当前会话并退出交互界面。")
    ];

    private static readonly int DescriptionColumn =
        Entries.Max(GetCommandLength) + DescriptionGap;

    public static bool Write(ICliTerminal terminal, string? topic)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        if (topic is null)
        {
            terminal.WriteLine("可用命令：", CliTextStyle.Title);
            foreach (var entry in Entries)
            {
                WriteEntry(terminal, entry);
            }

            return true;
        }

        var normalizedTopic = topic.TrimStart('/').ToUpperInvariant();
        var match = Entries.FirstOrDefault(entry => entry.Topics.Contains(
            normalizedTopic,
            StringComparer.Ordinal));
        if (match is null)
        {
            return false;
        }

        WriteEntry(terminal, match);
        return true;
    }

    private static void WriteEntry(ICliTerminal terminal, HelpEntry entry)
    {
        terminal.Write(entry.Command, CliTextStyle.Command);
        if (entry.Parameter is not null)
        {
            terminal.Write(" ");
            terminal.Write(entry.Parameter, CliTextStyle.Parameter);
        }

        terminal.Write(
            new string(' ', DescriptionColumn - GetCommandLength(entry)),
            CliTextStyle.Muted);
        terminal.WriteLine(entry.Description, CliTextStyle.Muted);
    }

    private static int GetCommandLength(HelpEntry entry) =>
        entry.Command.Length + (entry.Parameter is null ? 0 : entry.Parameter.Length + 1);

    private sealed record HelpEntry(
        string[] Topics,
        string Command,
        string? Parameter,
        string Description);
}
