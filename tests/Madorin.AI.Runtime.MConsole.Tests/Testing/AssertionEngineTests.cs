using System.Text.Json;

using Madorin.AI.Runtime.MConsole.Hosting;
using Madorin.AI.Runtime.MConsole.Testing;
using Madorin.AI.Runtime.MConsole.Tests.TestSupport;

namespace Madorin.AI.Runtime.MConsole.Tests.Testing;

[TestClass]
public sealed class AssertionEngineTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public void Evaluate_ExecutionAssertions_ReturnStructuredResultsWithoutThrowing()
    {
        var execution = CreateExecution(
            0,
            "{\"success\":true,\"data\":{\"value\":42},\"warnings\":[]}",
            string.Empty);
        var assertions = new[]
        {
            new AssertionDefinition(AssertionKind.ExitCodeEquals, Expected: "0"),
            new AssertionDefinition(AssertionKind.JsonSuccess, Expected: "true"),
            new AssertionDefinition(AssertionKind.JsonPathExists, Path: "data.value"),
            new AssertionDefinition(AssertionKind.JsonPathEquals, Path: "data.value", Expected: "42"),
            new AssertionDefinition(AssertionKind.NoAnsi),
            new AssertionDefinition(AssertionKind.DurationLessThan, MaximumMilliseconds: 1_000),
        };

        var results = AssertionEngine.Evaluate(execution, assertions);

        Assert.HasCount(assertions.Length, results);
        Assert.IsTrue(results.All(static item => item.Passed));
    }

    [TestMethod]
    public async Task NoDataChange_DetectsModifiedFileAndListsIt()
    {
        using var directory = new OwnedTestDirectory();
        var path = Path.Combine(directory.Path, "state.db");
        await File.WriteAllTextAsync(path, "before", TestContext.CancellationToken);
        var before = await FileTreeSnapshotService.CaptureAsync(
            directory.Path,
            TestContext.CancellationToken);
        await File.WriteAllTextAsync(path, "after", TestContext.CancellationToken);
        var after = await FileTreeSnapshotService.CaptureAsync(
            directory.Path,
            TestContext.CancellationToken);

        var result = AssertionEngine.Evaluate(
            CreateExecution(0, string.Empty, string.Empty),
            [new AssertionDefinition(AssertionKind.NoDataChange)],
            before,
            after).Single();

        Assert.IsFalse(result.Passed);
        StringAssert.Contains(result.Actual!, "state.db");
    }

    private static ExecutionResult CreateExecution(int exitCode, string stdout, string stderr)
    {
        _ = JsonEnvelope.TryParse(stdout, out var envelope, out _);
        return new ExecutionResult(
            "command",
            [],
            exitCode,
            stdout,
            stderr,
            envelope,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(10),
            TimedOut: false,
            Canceled: false);
    }
}
