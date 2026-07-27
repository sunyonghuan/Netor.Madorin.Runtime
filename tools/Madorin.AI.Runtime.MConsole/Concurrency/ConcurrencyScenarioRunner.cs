using Madorin.AI.Runtime.MConsole.Hosting;
using Madorin.AI.Runtime.MConsole.Testing;

namespace Madorin.AI.Runtime.MConsole.Concurrency;

public sealed class ConcurrencyScenarioRunner(string executablePath)
{
    private readonly string _executablePath = Path.IsPathFullyQualified(executablePath)
        ? Path.GetFullPath(executablePath)
        : executablePath;

    public async Task<ConcurrencyScenarioReport> RunAsync(
        ConcurrencyScenario scenario,
        CancellationToken cancellationToken = default)
    {
        ConcurrencyScenarioValidator.Validate(scenario);
        var startedAt = DateTimeOffset.UtcNow;
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(scenario.ResourceLimits.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        var workspaceMap = scenario.Workspaces.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        var tasks = scenario.ConcurrentSteps.Select(step => Task.Run(async () =>
        {
            var readyAt = DateTimeOffset.UtcNow;
            if (Interlocked.Increment(ref readyCount) == scenario.ConcurrentSteps.Length)
            {
                ready.TrySetResult();
            }

            await release.Task.WaitAsync(linked.Token);
            var workspace = workspaceMap[step.WorkspaceId];
            var dataDirectory = step.DataDirectory ?? workspace.DataDirectory;
            var before = dataDirectory is null
                ? null
                : await FileTreeSnapshotService.CaptureAsync(dataDirectory, linked.Token);
            var execution = await new ProcessRunner().RunAsync(
                _executablePath,
                step.Arguments,
                workspace.Path,
                timeout: TimeSpan.FromSeconds(scenario.ResourceLimits.TimeoutSeconds),
                cancellationToken: linked.Token);
            var exitedAt = DateTimeOffset.UtcNow;
            var after = dataDirectory is null
                ? null
                : await FileTreeSnapshotService.CaptureAsync(dataDirectory, linked.Token);
            DateTimeOffset? firstOutput = string.IsNullOrEmpty(execution.StandardOutput)
                && string.IsNullOrEmpty(execution.StandardError)
                ? null
                : exitedAt;
            return new ConcurrentStepResult(
                step.Id,
                step.WorkspaceId,
                execution,
                new ConcurrentStepTimeline(readyAt, execution.StartedAt, firstOutput, exitedAt),
                before,
                after);
        }, CancellationToken.None)).ToArray();

        await ready.Task.WaitAsync(linked.Token);
        release.TrySetResult();
        ConcurrentStepResult[] results;
        try
        {
            results = await Task.WhenAll(tasks).WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            linked.Cancel();
            throw new TimeoutException(
                $"Concurrency scenario '{scenario.Name}' exceeded its {scenario.ResourceLimits.TimeoutSeconds}s timeout.");
        }

        var assertions = AggregateAssertionEngine.Evaluate(results, scenario.Expectations);
        return new ConcurrencyScenarioReport(
            scenario.Name,
            startedAt,
            DateTimeOffset.UtcNow,
            results.OrderBy(static item => item.StepId, StringComparer.Ordinal).ToArray(),
            assertions,
            []);
    }
}
