namespace Madorin.AI.Runtime.MConsole.Hosting;

public sealed class MadorinLocator(ProcessRunner processRunner)
{
    private static readonly string[] NonRunnableBuildOutputSegments = ["obj", "native", "ref", "refint"];

    public async Task<LocatedExecutable> LocateAsync(
        string? explicitPath,
        string? repositoryRoot,
        TimeSpan validationTimeout,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();
        foreach (var candidate in GetCandidates(explicitPath, repositoryRoot))
        {
            if (!File.Exists(candidate.Path))
            {
                failures.Add($"{candidate.Source}: '{candidate.Path}' does not exist.");
                continue;
            }

            var result = await processRunner.RunAsync(
                candidate.Path,
                ["version", "--json", "--no-color"],
                timeout: validationTimeout,
                cancellationToken: cancellationToken);
            if (result.ExitCode == 0 && result.Envelope is { Success: true })
            {
                return new LocatedExecutable(candidate.Path, candidate.Source, result);
            }

            failures.Add(
                $"{candidate.Source}: '{candidate.Path}' failed validation " +
                $"(exit={result.ExitCode}, stderr={Summarize(result.StandardError)})." );
        }

        throw new FileNotFoundException(
            "No runnable madorin executable was found. " + string.Join(" ", failures));
    }

    public static IReadOnlyList<(string Path, ExecutableLocationSource Source)> GetCandidates(
        string? explicitPath,
        string? repositoryRoot)
    {
        var candidates = new List<(string Path, ExecutableLocationSource Source)>();
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidates.Add((System.IO.Path.GetFullPath(explicitPath), ExecutableLocationSource.ExplicitConfiguration));
        }

        foreach (var pathCandidate in FindOnPath())
        {
            candidates.Add((pathCandidate, ExecutableLocationSource.PathEnvironment));
        }

        if (!string.IsNullOrWhiteSpace(repositoryRoot))
        {
            var cliOutput = System.IO.Path.Combine(
                System.IO.Path.GetFullPath(repositoryRoot),
                "src",
                "Madorin.AI.Runtime.Cli",
                "bin");
            if (Directory.Exists(cliOutput))
            {
                var executableNames = OperatingSystem.IsWindows()
                    ? new[] { "madorin.exe", "madorin" }
                    : new[] { "madorin", "madorin.exe" };

                foreach (var executableName in executableNames)
                {
                    candidates.AddRange(
                        Directory.EnumerateFiles(cliOutput, executableName, SearchOption.AllDirectories)
                            .Where(IsRunnableBuildOutput)
                            .OrderByDescending(File.GetLastWriteTimeUtc)
                            .Select(path => (path, ExecutableLocationSource.RepositoryBuildOutput)));
                }
            }
        }

        return candidates
            .DistinctBy(candidate => candidate.Path, PathComparer)
            .ToArray();
    }

    private static IEnumerable<string> FindOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        var names = OperatingSystem.IsWindows()
            ? new[] { "madorin.exe", "madorin" }
            : new[] { "madorin", "madorin.exe" };
        foreach (var directory in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                string candidate;
                try
                {
                    candidate = System.IO.Path.GetFullPath(System.IO.Path.Join(directory.Trim(), name));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    continue;
                }

                if (File.Exists(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static string Summarize(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 200 ? normalized : normalized[..200];
    }

    private static bool IsRunnableBuildOutput(string path)
    {
        var separator = System.IO.Path.DirectorySeparatorChar;
        return !NonRunnableBuildOutputSegments.Any(segment =>
            path.Contains(
                $"{separator}{segment}{separator}",
                StringComparison.OrdinalIgnoreCase));
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
