using Madorin.AI.Runtime.Cli.Config;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class ReplWelcomeWriter
{
    private const string IntroIndent = "           ";
    internal const string Banner = "ＭＡＤＯＲＩＮ ＡＩ ＲＵＮＴＩＭＥ";

    private static readonly string[] Horse =
    [
        """              /\          /\""",
        """             /  \        /  \""",
        """            /    \      /    \""",
        """           |      \    /      |""",
        """           |   o          o   |""",
        """           |        ||        |""",
        """            \       ||       /""",
        """             \     /  \     /""",
        """              \   |    |   /""",
        """               \  |    |  /""",
        """                \ | __ | /""",
        """                 \| [] |/""",
        """                  |____|"""
    ];

    public static void Write(ICliTerminal terminal, ReplWelcomeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(snapshot);

        var agent = string.IsNullOrWhiteSpace(snapshot.AgentName)
            ? snapshot.AgentId
            : snapshot.AgentName;

        terminal.Write(Banner, CliTextStyle.Title);
        terminal.Write(" ");
        terminal.Write(snapshot.Version, CliTextStyle.Title);
        terminal.Write("  ");
        terminal.WriteLine("ready", CliTextStyle.Success);
        terminal.WriteLine();
        if (!terminal.TryWriteLogo(IntroIndent.Length))
        {
            WriteHorse(terminal);
        }

        terminal.WriteLine();
        terminal.WriteLine($"{IntroIndent}有史以来", CliTextStyle.Muted);
        terminal.WriteLine($"{IntroIndent}功能最强大 · 性能最快 · 效率最高", CliTextStyle.Muted);
        terminal.WriteLine($"{IntroIndent}集成最简单 · token 最省", CliTextStyle.Muted);
        terminal.WriteLine($"{IntroIndent}的 CLI", CliTextStyle.Muted);
        terminal.WriteLine();
        terminal.WriteLine($"{IntroIndent}@madorins.com", CliTextStyle.Muted);
        terminal.WriteLine($"{IntroIndent}service@madorins.com", CliTextStyle.Muted);
        terminal.WriteLine();
        WriteField(terminal, "Agent", agent, CliTextStyle.Selected);
        WriteField(terminal, "Provider", snapshot.Provider, CliTextStyle.Selected);
        WriteField(terminal, "Model", snapshot.Model, CliTextStyle.Selected);
        WriteField(terminal, "Workspace", snapshot.Workspace);
        WriteField(terminal, "Session", "New");
        terminal.WriteLine();
        terminal.Write("Type ");
        terminal.Write("/help", CliTextStyle.Command);
        terminal.Write(" to view commands, or ");
        terminal.Write("/exit", CliTextStyle.Command);
        terminal.WriteLine(" to quit.");
        terminal.WriteLine();
    }

    private static void WriteField(
        ICliTerminal terminal,
        string label,
        string value,
        CliTextStyle valueStyle = CliTextStyle.Default)
    {
        terminal.Write(label.PadRight(11), CliTextStyle.Muted);
        terminal.WriteLine(value, valueStyle);
    }

    private static void WriteHorse(ICliTerminal terminal)
    {
        foreach (var line in Horse)
        {
            terminal.WriteLine(line, CliTextStyle.Muted);
        }
    }
}
