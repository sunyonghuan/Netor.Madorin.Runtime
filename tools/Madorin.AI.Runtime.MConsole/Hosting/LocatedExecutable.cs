namespace Madorin.AI.Runtime.MConsole.Hosting;

public enum ExecutableLocationSource
{
    ExplicitConfiguration,
    PathEnvironment,
    RepositoryBuildOutput,
}

public sealed record LocatedExecutable(
    string Path,
    ExecutableLocationSource Source,
    ExecutionResult ValidationResult);
