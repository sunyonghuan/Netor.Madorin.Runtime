using Madorin.AI.Runtime.MConsole.Testing;

namespace Madorin.AI.Runtime.MConsole.Concurrency;

public static class AggregateAssertionEngine
{
    public static AggregateAssertionResult[] Evaluate(
        IReadOnlyList<ConcurrentStepResult> results,
        IReadOnlyList<AggregateExpectation> expectations) =>
        expectations.Select(expectation => Evaluate(results, expectation)).ToArray();

    private static AggregateAssertionResult Evaluate(
        IReadOnlyList<ConcurrentStepResult> allResults,
        AggregateExpectation expectation)
    {
        var selected = allResults.Where(item => expectation.StepIds.Contains(
            item.StepId,
            StringComparer.Ordinal)).ToArray();
        if (selected.Length != expectation.StepIds.Distinct(StringComparer.Ordinal).Count())
        {
            return Result(expectation, false, "One or more referenced steps are missing.");
        }

        return expectation.Kind switch
        {
            "atMostOneExitZeroAmong" => Result(
                expectation,
                selected.Count(static item => item.Execution.ExitCode == 0) <= 1,
                $"exit-zero count={selected.Count(static item => item.Execution.ExitCode == 0)}"),
            "allOthersExitCode" => EvaluateAllOthersExitCode(expectation, selected),
            "allNoDataChange" => Result(
                expectation,
                selected.All(NoDataChange),
                DescribeDataChanges(selected)),
            "noCrossWorkspaceWrites" => Result(
                expectation,
                selected.GroupBy(static item => item.WorkspaceId, StringComparer.Ordinal)
                    .All(static group => group.All(item => string.Equals(
                        item.WorkspaceId,
                        group.Key,
                        StringComparison.Ordinal))),
                "Each result remained bound to its declared workspace."),
            "timelineOverlapAtLeast" => EvaluateTimelineOverlap(expectation, selected),
            _ => Result(expectation, false, $"Unsupported aggregate assertion '{expectation.Kind}'."),
        };
    }

    private static AggregateAssertionResult EvaluateAllOthersExitCode(
        AggregateExpectation expectation,
        ConcurrentStepResult[] selected)
    {
        var expected = expectation.ExpectedExitCode
            ?? throw new InvalidDataException("allOthersExitCode requires expectedExitCode.");
        var successes = selected.Count(static item => item.Execution.ExitCode == 0);
        var passed = successes <= 1
            && selected.Where(static item => item.Execution.ExitCode != 0)
                .All(item => item.Execution.ExitCode == expected);
        return Result(expectation, passed, $"successful={successes}; expected non-zero exit={expected}.");
    }

    private static AggregateAssertionResult EvaluateTimelineOverlap(
        AggregateExpectation expectation,
        ConcurrentStepResult[] selected)
    {
        if (selected.Length < 2)
        {
            return Result(expectation, false, "At least two steps are required to measure overlap.");
        }

        var overlapStart = selected.Max(static item => item.Timeline.StartedAt);
        var overlapEnd = selected.Min(static item => item.Timeline.ExitedAt);
        var milliseconds = Math.Max(0, (overlapEnd - overlapStart).TotalMilliseconds);
        var minimum = expectation.MinimumMilliseconds ?? 0;
        return Result(expectation, milliseconds >= minimum, $"overlap={milliseconds:F0}ms; minimum={minimum}ms.");
    }

    private static bool NoDataChange(ConcurrentStepResult result) =>
        result.Before is not null
        && result.After is not null
        && !FileTreeSnapshotService.Compare(result.Before, result.After).HasChanges;

    private static string DescribeDataChanges(IEnumerable<ConcurrentStepResult> results)
    {
        var changed = results.Where(static item => !NoDataChange(item))
            .Select(static item => item.StepId)
            .ToArray();
        return changed.Length == 0 ? "No data changes detected." : "Changed steps: " + string.Join(", ", changed);
    }

    private static AggregateAssertionResult Result(
        AggregateExpectation expectation,
        bool passed,
        string message) => new(expectation.Kind, passed, message, expectation.StepIds);
}
