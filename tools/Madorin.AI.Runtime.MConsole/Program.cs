using Madorin.AI.Runtime.MConsole.Configuration;
using Madorin.AI.Runtime.MConsole.LiveHost;
using Madorin.AI.Runtime.MConsole.Diagnostics;
using Madorin.AI.Runtime.MConsole.Menu;

namespace Madorin.AI.Runtime.MConsole;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellationSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            if (DebugInterrupt.TryConsume())
            {
                return;
            }

            cancellationSource.Cancel();
        };

        try
        {
            var configurationPath = GetOption(args, "--config");
            var options = await MConsoleOptionsLoader.LoadAsync(
                configurationPath,
                cancellationSource.Token);
            ApplyCommandLineOverrides(options, args);
            options.Validate();

            var paths = MConsolePaths.Create(options.MConsoleHome);
            paths.EnsureCreated();
            var repositoryRoot = FindRepositoryRoot(Environment.CurrentDirectory)
                ?? Environment.CurrentDirectory;

            if (args.Contains("--help", StringComparer.Ordinal))
            {
                PrintHelp();
                return 0;
            }

            if (args.Contains("--smoke-p0", StringComparer.Ordinal))
            {
                var report = await new P0SmokeRunner(options, repositoryRoot)
                    .RunAsync(cancellationSource.Token);
                Console.WriteLine($"runtime={report.Executable.Path}");
                Console.WriteLine($"source={report.Executable.Source}");
                Console.WriteLine($"versionExit={report.Executable.ValidationResult.ExitCode}");
                Console.WriteLine($"doctorExit={report.Doctor.ExitCode}");
                Console.WriteLine($"servePid={report.StartedServe.ProcessId}");
                Console.WriteLine($"serveExit={report.StoppedServe.ExitCode}");
                Console.WriteLine($"secretLeak={report.SecretLeakDetected}");
                Console.WriteLine($"passed={report.Passed}");
                return report.Passed ? 0 : 1;
            }

            if (args.Contains("--smoke-p1", StringComparer.Ordinal))
            {
                var report = await new P1SmokeRunner(options, repositoryRoot)
                    .RunAsync(cancellationSource.Token);
                Console.WriteLine($"attach={report.AttachExisting.ActualStartBranch}");
                Console.WriteLine($"startIfMissingExisting={report.StartIfMissingAttach.ActualStartBranch}");
                Console.WriteLine($"startIfMissingMissing={report.StartIfMissingStart.ActualStartBranch}");
                Console.WriteLine($"alwaysStart={report.AlwaysStart.ActualStartBranch}");
                Console.WriteLine($"ownedProcessesStopped={report.OwnedProcessesStopped}");
                Console.WriteLine($"passed={report.Passed}");
                return report.Passed ? 0 : 1;
            }

            if (args.Contains("--smoke-p2", StringComparer.Ordinal))
            {
                var report = await new P2SmokeRunner(options, paths, repositoryRoot)
                    .RunAsync(cancellationSource.Token);
                Console.WriteLine($"permission={report.PermissionDecision}");
                Console.WriteLine($"dangerousApproval={report.DangerousApprovalDecision}");
                Console.WriteLine($"echoExecutions={report.EchoExecutionCount}");
                Console.WriteLine($"replay={report.ReplayedCallStatus}");
                Console.WriteLine($"query={report.QueryStatus}");
                Console.WriteLine($"cancellationAccepted={report.CancellationAccepted}");
                Console.WriteLine($"cancelledCall={report.CancelledCallStatus}");
                Console.WriteLine($"history={report.HistoryCount}");
                Console.WriteLine($"passed={report.Passed}");
                return report.Passed ? 0 : 1;
            }

            if (args.Contains("--smoke-p3", StringComparer.Ordinal))
            {
                var report = await new P3SmokeRunner(options, paths, repositoryRoot)
                    .RunAsync(cancellationSource.Token);
                Console.WriteLine($"runtime={report.Reconnect.RuntimeInstanceId}");
                Console.WriteLine($"previousPid={report.Reconnect.PreviousProcessId}");
                Console.WriteLine($"currentPid={report.Reconnect.CurrentProcessId}");
                Console.WriteLine($"protocol={report.Reconnect.CurrentProtocolVersion}");
                Console.WriteLine($"recoveryStates={report.PersistedRecoveryCount}");
                Console.WriteLine($"identityPreserved={report.RuntimeIdentityPreserved}");
                Console.WriteLine($"cursorPreserved={report.CursorPreserved}");
                Console.WriteLine($"ownedProcessStopped={report.OwnedProcessStopped}");
                Console.WriteLine($"passed={report.Passed}");
                return report.Passed ? 0 : 1;
            }

            if (args.Contains("--smoke-p4", StringComparer.Ordinal))
            {
                var report = await new P4SmokeRunner(options, repositoryRoot)
                    .RunAsync(cancellationSource.Token);
                Console.WriteLine($"groups={report.GroupCount}");
                Console.WriteLine($"commands={report.CommandCount}");
                foreach (var command in report.Commands)
                {
                    Console.WriteLine($"{command.CommandId}={command.ExitCode}");
                }

                Console.WriteLine($"runConflictBlocked={report.RunConflictBlocked}");
                Console.WriteLine($"sessionDeleteBlocked={report.SessionDeleteBlocked}");
                Console.WriteLine($"storageConflictBlocked={report.StorageConflictBlocked}");
                Console.WriteLine($"passed={report.Passed}");
                return report.Passed ? 0 : 1;
            }

            if (args.Contains("--smoke-p5", StringComparer.Ordinal))
            {
                var report = await new P5SmokeRunner(options, paths, repositoryRoot)
                    .RunAsync(cancellationSource.Token);
                Console.WriteLine($"assertionsPassed={report.AssertionPassed}");
                Console.WriteLine($"assertionsFailed={report.AssertionFailed}");
                Console.WriteLine($"history={report.HistoryCount}");
                Console.WriteLine($"baselineDifferences={report.BaselineDifferenceCount}");
                Console.WriteLine($"replayPassed={report.ReplayPassed}");
                Console.WriteLine($"reportExported={report.ReportExported}");
                Console.WriteLine($"monitorCaptured={report.MonitorCaptured}");
                Console.WriteLine($"fixtureCleaned={report.FixtureCleaned}");
                Console.WriteLine($"passed={report.Passed}");
                return report.Passed ? 0 : 1;
            }

            if (args.Contains("--smoke-p6", StringComparer.Ordinal))
            {
                var report = await new P6SmokeRunner(options, paths, repositoryRoot)
                    .RunAsync(cancellationSource.Token);
                Console.WriteLine($"instances={report.RunningInstanceCount}");
                Console.WriteLine($"bindingsIsolated={report.RuntimeBindingsIsolated}");
                Console.WriteLine($"lockConflictExit={report.LockConflictExitCode}");
                Console.WriteLine($"lockConflictNoDataChange={report.LockConflictNoDataChange}");
                Console.WriteLine($"aliasConflictExit={report.AliasConflictExitCode}");
                Console.WriteLine($"readOnlyPassed={report.ReadOnlyCommandsPassed}");
                Console.WriteLine($"maximumRunningRuns={report.MaximumObservedRunningRuns}");
                Console.WriteLine($"rejectedRuns={report.RejectedRunCount}");
                Console.WriteLine($"runLimitRespected={report.RunLimitRespected}");
                Console.WriteLine($"cancelIsolated={report.CrossInstanceCancelIsolated}");
                Console.WriteLine($"reportExported={report.ReportExported}");
                Console.WriteLine($"ownedProcessesStopped={report.OwnedProcessesStopped}");
                Console.WriteLine($"fixturesCleaned={report.FixturesCleaned}");
                Console.WriteLine($"passed={report.Passed}");
                return report.Passed ? 0 : 1;
            }

            if (args.Contains("--debug", StringComparer.Ordinal))
            {
                return await DebugSession.RunAsync(
                    options,
                    paths,
                    repositoryRoot,
                    cancellationSource.Token);
            }

            return await new MenuLoop(
                    options,
                    paths,
                    repositoryRoot,
                    Console.In,
                    Console.Out)
                .RunAsync(cancellationSource.Token);
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }
    }

    private static void ApplyCommandLineOverrides(MConsoleOptions options, string[] args)
    {
        options.RuntimeExecutablePath = GetOption(args, "--runtime") ?? options.RuntimeExecutablePath;
        options.DefaultWorkspace = GetOption(args, "--workspace") ?? options.DefaultWorkspace;
        options.DefaultDataDirectory = GetOption(args, "--data-dir") ?? options.DefaultDataDirectory;
        options.MConsoleHome = GetOption(args, "--home") ?? options.MConsoleHome;
    }

    private static string? GetOption(string[] args, string option)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], option, StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static string? FindRepositoryRoot(string startDirectory)
    {
        for (var directory = new DirectoryInfo(startDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Madorin.AI.Runtime.slnx")))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Madorin.AI.Runtime.MConsole");
        Console.WriteLine("  --config <path>     Load an alternate MConsole configuration file");
        Console.WriteLine("  --runtime <path>    Use an explicit madorin executable");
        Console.WriteLine("  --workspace <path>  Set the default test workspace");
        Console.WriteLine("  --data-dir <path>   Set the default Runtime data directory");
        Console.WriteLine("  --home <path>       Set the MConsole-owned data directory");
        Console.WriteLine("  --smoke-p0          Run the non-interactive P0 acceptance chain");
        Console.WriteLine("  --smoke-p1          Run the non-interactive Client SDK policy chain");
        Console.WriteLine("  --smoke-p2          Run the host callback and durable tool chain");
        Console.WriteLine("  --smoke-p3          Run the reconnect and persisted recovery chain");
        Console.WriteLine("  --smoke-p4          Run the standalone command catalog chain");
        Console.WriteLine("  --smoke-p5          Run assertions, history, case, fixture, and monitor chain");
        Console.WriteLine("  --smoke-p6          Run concurrency and multi-instance acceptance chain");
        Console.WriteLine("  --debug             Open the live host console for mode, provider, model, output, and events");
    }
}
