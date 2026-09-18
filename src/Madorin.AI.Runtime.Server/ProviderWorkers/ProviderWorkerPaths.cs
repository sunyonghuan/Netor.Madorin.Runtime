namespace Madorin.AI.Runtime.Server.ProviderWorkers;

public static class ProviderWorkerPaths
{
    public static string ResolveDirectory()
    {
        var env = Environment.GetEnvironmentVariable("MADORIN_PROVIDER_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return Path.GetFullPath(env.Trim());
        }

        var processDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(processDir))
        {
            return Path.GetFullPath(processDir);
        }

        return Path.GetFullPath(AppContext.BaseDirectory);
    }

    public static string GetExecutablePath(string directory, string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        var fileName = OperatingSystem.IsWindows()
            ? $"madorin-provider-{slug}.exe"
            : $"madorin-provider-{slug}";
        return Path.GetFullPath(Path.Combine(directory, fileName));
    }
}
