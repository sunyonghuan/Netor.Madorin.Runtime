using Madorin.AI.Runtime.Cli.Config;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class ReplHelpWriter
{
    private static readonly HelpEntry[] Entries =
    [
        new(["MODEL"], "/model", "[modelId]", "select or update the next-turn model."),
        new(["PROVIDER"], "/provider", "[providerId]", "select or update the next-turn Provider."),
        new(["AGENT"], "/agent", "[agentId]", "select or update the next-turn Agent."),
        new(["MEMORY"], "/memory", "[global|project|effective]", "show memory content and sources."),
        new(["REMEMBER"], "/remember", "<global|project>", "append one item after interactive approval."),
        new(["MODE"], "/mode", null, "show the immutable mode of the current REPL Session."),
        new(["COMPACT"], "/compact", null, "create or refresh the current Session context summary."),
        new(["CONTEXT"], "/context", null, "show CanonicalHistory and effective ContextProjection statistics."),
        new(["SESSION"], "/session", null, "show the current Session and next-turn selection."),
        new(["NEW"], "/new", null, "start a new Session on the next submitted turn."),
        new(["RESUME"], "/resume", "[sessionId]", "list resumable Sessions or switch to one."),
        new(["EXPORT"], "/export", "[path]", "write the current Session as Markdown."),
        new(["TOOLS"], "/tools", null, "show tools and their permission state."),
        new(["SKILL"], "/skill", "<list|show <name>|refresh>", "inspect or refresh the local Skill catalog."),
        new(["HISTORY"], "/history", "[positive-count|all]", "show recent canonical messages."),
        new(["CLEAR"], "/clear", null, "clear an attached interactive console without deleting history."),
        new(["STATUS"], "/status", null, "show Runtime, workspace, activity, memory, and tool counts."),
        new(["HELP"], "/help", "[command]", "show the command list or detailed command help."),
        new(["EXIT", "QUIT"], "/exit | /quit", null, "save the current Session and leave the REPL.")
    ];

    public static bool Write(ICliTerminal terminal, string? topic)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        if (topic is null)
        {
            terminal.WriteLine("Available commands:", CliTextStyle.Title);
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

        terminal.Write(" - ", CliTextStyle.Muted);
        terminal.WriteLine(entry.Description);
    }

    private sealed record HelpEntry(
        string[] Topics,
        string Command,
        string? Parameter,
        string Description);
}
