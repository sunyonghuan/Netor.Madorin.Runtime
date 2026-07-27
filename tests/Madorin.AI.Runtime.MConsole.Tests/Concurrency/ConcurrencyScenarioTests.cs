using Madorin.AI.Runtime.MConsole.Concurrency;
using Madorin.AI.Runtime.MConsole.Hosting;
using Madorin.AI.Runtime.MConsole.Tests.TestSupport;

namespace Madorin.AI.Runtime.MConsole.Tests.Concurrency;

[TestClass]
public sealed class ConcurrencyScenarioTests
{
    [TestMethod]
    public void Validate_ResourceLimitExceeded_RejectsScenario()
    {
        var scenario = new ConcurrencyScenario(
            "too-many",
            new ConcurrencyResourceLimits(MaxParallelSteps: 1),
            [new ConcurrencyWorkspace("workspace", Environment.CurrentDirectory)],
            [],
            [
                new ConcurrentStep("one", "workspace", ["--version"]),
                new ConcurrentStep("two", "workspace", ["--version"]),
            ],
            []);

        Assert.ThrowsExactly<InvalidDataException>(() => ConcurrencyScenarioValidator.Validate(scenario));
    }

    [TestMethod]
    public async Task RunAsync_TwoCommands_UsesBarrierAndKeepsOutputsSeparated()
    {
        using var directory = new OwnedTestDirectory();
        var dataDirectory = Path.Combine(directory.Path, "data");
        Directory.CreateDirectory(dataDirectory);
        var scenario = new ConcurrencyScenario(
            "parallel-dotnet-version",
            new ConcurrencyResourceLimits(TimeoutSeconds: 30),
            [new ConcurrencyWorkspace("workspace", directory.Path, dataDirectory)],
            [],
            [
                new ConcurrentStep("one", "workspace", ["--version"]),
                new ConcurrentStep("two", "workspace", ["--version"]),
            ],
            [
                new AggregateExpectation("allNoDataChange", ["one", "two"]),
                new AggregateExpectation("timelineOverlapAtLeast", ["one", "two"]),
            ]);

        var report = await new ConcurrencyScenarioRunner("dotnet").RunAsync(scenario);

        Assert.IsTrue(report.IsSuccess);
        Assert.HasCount(2, report.Steps);
        Assert.IsTrue(report.Steps.All(static step => step.Execution.ExitCode == 0));
        Assert.IsTrue(report.Steps.All(static step => !string.IsNullOrWhiteSpace(step.Execution.StandardOutput)));
        Assert.IsTrue(report.Steps.Max(static step => step.Timeline.ReadyAt)
            <= report.Steps.Min(static step => step.Timeline.ExitedAt));
    }

    [TestMethod]
    public void Evaluate_LockContentionContract_RequiresExitFourForLosers()
    {
        var now = DateTimeOffset.UtcNow;
        var results = new[]
        {
            CreateResult("winner", 0, now),
            CreateResult("loser", 4, now),
        };

        var assertions = AggregateAssertionEngine.Evaluate(
            results,
            [
                new AggregateExpectation("atMostOneExitZeroAmong", ["winner", "loser"]),
                new AggregateExpectation("allOthersExitCode", ["winner", "loser"], ExpectedExitCode: 4),
            ]);

        Assert.IsTrue(assertions.All(static item => item.Passed));
    }

    private static ConcurrentStepResult CreateResult(string id, int exitCode, DateTimeOffset now) => new(
        id,
        "workspace",
        new ExecutionResult("madorin", [], exitCode, string.Empty, string.Empty, null, now, TimeSpan.Zero, false, false),
        new ConcurrentStepTimeline(now, now, null, now),
        null,
        null);
}
