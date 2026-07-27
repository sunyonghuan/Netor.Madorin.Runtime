using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.MConsole.Configuration;
using Madorin.AI.Runtime.MConsole.HostClient;
using Madorin.AI.Runtime.MConsole.Hosting;
using Madorin.AI.Runtime.MConsole.Menu;
using Madorin.AI.Runtime.MConsole.Monitoring;
using Madorin.AI.Runtime.MConsole.Testing;

namespace Madorin.AI.Runtime.MConsole.Diagnostics;

public sealed class P5SmokeRunner(
    MConsoleOptions options,
    MConsolePaths paths,
    string repositoryRoot)
{
    public async Task<P5SmokeReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var processRunner = new ProcessRunner();
        var executable = await new MadorinLocator(processRunner).LocateAsync(
            options.RuntimeExecutablePath,
            repositoryRoot,
            TimeSpan.FromSeconds(options.CommandTimeoutSeconds),
            cancellationToken);
        var fixtureRoot = Path.Combine(paths.StateDirectory, "p5-fixtures");
        var fixtures = new FixtureLibrary(fixtureRoot, executable.Path, processRunner);
        var context = new CaseExecutionContext();
        await fixtures.CreateFreshWorkspaceAsync(context, cancellationToken);
        var workspace = context.Values["workspace"];
        var dataDirectory = context.Values["dataDir"];
        var fixtureCleaned = false;
        try
        {
            var defaults = new CommandDefaults(
                workspace,
                dataDirectory,
                options.DefaultLogLevel,
                NoColor: true,
                Json: true);
            var catalog = CommandCatalog.CreateDefault();
            var command = catalog.Get("version");
            var invocation = ParameterWizard.Build(
                command,
                defaults,
                new Dictionary<string, string?>(StringComparer.Ordinal));
            var before = await FileTreeSnapshotService.CaptureAsync(
                dataDirectory,
                cancellationToken);
            var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            var outcome = await new CommandExecutionService(
                    processRunner,
                    new StringReader(string.Empty),
                    output)
                .ExecuteAsync(
                    executable.Path,
                    invocation,
                    defaults,
                    TimeSpan.FromSeconds(options.CommandTimeoutSeconds),
                    cancellationToken: cancellationToken);
            var execution = outcome.Result
                ?? throw new InvalidOperationException("P5 version execution did not start.");
            var after = await FileTreeSnapshotService.CaptureAsync(
                dataDirectory,
                cancellationToken);
            var definitions = DefaultAssertionFactory.Create(
                command,
                jsonEnabled: true,
                includeNoDataChange: true);
            var assertions = AssertionEngine.Evaluate(execution, definitions, before, after);

            using var history = new ExecutionHistoryStore(paths.HistoryDirectory);
            await history.AppendExecutionAsync(
                command.Id,
                execution,
                assertions,
                workspace,
                cancellationToken);
            var historyCount = (await history.QueryAsync(
                new ExecutionHistoryFilter(CommandPrefix: "version"),
                cancellationToken)).Count;

            var baselines = new BaselineStore(paths.BaselinesDirectory);
            var baseline = await baselines.SaveAsync(
                "p5-version",
                execution,
                definitions,
                cancellationToken: cancellationToken);
            var differences = BaselineStore.Compare(baseline, execution);

            var testCase = new MConsoleTestCase(
                "p5-version-replay",
                [TestCaseStore.RecordCommand(
                    "version",
                    "version",
                    new Dictionary<string, string?>(StringComparer.Ordinal),
                    definitions)],
                new TestFixtureDefinition("fresh-workspace", "cleanup-owned-workspace"));
            var caseStore = new TestCaseStore(paths.CasesDirectory);
            await caseStore.SaveAsync(testCase, cancellationToken);
            var replay = await new TestCaseRunner(
                    executable.Path,
                    catalog,
                    new CommandDefaults(null, null, options.DefaultLogLevel, true, true),
                    processRunner,
                    fixtures)
                .RunAsync(await caseStore.LoadAsync(testCase.Name, cancellationToken), cancellationToken);
            var reportPath = Path.Combine(paths.ReportsDirectory, "p5-version-replay.md");
            await TestCaseReportExporter.ExportMarkdownAsync(
                replay,
                reportPath,
                cancellationToken);

            var monitorCaptured = await CaptureMonitorAsync(
                executable.Path,
                workspace,
                dataDirectory,
                cancellationToken);

            fixtures.CleanupOwnedWorkspace(context);
            fixtureCleaned = !Directory.Exists(workspace);
            return new P5SmokeReport(
                assertions.Count(static item => item.Passed),
                assertions.Count(static item => !item.Passed),
                historyCount,
                differences.Count,
                replay.IsSuccess,
                File.Exists(reportPath),
                monitorCaptured,
                fixtureCleaned);
        }
        finally
        {
            if (!fixtureCleaned && Directory.Exists(workspace))
            {
                fixtures.CleanupOwnedWorkspace(context);
            }
        }
    }

    private async Task<bool> CaptureMonitorAsync(
        string executablePath,
        string workspace,
        string dataDirectory,
        CancellationToken cancellationToken)
    {
        var instanceId = $"p5-{Guid.NewGuid():N}";
        await using var session = await HostClientFactory.ConnectAsync(
            new HostConnectionOptions(
                $"mconsole-p5-{Guid.NewGuid():N}",
                workspace,
                executablePath,
                RuntimeStartPolicy.AlwaysStart,
                OwnedProcessClosePolicy.Shutdown,
                instanceId,
                $"madorin.mconsole.p5.{Guid.NewGuid():N}",
                DataDirectory: dataDirectory,
                ExpectedRuntimeInstanceId: instanceId,
                StartupTimeout: TimeSpan.FromSeconds(options.StartupTimeoutSeconds),
                ReconnectWindow: TimeSpan.FromSeconds(15),
                ReconnectDelay: TimeSpan.FromMilliseconds(100)),
            cancellationToken: cancellationToken);
        var snapshot = await new RuntimeMonitorService(session.Client)
            .CaptureAsync(cancellationToken);
        return string.Equals(
            snapshot.Binding.RuntimeInstanceId,
            instanceId,
            StringComparison.Ordinal)
            && string.Equals(snapshot.Status.RuntimeInstanceId, instanceId, StringComparison.Ordinal);
    }
}
