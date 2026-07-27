using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.MConsole.Concurrency;
using Madorin.AI.Runtime.MConsole.Configuration;
using Madorin.AI.Runtime.MConsole.HostClient;
using Madorin.AI.Runtime.MConsole.Hosting;
using Madorin.AI.Runtime.MConsole.Monitoring;
using Madorin.AI.Runtime.MConsole.Testing;

namespace Madorin.AI.Runtime.MConsole.Diagnostics;

public sealed class P6SmokeRunner(
    MConsoleOptions options,
    MConsolePaths paths,
    string repositoryRoot)
{
    public async Task<P6SmokeReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var processRunner = new ProcessRunner();
        var executable = await new MadorinLocator(processRunner).LocateAsync(
            options.RuntimeExecutablePath,
            repositoryRoot,
            TimeSpan.FromSeconds(options.CommandTimeoutSeconds),
            cancellationToken);
        var fixtureRoot = Path.Combine(paths.StateDirectory, "p6-fixtures");
        var fixtures = new FixtureLibrary(fixtureRoot, executable.Path, processRunner);
        var contextA = new CaseExecutionContext();
        var contextB = new CaseExecutionContext();
        await fixtures.CreateFreshWorkspaceAsync(contextA, cancellationToken);
        await fixtures.CreateFreshWorkspaceAsync(contextB, cancellationToken);
        var workspaceA = contextA.Values["workspace"];
        var workspaceB = contextB.Values["workspace"];
        var dataA = contextA.Values["dataDir"];
        var dataB = contextB.Values["dataDir"];
        var stopped = false;
        var cleaned = false;
        var phase = "start-instances";
        await using var pool = new ServeHostPool(executable.Path);
        try
        {
            var definitionA = CreateDefinition("a", workspaceA, dataA, maxRuns: 1);
            var definitionB = CreateDefinition("b", workspaceB, dataB, maxRuns: 2);
            await Task.WhenAll(
                pool.StartAsync(definitionA, cancellationToken),
                pool.StartAsync(definitionB, cancellationToken));
            var snapshots = pool.GetSnapshots();
            var bindingsIsolated = snapshots.Select(static item => item.BoundRuntimeInstanceId)
                .Distinct(StringComparer.Ordinal).Count() == 2
                && snapshots.Select(static item => item.Process.ProcessId).Distinct().Count() == 2;

            phase = "lock-contention";
            var beforeConflict = await FileTreeSnapshotService.CaptureAsync(dataA, cancellationToken);
            var lockExit = await CaptureStartFailureExitCodeAsync(
                pool,
                CreateDefinition("lock-conflict", workspaceA, dataA),
                cancellationToken);
            var afterConflict = await FileTreeSnapshotService.CaptureAsync(dataA, cancellationToken);
            var lockNoDataChange = !FileTreeSnapshotService.Compare(beforeConflict, afterConflict).HasChanges;

            phase = "path-alias-contention";
            var aliasWorkspace = workspaceA.TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var aliasExit = await CaptureStartFailureExitCodeAsync(
                pool,
                CreateDefinition("alias-conflict", aliasWorkspace, dataA),
                cancellationToken);

            phase = "read-only-concurrency";
            var scenario = CreateReadOnlyScenario(workspaceA, dataA);
            var readOnlyReport = await new ConcurrencyScenarioRunner(executable.Path)
                .RunAsync(scenario, cancellationToken);
            var reportPath = Path.Combine(paths.ReportsDirectory, "p6-concurrency.md");
            await ConcurrencyReportExporter.ExportMarkdownAsync(
                readOnlyReport,
                reportPath,
                cancellationToken);

            phase = "run-capacity-and-cancel-isolation";
            var (maximumRunning, rejectedRuns, limitRespected, cancelIsolated) = await ExerciseRunsAsync(
                pool,
                definitionA,
                definitionB,
                cancellationToken);

            phase = "owned-process-convergence";
            await pool.StopAsync("a", cancellationToken);
            await pool.StopAsync("b", cancellationToken);
            stopped = pool.GetSnapshots().Count == 0;
            fixtures.CleanupOwnedWorkspace(contextA);
            fixtures.CleanupOwnedWorkspace(contextB);
            cleaned = !Directory.Exists(workspaceA) && !Directory.Exists(workspaceB);
            return new P6SmokeReport(
                snapshots.Count,
                bindingsIsolated,
                lockExit,
                lockNoDataChange,
                aliasExit,
                readOnlyReport.IsSuccess,
                maximumRunning,
                rejectedRuns,
                limitRespected,
                cancelIsolated,
                File.Exists(reportPath),
                stopped,
                cleaned);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"P6 smoke phase '{phase}' failed: {ex.Message}", ex);
        }
        finally
        {
            await pool.DisposeAsync();
            if (!cleaned)
            {
                if (Directory.Exists(workspaceA))
                {
                    fixtures.CleanupOwnedWorkspace(contextA);
                }

                if (Directory.Exists(workspaceB))
                {
                    fixtures.CleanupOwnedWorkspace(contextB);
                }
            }
        }
    }

    private static ServeHostDefinition CreateDefinition(
        string id,
        string workspace,
        string dataDirectory,
        int maxRuns = 1) => new(
        id,
        workspace,
        dataDirectory,
        Path.Combine(workspace, ".mconsole-logs", id),
        $"p6-{id}-{Guid.NewGuid():N}",
        $"madorin.mconsole.p6.{id}.{Guid.NewGuid():N}",
        maxRuns);

    private static async Task<int?> CaptureStartFailureExitCodeAsync(
        ServeHostPool pool,
        ServeHostDefinition definition,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await pool.StartAsync(definition, cancellationToken);
            await pool.StopAsync(definition.Id, cancellationToken);
            return 0;
        }
        catch (ServeHostPoolStartException ex)
        {
            return ex.ProcessSnapshot?.ExitCode;
        }
    }

    private static ConcurrencyScenario CreateReadOnlyScenario(string workspace, string dataDirectory) => new(
        "p6-read-only-under-write-lock",
        new ConcurrencyResourceLimits(MaxParallelSteps: 3, TimeoutSeconds: 60),
        [new ConcurrencyWorkspace("a", workspace, dataDirectory)],
        [],
        [
            new ConcurrentStep(
                "db-check",
                "a",
                ["db", "check", "--workspace", workspace, "--json", "--no-color"]),
            new ConcurrentStep(
                "storage-check",
                "a",
                ["storage", "check", "--workspace", workspace, "--json", "--no-color"]),
            new ConcurrentStep(
                "session-list",
                "a",
                ["session", "list", "--workspace", workspace, "--json", "--no-color"]),
        ],
        [new AggregateExpectation("allNoDataChange", ["db-check", "storage-check", "session-list"])]);

    private static async Task<(int MaximumRunning, int RejectedRuns, bool LimitRespected, bool CancelIsolated)> ExerciseRunsAsync(
        ServeHostPool pool,
        ServeHostDefinition definitionA,
        ServeHostDefinition definitionB,
        CancellationToken cancellationToken)
    {
        var clientA = pool.GetClient("a");
        var clientB = pool.GetClient("b");
        var conductorA = new RunConductor(clientA, definitionA.InstanceId);
        var conductorB = new RunConductor(clientB, definitionB.InstanceId);
        var draft = new RunRequestDraft(
            RuntimeMode.Expert,
            "P6 concurrency probe",
            new RunAgentDraft("p6-agent", "missing-provider", "missing-model", "1"));
        var monitorA = new RuntimeMonitorService(clientA);
        var maximumRunning = 0;
        var sampler = Task.Run(async () =>
        {
            for (var sample = 0; sample < 20; sample++)
            {
                var snapshot = await monitorA.CaptureAsync(cancellationToken);
                maximumRunning = Math.Max(maximumRunning, snapshot.Status.ActiveRunCount);
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
        }, CancellationToken.None);
        var submissionTasks = Enumerable.Range(0, 3)
            .Select(_ => TryStartAsync(conductorA, draft, cancellationToken))
            .ToArray();
        var submissionB = await conductorB.StartNewSessionAsync(draft, cancellationToken);
        var attempts = await Task.WhenAll(submissionTasks);
        await sampler;
        var submissionsA = attempts.Where(static item => item.Submission is not null)
            .Select(static item => item.Submission!)
            .ToArray();
        var rejectedRuns = attempts.Count(static item => item.CapacityRejected);

        var beforeB = await conductorB.QueryAsync(submissionB.RunId, cancellationToken);
        if (submissionsA.Length > 0)
        {
            _ = await conductorA.CancelAsync(submissionsA[0].RunId, "P6 targeted cancellation", cancellationToken);
        }

        var afterB = await conductorB.QueryAsync(submissionB.RunId, cancellationToken);
        return (
            maximumRunning,
            rejectedRuns,
            maximumRunning <= definitionA.MaxRuns && rejectedRuns > 0,
            beforeB.RunId == afterB.RunId && beforeB.Status == afterB.Status);
    }

    private static async Task<RunStartAttempt> TryStartAsync(
        RunConductor conductor,
        RunRequestDraft draft,
        CancellationToken cancellationToken)
    {
        try
        {
            return new RunStartAttempt(
                await conductor.StartNewSessionAsync(draft, cancellationToken),
                CapacityRejected: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            && ex.Message.Contains("capacity", StringComparison.OrdinalIgnoreCase))
        {
            return new RunStartAttempt(null, CapacityRejected: true);
        }
    }

    private sealed record RunStartAttempt(RunSubmission? Submission, bool CapacityRejected);
}
