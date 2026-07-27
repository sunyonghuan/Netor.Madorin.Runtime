using Madorin.AI.Runtime.MConsole.Hosting;
using Madorin.AI.Runtime.MConsole.Testing;

namespace Madorin.AI.Runtime.MConsole.Concurrency;

public sealed record ConcurrencyResourceLimits(
    int MaxInstances = 4,
    int MaxWorkspaces = 4,
    int MaxParallelSteps = 8,
    int TimeoutSeconds = 120);

public sealed record ConcurrencyWorkspace(string Id, string Path, string? DataDirectory = null);

public sealed record ConcurrencyInstance(
    string Id,
    string WorkspaceId,
    int MaxRuns = 1);

public sealed record ConcurrentStep(
    string Id,
    string WorkspaceId,
    string[] Arguments,
    string? DataDirectory = null);

public sealed record AggregateExpectation(
    string Kind,
    string[] StepIds,
    int? ExpectedExitCode = null,
    int? Maximum = null,
    long? MinimumMilliseconds = null);

public sealed record ConcurrencyScenario(
    string Name,
    ConcurrencyResourceLimits ResourceLimits,
    ConcurrencyWorkspace[] Workspaces,
    ConcurrencyInstance[] Instances,
    ConcurrentStep[] ConcurrentSteps,
    AggregateExpectation[] Expectations);

public sealed record ConcurrentStepTimeline(
    DateTimeOffset ReadyAt,
    DateTimeOffset StartedAt,
    DateTimeOffset? FirstOutputObservedAt,
    DateTimeOffset ExitedAt);

public sealed record ConcurrentStepResult(
    string StepId,
    string WorkspaceId,
    ExecutionResult Execution,
    ConcurrentStepTimeline Timeline,
    FileTreeSnapshot? Before,
    FileTreeSnapshot? After);

public sealed record AggregateAssertionResult(
    string Kind,
    bool Passed,
    string Message,
    string[] RelatedStepIds);

public sealed record ConcurrencyScenarioReport(
    string Name,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    ConcurrentStepResult[] Steps,
    AggregateAssertionResult[] Assertions,
    string[] CleanupErrors)
{
    public bool IsSuccess => Assertions.All(static item => item.Passed) && CleanupErrors.Length == 0;
}

public static class ConcurrencyScenarioValidator
{
    public static void Validate(ConcurrencyScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario.Name);
        var limits = scenario.ResourceLimits;
        if (limits.MaxInstances is < 1 or > 32
            || limits.MaxWorkspaces is < 1 or > 32
            || limits.MaxParallelSteps is < 1 or > 64
            || limits.TimeoutSeconds is < 1 or > 3600)
        {
            throw new InvalidDataException("Scenario resource limits are outside the supported safety bounds.");
        }

        if (scenario.Instances.Length > limits.MaxInstances
            || scenario.Workspaces.Length > limits.MaxWorkspaces
            || scenario.ConcurrentSteps.Length > limits.MaxParallelSteps)
        {
            throw new InvalidDataException("Scenario declarations exceed their configured resource limits.");
        }

        EnsureUnique(scenario.Workspaces.Select(static item => item.Id), "workspace");
        EnsureUnique(scenario.Instances.Select(static item => item.Id), "instance");
        EnsureUnique(scenario.ConcurrentSteps.Select(static item => item.Id), "step");
        var workspaces = scenario.Workspaces.Select(static item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (scenario.Instances.Any(item => !workspaces.Contains(item.WorkspaceId))
            || scenario.ConcurrentSteps.Any(item => !workspaces.Contains(item.WorkspaceId)))
        {
            throw new InvalidDataException("Scenario references an undeclared workspace.");
        }
    }

    private static void EnsureUnique(IEnumerable<string> values, string kind)
    {
        var duplicate = values.GroupBy(static value => value, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Scenario contains duplicate {kind} id '{duplicate.Key}'.");
        }
    }
}
