namespace Madorin.AI.Runtime.MConsole.Configuration;

public sealed record MConsolePaths(
    string HomeDirectory,
    string HistoryDirectory,
    string CasesDirectory,
    string BaselinesDirectory,
    string StateDirectory,
    string ReportsDirectory)
{
    public static MConsolePaths Create(string configuredHome)
    {
        var home = NormalizePath(configuredHome);
        return new MConsolePaths(
            home,
            Path.Combine(home, "history"),
            Path.Combine(home, "cases"),
            Path.Combine(home, "baselines"),
            Path.Combine(home, "state"),
            Path.Combine(home, "reports"));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(HomeDirectory);
        Directory.CreateDirectory(HistoryDirectory);
        Directory.CreateDirectory(CasesDirectory);
        Directory.CreateDirectory(BaselinesDirectory);
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(ReportsDirectory);
    }

    public static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var expanded = path;
        if (path == "~")
        {
            expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        else if (path.StartsWith("~/", StringComparison.Ordinal)
            || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            expanded = Path.Join(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path.AsSpan(2));
        }

        return Path.GetFullPath(expanded);
    }
}
