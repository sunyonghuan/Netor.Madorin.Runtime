using Madorin.AI.Runtime.MConsole.Hosting;
using Madorin.AI.Runtime.MConsole.Menu;
using Madorin.AI.Runtime.MConsole.Testing;
using Madorin.AI.Runtime.MConsole.Tests.TestSupport;

namespace Madorin.AI.Runtime.MConsole.Tests.Testing;

[TestClass]
public sealed class TestCaseAndFixtureTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public void CaseExecutionContext_ResolvesStructuredStepVariablesAndHidesSecrets()
    {
        var context = new CaseExecutionContext();
        using var document = System.Text.Json.JsonDocument.Parse(
            "{\"sessionId\":\"session-1\",\"nested\":{\"gsn\":7}}");
        context.CaptureStepData("first", document.RootElement);
        context.Set("apiKey", "secret-value", isSecret: true);

        var resolved = context.Resolve(
            "session=${sessionId}; gsn=${step.first.data.nested.gsn}");

        Assert.AreEqual("session=session-1; gsn=7", resolved);
        Assert.IsFalse(context.ExportReportVariables().ContainsKey("apiKey"));
        Assert.ThrowsExactly<InvalidDataException>(() => context.Resolve("${missing}"));
    }

    [TestMethod]
    public async Task TestCaseStore_RoundTripsCommandAndHostSteps()
    {
        using var directory = new OwnedTestDirectory();
        var store = new TestCaseStore(directory.Path);
        var testCase = new MConsoleTestCase(
            "roundtrip",
            [
                TestCaseStore.RecordCommand(
                    "version",
                    "version",
                    new Dictionary<string, string?>(StringComparer.Ordinal),
                    [new AssertionDefinition(AssertionKind.ExitCodeEquals, Expected: "0")]),
                TestCaseStore.RecordHostAction(
                    "status",
                    "runtimeStatus",
                    new Dictionary<string, string?>(StringComparer.Ordinal),
                    []),
            ],
            new TestFixtureDefinition("fresh-workspace", "cleanup-owned-workspace"));

        await store.SaveAsync(testCase, TestContext.CancellationToken);
        var loaded = await store.LoadAsync("roundtrip", TestContext.CancellationToken);

        Assert.AreEqual(testCase.Name, loaded.Name);
        Assert.AreEqual(testCase.Fixtures, loaded.Fixtures);
        Assert.AreEqual(TestStepKind.Command, loaded.Steps[0].Kind);
        Assert.AreEqual(TestStepKind.Host, loaded.Steps[1].Kind);
        Assert.HasCount(2, loaded.Steps);
    }

    [TestMethod]
    public async Task FixtureLibrary_OnlyCleansWorkspaceWithMatchingMarker()
    {
        using var directory = new OwnedTestDirectory();
        var fixtureRoot = Path.Combine(directory.Path, "fixtures");
        var library = new FixtureLibrary(fixtureRoot, Environment.ProcessPath!, new ProcessRunner());
        var context = new CaseExecutionContext();
        await library.CreateFreshWorkspaceAsync(context, TestContext.CancellationToken);
        var workspace = context.Values["workspace"];

        File.WriteAllText(Path.Combine(workspace, FixtureLibrary.OwnershipMarker), "tampered");
        Assert.ThrowsExactly<InvalidOperationException>(() => library.CleanupOwnedWorkspace(context));
        Assert.IsTrue(Directory.Exists(workspace));

        File.WriteAllText(
            Path.Combine(workspace, FixtureLibrary.OwnershipMarker),
            context.Values["fixtureOwner"]);
        library.CleanupOwnedWorkspace(context);
        Assert.IsFalse(Directory.Exists(workspace));
    }

    [TestMethod]
    public async Task TestCaseRunner_ExecutesCommandAndExportsReport()
    {
        using var directory = new OwnedTestDirectory();
        var dotnet = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet",
            "dotnet.exe");
        Assert.IsTrue(File.Exists(dotnet), dotnet);
        var command = new CommandDefinition(
            "dotnet.version",
            "dotnet version",
            ["--version"],
            CommandRiskLevel.Safe,
            CommandReadWriteKind.ReadOnly,
            CommandLockKind.None,
            [],
            ["exitCode"]);
        var catalog = new CommandCatalog(
            [new CommandGroup("test", "Test", "Test", [command])]);
        var runner = new ProcessRunner();
        var fixtures = new FixtureLibrary(
            Path.Combine(directory.Path, "fixtures"),
            dotnet,
            runner);
        var testCase = new MConsoleTestCase(
            "dotnet-version",
            [new TestStepDefinition(
                "version",
                TestStepKind.Command,
                command.Id,
                null,
                new Dictionary<string, string?>(StringComparer.Ordinal),
                [
                    new AssertionDefinition(AssertionKind.ExitCodeEquals, Expected: "0"),
                    new AssertionDefinition(AssertionKind.StdoutRegex, Expected: "^10\\."),
                ])],
            new TestFixtureDefinition());
        var report = await new TestCaseRunner(
                dotnet,
                catalog,
                new CommandDefaults(null, null, string.Empty, false, false),
                runner,
                fixtures)
            .RunAsync(testCase, TestContext.CancellationToken);
        var reportPath = Path.Combine(directory.Path, "report.md");
        await TestCaseReportExporter.ExportMarkdownAsync(
            report,
            reportPath,
            TestContext.CancellationToken);

        Assert.IsTrue(report.IsSuccess);
        Assert.AreEqual(1, report.Passed);
        Assert.IsTrue(File.Exists(reportPath));
    }
}
