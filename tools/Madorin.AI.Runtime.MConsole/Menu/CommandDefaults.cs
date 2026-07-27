using Madorin.AI.Runtime.MConsole.Configuration;

namespace Madorin.AI.Runtime.MConsole.Menu;

public sealed record CommandDefaults(
    string? Workspace,
    string? DataDirectory,
    string LogLevel,
    bool NoColor,
    bool Json)
{
    public static CommandDefaults FromOptions(MConsoleOptions options) => new(
        NormalizeOptionalPath(options.DefaultWorkspace),
        NormalizeOptionalPath(options.DefaultDataDirectory),
        options.DefaultLogLevel,
        options.DefaultNoColor,
        options.DefaultJson);

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : MConsolePaths.NormalizePath(path);
}
