namespace Madorin.AI.Runtime.Cli.Commands;

internal static class ReplWelcomeWriter
{
    public static void Write(TextWriter output, ReplWelcomeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(snapshot);

        var agent = string.IsNullOrWhiteSpace(snapshot.AgentName)
            ? snapshot.AgentId
            : snapshot.AgentName;

        output.WriteLine($"Madorin AI Runtime {snapshot.Version}");
        output.WriteLine("Local runtime ready");
        output.WriteLine();
        output.WriteLine($"Mode       {snapshot.Mode}");
        output.WriteLine($"Agent      {agent}");
        output.WriteLine($"Provider   {snapshot.Provider}");
        output.WriteLine($"Model      {snapshot.Model}");
        output.WriteLine($"Workspace  {snapshot.Workspace}");
        output.WriteLine("Session    New");
        output.WriteLine();
        output.WriteLine("Enter a message to begin. /exit to quit.");
        output.WriteLine();
    }
}
