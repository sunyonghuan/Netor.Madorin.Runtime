using Madorin.AI.Runtime.Cli.Config;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class ReplWelcomeWriter
{
    public static void Write(ICliTerminal terminal, ReplWelcomeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(snapshot);

        var agent = string.IsNullOrWhiteSpace(snapshot.AgentName)
            ? snapshot.AgentId
            : snapshot.AgentName;

        terminal.WriteLine($"Madorin AI Runtime {snapshot.Version}", CliTextStyle.Title);
        terminal.Write("Local runtime ");
        terminal.WriteLine("ready", CliTextStyle.Success);
        terminal.WriteLine();
        WriteField(terminal, "Mode", snapshot.Mode.ToString());
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
}
