using Madorin.AI.Runtime.MConsole.Hosting;
using Madorin.AI.Runtime.MConsole.Testing;
using Madorin.AI.Runtime.MConsole.Tests.TestSupport;

namespace Madorin.AI.Runtime.MConsole.Tests.Testing;

[TestClass]
public sealed class HistoryAndBaselineTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task History_RedactsArgumentsAndSupportsFilteringAndExport()
    {
        using var directory = new OwnedTestDirectory();
        using var store = new ExecutionHistoryStore(directory.Path);
        var execution = CreateExecution(
            ["ctl", "credential", "update", "--api-key", "literal-secret"],
            "{\"success\":true,\"data\":{},\"warnings\":[]}");
        await store.AppendExecutionAsync(
            "ctl.credential.update",
            execution,
            [new AssertionResult(AssertionKind.ExitCodeEquals, true, "0", "0", "ok")],
            Environment.CurrentDirectory,
            TestContext.CancellationToken);

        var entries = await store.QueryAsync(
            new ExecutionHistoryFilter(CommandPrefix: "ctl.", Passed: true),
            TestContext.CancellationToken);
        var reportPath = Path.Combine(directory.Path, "history.md");
        await store.ExportMarkdownAsync(
            reportPath,
            cancellationToken: TestContext.CancellationToken);

        Assert.HasCount(1, entries);
        Assert.IsFalse(entries.Single().Arguments.Contains("literal-secret", StringComparer.Ordinal));
        Assert.IsTrue(entries.Single().Arguments.Contains("<redacted>", StringComparer.Ordinal));
        Assert.IsTrue(File.Exists(reportPath));
    }

    [TestMethod]
    public async Task Baseline_MasksVolatileValuesAndReportsStableDifference()
    {
        using var directory = new OwnedTestDirectory();
        var store = new BaselineStore(directory.Path);
        var first = CreateExecution([], "pid=123 diagnosticId=abc", string.Empty);
        var baseline = await store.SaveAsync(
            "sample",
            first,
            [],
            cancellationToken: TestContext.CancellationToken);
        var loaded = await store.LoadAsync("sample", TestContext.CancellationToken);
        var volatileOnly = CreateExecution([], "pid=999 diagnosticId=xyz", string.Empty);
        var stableChange = CreateExecution([], "pid=999 changed", string.Empty);

        Assert.IsEmpty(BaselineStore.Compare(loaded, volatileOnly));
        Assert.HasCount(1, BaselineStore.Compare(baseline, stableChange));
        Assert.AreEqual("stdout", BaselineStore.Compare(baseline, stableChange).Single().Field);
    }

    private static ExecutionResult CreateExecution(
        IReadOnlyList<string> arguments,
        string stdout,
        string stderr = "")
    {
        _ = JsonEnvelope.TryParse(stdout, out var envelope, out _);
        return new ExecutionResult(
            "command",
            arguments,
            0,
            stdout,
            stderr,
            envelope,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(5),
            false,
            false);
    }
}
