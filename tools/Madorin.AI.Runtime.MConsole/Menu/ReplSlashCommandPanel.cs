using Madorin.AI.Runtime.MConsole.Hosting;

namespace Madorin.AI.Runtime.MConsole.Menu;

public sealed class ReplSlashCommandPanel
{
    private static readonly string[] Commands =
    [
        "/model", "/provider", "/agent", "/mode", "/compact", "/context",
        "/session", "/new", "/resume", "/export", "/tools", "/history",
        "/memory", "/remember", "/skill", "/status", "/clear", "/help",
        "/exit", "/quit",
    ];

    public static IReadOnlyList<string> AvailableCommands => Commands;

    public static async Task SendAsync(
        ReplSession session,
        string command,
        string? argument = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Commands.Contains(command, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unknown REPL command '{command}'.", nameof(command));
        }

        var line = string.IsNullOrWhiteSpace(argument)
            ? command
            : command + " " + argument.Trim();
        await session.SendLineAsync(line, cancellationToken);
    }
}
