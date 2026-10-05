using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.MConsole.Callbacks;
using Madorin.AI.Runtime.MConsole.Configuration;
using Madorin.AI.Runtime.MConsole.HostClient;
using Madorin.AI.Runtime.MConsole.Hosting;

namespace Madorin.AI.Runtime.MConsole.LiveHost;

/// <summary>调试台入口。拉起当前 CLI，把模型原文和事件分开展示。</summary>
public static partial class DebugSession
{
    public static async Task<int> RunAsync(
        MConsoleOptions options,
        MConsolePaths paths,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        var located = await new MadorinLocator(new ProcessRunner()).LocateAsync(
            options.RuntimeExecutablePath,
            repositoryRoot,
            TimeSpan.FromSeconds(options.StartupTimeoutSeconds),
            cancellationToken);
        var config = DebugConfigSnapshot.Load(DebugConfigDirectory());
        var workspace = ResolveWorkspace(options.DefaultWorkspace);
        var dataDirectory = string.IsNullOrWhiteSpace(options.DefaultDataDirectory)
            ? Path.Combine(workspace, ".madorin-debug")
            : Path.GetFullPath(options.DefaultDataDirectory);
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(dataDirectory);

        var runtimeId = "debug" + Guid.NewGuid().ToString("N")[..8];
        using var toolStore = new ToolCallResultStore(Path.Combine(paths.StateDirectory, "debug-tools"));
        var callbacks = new CallbackCoordinator(
                CallbackPolicy.DenyAll,
                new ConservativeToolGrantFactory(workspace),
                new ToolCallExecutionCoordinator(toolStore),
                new RejectingToolExecutor())
            .CreateCallbacks(
                runtimeId,
                options.MaxCallbackConcurrency,
                TimeSpan.FromSeconds(options.CallbackTimeoutSeconds));
        var connection = new HostConnectionOptions(
            "mconsole-" + runtimeId,
            workspace,
            located.Path,
            RuntimeStartPolicy.AlwaysStart,
            OwnedProcessClosePolicy.Shutdown,
            runtimeId,
            DataDirectory: dataDirectory,
            StartupTimeout: TimeSpan.FromSeconds(options.StartupTimeoutSeconds));
        var host = await HostClientFactory.ConnectAsync(connection, callbacks, cancellationToken);
        var transcript = new DebugTranscript();
        var viewport = new DebugViewport();
        await using var sessionLifetime = host;
        using var pumpCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pump = new DebugEventPump(host.Client, transcript, () => Refresh(transcript, viewport));
        var turn = CreateTurn(host, pump, config, located.Path, workspace);
        WriteBanner(turn, located.Path, dataDirectory);
        var pumpTask = pump.PumpAsync(pumpCancel.Token);
        try
        {
            return await ReadLoopAsync(turn, transcript, pump, viewport, cancellationToken);
        }
        finally
        {
            await pumpCancel.CancelAsync();
            DebugInterrupt.Bind(null);
            await ObservePumpAsync(pumpTask);
        }
    }

    private static DebugTurnClient CreateTurn(
        HostClientSession host,
        DebugEventPump pump,
        DebugConfigSnapshot config,
        string executablePath,
        string workspace)
    {
        var provider = config.DefaultProvider ?? string.Empty;
        if (provider.Length == 0 && config.Providers.Count > 0)
        {
            provider = config.Providers[0].Name;
        }

        var model = config.DefaultModel ?? string.Empty;
        if (model.Length == 0)
        {
            for (var index = 0; index < config.Providers.Count; index++)
            {
                var candidate = config.Providers[index];
                if (candidate.Name == provider && candidate.Models.Count > 0)
                {
                    model = candidate.Models[0];
                    break;
                }
            }
        }
        return new DebugTurnClient
        {
            Host = host,
            Pump = pump,
            Config = config,
            ExecutablePath = executablePath,
            Workspace = workspace,
            ProviderId = provider,
            ModelId = model,
        };
    }

    private static void WriteBanner(DebugTurnClient turn, string executablePath, string dataDirectory)
    {
        Console.WriteLine("Madorin 调试台");
        Console.WriteLine("CLI: " + executablePath);
        Console.WriteLine("工作区: " + turn.Workspace);
        Console.WriteLine("数据目录: " + dataDirectory);
        Console.WriteLine(turn.Config.Describe());
        Console.WriteLine($"当前 模式={turn.Mode} 厂商={Show(turn.ProviderId)} 模型={Show(turn.ModelId)}");
        Console.WriteLine(DebugCommandParser.HelpText);
        Console.Write("> ");
    }

    private static string Show(string value) => string.IsNullOrWhiteSpace(value) ? "(未设置)" : value;

    private static string DebugConfigDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".madorin");

    private static string ResolveWorkspace(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Directory.GetCurrentDirectory();
        }

        return Path.GetFullPath(configured);
    }

    private static void Refresh(DebugTranscript transcript, DebugViewport viewport)
    {
        if (viewport.Origin < 0 || Console.IsOutputRedirected)
        {
            return;
        }

        var width = Math.Max(20, Console.WindowWidth - 1);
        var frame = transcript.Capture(viewport.OutputRows, viewport.EventRows, width);
        DebugLiveView.Render(viewport.Origin, frame);
    }

    private static async Task ObservePumpAsync(Task pumpTask)
    {
        try
        {
            await pumpTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class RejectingToolExecutor : IToolCallExecutor
    {
        public ValueTask<ToolCallResponse> ExecuteAsync(
            ToolCallRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ToolCallResponse>(
                new InvalidOperationException("调试台不执行 host.* 工具。"));
    }
}
