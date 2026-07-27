using Madorin.AI.Runtime.MConsole.Configuration;
using Madorin.AI.Runtime.MConsole.Diagnostics;
using Madorin.AI.Runtime.MConsole.HostClient;
using Madorin.AI.Runtime.MConsole.Hosting;
using Madorin.AI.Runtime.MConsole.Monitoring;
using Madorin.AI.Runtime.MConsole.Testing;
using Madorin.AI.Runtime.Client;

namespace Madorin.AI.Runtime.MConsole.Menu;

public sealed class MenuLoop(
    MConsoleOptions options,
    MConsolePaths paths,
    string repositoryRoot,
    TextReader input,
    TextWriter output)
{
    private readonly CommandCatalog _catalog = CommandCatalog.CreateDefault();
    private readonly ProcessRunner _processRunner = new();

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await PrintMainMenuAsync();
            var selection = await input.ReadLineAsync(cancellationToken);
            if (selection is "0" or null)
            {
                return 0;
            }

            if (int.TryParse(selection, out var groupIndex)
                && groupIndex >= 1
                && groupIndex <= _catalog.Groups.Count)
            {
                await RunGroupAsync(_catalog.Groups[groupIndex - 1], cancellationToken);
                continue;
            }

            switch (selection)
            {
                case "14":
                    await PrintDefaultsAsync();
                    break;
                case "9":
                    await RunHistoryPanelAsync(cancellationToken);
                    break;
                case "10":
                    await RunCasesPanelAsync(cancellationToken);
                    break;
                case "11":
                    await RunFixturePanelAsync(cancellationToken);
                    break;
                case "12":
                    await RunMonitorPanelAsync(cancellationToken);
                    break;
                case "13":
                    await RunConcurrencyPanelAsync(cancellationToken);
                    break;
                default:
                    await output.WriteLineAsync("Unknown selection.");
                    break;
            }
        }

        return 130;
    }

    private async Task PrintMainMenuAsync()
    {
        await output.WriteLineAsync("Madorin MConsole");
        await output.WriteLineAsync($"Data home: {paths.HomeDirectory}");
        for (var index = 0; index < _catalog.Groups.Count; index++)
        {
            await output.WriteLineAsync($"{index + 1}. {_catalog.Groups[index].Title}");
        }

        await output.WriteLineAsync("9. 结果断言与执行历史");
        await output.WriteLineAsync("10. 用例录制与回放");
        await output.WriteLineAsync("11. 数据夹具准备与清理");
        await output.WriteLineAsync("12. serve 实时监控看板");
        await output.WriteLineAsync("13. 并发与多实例场景");
        await output.WriteLineAsync("14. 目标二进制与默认参数设置");
        await output.WriteLineAsync("0. 退出");
        await output.WriteAsync("> ");
    }

    private async Task RunGroupAsync(
        CommandGroup group,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await output.WriteLineAsync(group.Title);
            for (var index = 0; index < group.Items.Length; index++)
            {
                var command = group.Items[index];
                await output.WriteLineAsync(
                    $"{index + 1}. [{command.RiskLevel}] {command.DisplayName}");
            }

            await output.WriteLineAsync("0. 返回");
            await output.WriteAsync("> ");
            var selection = await input.ReadLineAsync(cancellationToken);
            if (selection is "0" or null)
            {
                return;
            }

            if (!int.TryParse(selection, out var commandIndex)
                || commandIndex < 1
                || commandIndex > group.Items.Length)
            {
                await output.WriteLineAsync("Unknown selection.");
                continue;
            }

            await ExecuteCommandAsync(group.Items[commandIndex - 1], cancellationToken);
        }
    }

    private async Task ExecuteCommandAsync(
        CommandDefinition definition,
        CancellationToken cancellationToken)
    {
        var located = await LocateAsync(cancellationToken);
        var defaults = CommandDefaults.FromOptions(options);
        var invocation = await new ParameterWizard(input, output)
            .CollectAsync(definition, defaults, cancellationToken);
        var validation = CommandInvocationValidator.Validate(invocation, defaults);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                await output.WriteLineAsync("Validation: " + error);
            }

            return;
        }

        if (definition.Id == "serve")
        {
            await RunServeAsync(located.Path, defaults, invocation, cancellationToken);
            return;
        }

        if (definition.Id == "run"
            && !invocation.Values.ContainsKey("--input")
            && !invocation.Values.ContainsKey("--input-file"))
        {
            await RunReplAsync(located.Path, defaults, invocation, cancellationToken);
            return;
        }

        if (definition.Id.StartsWith("skill.", StringComparison.Ordinal)
            && definition.ContractNote is not null)
        {
            await output.WriteLineAsync(definition.ContractNote);
        }

        var outcome = await new CommandExecutionService(_processRunner, input, output)
            .ExecuteAsync(
                located.Path,
                invocation,
                defaults,
                TimeSpan.FromSeconds(options.CommandTimeoutSeconds),
                cancellationToken: cancellationToken);
        if (outcome.Result is { } result)
        {
            await PrintResultAsync(result);
        }
    }

    private async Task RunServeAsync(
        string executablePath,
        CommandDefaults defaults,
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!await DangerousCommandGuard.ConfirmAsync(
            invocation,
            defaults,
            input,
            output,
            cancellationToken: cancellationToken))
        {
            await output.WriteLineAsync("Command cancelled before process start.");
            return;
        }

        var workspace = defaults.Workspace
            ?? throw new InvalidOperationException("serve requires a configured workspace.");
        await using var host = new ServeHost(new ServeHostOptions(
            executablePath,
            workspace,
            GetValue(invocation, "--instance"),
            GetValue(invocation, "--pipe-prefix"),
            defaults.DataDirectory,
            MaxRuns: GetInteger(invocation, "--max-runs"),
            StartupTimeout: TimeSpan.FromSeconds(options.StartupTimeoutSeconds)));
        var started = await host.StartAsync(cancellationToken: cancellationToken);
        await output.WriteLineAsync($"serve started, PID={started.ProcessId}");
        await output.WriteAsync("Press Enter to stop: ");
        _ = await input.ReadLineAsync(cancellationToken);
        var stopped = await host.StopAsync(cancellationToken);
        await output.WriteLineAsync($"serve stopped, exit={stopped.ExitCode}");
    }

    private async Task RunReplAsync(
        string executablePath,
        CommandDefaults defaults,
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        await using var session = new ReplSession(
            executablePath,
            invocation.Arguments,
            defaults.Workspace,
            invocation.Environment);
        await session.StartAsync(cancellationToken);
        var outputPump = Task.Run(async () =>
        {
            await foreach (var line in session.ReadOutputAsync(cancellationToken))
            {
                await output.WriteLineAsync(line.Text);
            }
        }, CancellationToken.None);
        while (!session.Completion.IsCompleted && !cancellationToken.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                await session.CompleteInputAsync(cancellationToken);
                break;
            }

            await session.SendLineAsync(line, cancellationToken);
            if (line is "/exit" or "/quit")
            {
                break;
            }
        }

        await session.Completion.WaitAsync(cancellationToken);
        await outputPump.WaitAsync(cancellationToken);
    }

    private async Task<LocatedExecutable> LocateAsync(CancellationToken cancellationToken) =>
        await new MadorinLocator(_processRunner).LocateAsync(
            options.RuntimeExecutablePath,
            repositoryRoot,
            TimeSpan.FromSeconds(options.CommandTimeoutSeconds),
            cancellationToken);

    private async Task PrintDefaultsAsync()
    {
        var located = await LocateAsync(CancellationToken.None);
        var defaults = CommandDefaults.FromOptions(options);
        await output.WriteLineAsync($"Executable: {located.Path} ({located.Source})");
        await output.WriteLineAsync($"Workspace: {defaults.Workspace ?? "<not configured>"}");
        await output.WriteLineAsync($"Data directory: {defaults.DataDirectory ?? "<default>"}");
        await output.WriteLineAsync($"Log level: {defaults.LogLevel}");
        await output.WriteLineAsync($"JSON: {defaults.Json}; no-color: {defaults.NoColor}");
    }

    private async Task RunHistoryPanelAsync(CancellationToken cancellationToken)
    {
        using var history = new ExecutionHistoryStore(paths.HistoryDirectory);
        var entries = await history.QueryAsync(cancellationToken: cancellationToken);
        if (entries.Count == 0)
        {
            await output.WriteLineAsync("暂无执行历史。");
            return;
        }

        foreach (var entry in entries.Take(50))
        {
            await output.WriteLineAsync(
                $"{entry.StartedAt:O} | {entry.CommandOrHostAction} | exit={entry.ExitCode} | " +
                $"passed={entry.AssertionPassed} failed={entry.AssertionFailed}");
        }

        var reportPath = Path.Combine(paths.ReportsDirectory, "execution-history.md");
        await history.ExportMarkdownAsync(reportPath, cancellationToken: cancellationToken);
        await output.WriteLineAsync($"已导出：{reportPath}");
    }

    private async Task RunCasesPanelAsync(CancellationToken cancellationToken)
    {
        var store = new TestCaseStore(paths.CasesDirectory);
        var names = store.ListNames();
        if (names.Count == 0)
        {
            await output.WriteLineAsync("暂无已录制用例。");
            return;
        }

        for (var index = 0; index < names.Count; index++)
        {
            await output.WriteLineAsync($"{index + 1}. {names[index]}");
        }

        await output.WriteAsync("选择要回放的用例，输入 0 返回：");
        var selected = await input.ReadLineAsync(cancellationToken);
        if (!int.TryParse(selected, out var selectedIndex)
            || selectedIndex < 1
            || selectedIndex > names.Count)
        {
            return;
        }

        var located = await LocateAsync(cancellationToken);
        var fixtures = new FixtureLibrary(
            Path.Combine(paths.StateDirectory, "fixtures"),
            located.Path,
            _processRunner);
        var runner = new TestCaseRunner(
            located.Path,
            _catalog,
            CommandDefaults.FromOptions(options),
            _processRunner,
            fixtures);
        var testCase = await store.LoadAsync(names[selectedIndex - 1], cancellationToken);
        var report = await runner.RunAsync(testCase, cancellationToken);
        var reportPath = Path.Combine(paths.ReportsDirectory, testCase.Name + ".md");
        await TestCaseReportExporter.ExportMarkdownAsync(report, reportPath, cancellationToken);
        await output.WriteLineAsync(
            $"回放完成：passed={report.Passed}, failed={report.Failed}, " +
            $"cleanup={(report.FixtureCleanupError is null ? "ok" : "failed")}，报告={reportPath}");
    }

    private async Task RunFixturePanelAsync(CancellationToken cancellationToken)
    {
        var located = await LocateAsync(cancellationToken);
        var fixtures = new FixtureLibrary(
            Path.Combine(paths.StateDirectory, "fixtures"),
            located.Path,
            _processRunner);
        var context = new CaseExecutionContext();
        await fixtures.CreateFreshWorkspaceAsync(context, cancellationToken);
        var workspace = context.Values["workspace"];
        try
        {
            await output.WriteLineAsync($"已创建受控夹具工作区：{workspace}");
            await output.WriteAsync("按 Enter 校验所有权哨兵并清理：");
            _ = await input.ReadLineAsync(cancellationToken);
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                fixtures.CleanupOwnedWorkspace(context);
            }
        }

        await output.WriteLineAsync("夹具工作区已清理。");
    }

    private async Task RunMonitorPanelAsync(CancellationToken cancellationToken)
    {
        var defaults = CommandDefaults.FromOptions(options);
        var workspace = defaults.Workspace;
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            await output.WriteLineAsync("实时监控需要已存在的默认工作区。");
            return;
        }

        var located = await LocateAsync(cancellationToken);
        var instanceId = $"mconsole-monitor-{Guid.NewGuid():N}";
        await using var session = await HostClientFactory.ConnectAsync(
            new HostConnectionOptions(
                $"mconsole-monitor-host-{Guid.NewGuid():N}",
                workspace,
                located.Path,
                RuntimeStartPolicy.AlwaysStart,
                OwnedProcessClosePolicy.Shutdown,
                instanceId,
                $"madorin.mconsole.monitor.{Guid.NewGuid():N}",
                DataDirectory: defaults.DataDirectory,
                ExpectedRuntimeInstanceId: instanceId,
                StartupTimeout: TimeSpan.FromSeconds(options.StartupTimeoutSeconds)),
            cancellationToken: cancellationToken);
        var monitor = new RuntimeMonitorService(session.Client);
        while (!cancellationToken.IsCancellationRequested)
        {
            var snapshot = await monitor.CaptureAsync(cancellationToken);
            await output.WriteLineAsync(
                $"{snapshot.CapturedAt:O} | instance={snapshot.Status.RuntimeInstanceId} | " +
                $"pid={snapshot.Status.ProcessId} | active={snapshot.Status.ActiveRunCount} | " +
                $"hosts={snapshot.Status.ConnectedHostCount} | sessions={snapshot.Sessions.Sessions.Length} | " +
                $"runs={snapshot.Runs.Runs.Length}");
            await output.WriteAsync("按 Enter 刷新，输入 q 停止：");
            if (string.Equals(
                await input.ReadLineAsync(cancellationToken),
                "q",
                StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
    }

    private async Task RunConcurrencyPanelAsync(CancellationToken cancellationToken)
    {
        await output.WriteLineAsync("将创建两个受控工作区并运行完整 P6 多实例验收链。");
        await output.WriteAsync("输入 run 开始，其他输入返回：");
        if (!string.Equals(
            await input.ReadLineAsync(cancellationToken),
            "run",
            StringComparison.Ordinal))
        {
            return;
        }

        var report = await new P6SmokeRunner(options, paths, repositoryRoot)
            .RunAsync(cancellationToken);
        await output.WriteLineAsync(
            $"instances={report.RunningInstanceCount}, lockExit={report.LockConflictExitCode}, " +
            $"aliasExit={report.AliasConflictExitCode}, maxRunning={report.MaximumObservedRunningRuns}, " +
            $"rejected={report.RejectedRunCount}, stopped={report.OwnedProcessesStopped}, " +
            $"cleaned={report.FixturesCleaned}, passed={report.Passed}");
    }

    private async Task PrintResultAsync(ExecutionResult result)
    {
        await output.WriteLineAsync($"exit={result.ExitCode}, duration={result.Duration}");
        if (!string.IsNullOrEmpty(result.StandardOutput))
        {
            await output.WriteLineAsync(result.StandardOutput);
        }

        if (!string.IsNullOrEmpty(result.StandardError))
        {
            await output.WriteLineAsync(result.StandardError);
        }
    }

    private static string? GetValue(CommandInvocation invocation, string key) =>
        invocation.Values.TryGetValue(key, out var value) ? value : null;

    private static int? GetInteger(CommandInvocation invocation, string key) =>
        int.TryParse(
            GetValue(invocation, key),
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
}
