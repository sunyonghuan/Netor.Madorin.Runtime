using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Server.ProviderWorkers;

if (args.Length == 4 && args[0] == "--timed-read")
{
    var started = DateTimeOffset.UtcNow;
    await Task.Delay(2000);
    var content = await File.ReadAllTextAsync(args[1]);
    var result = JsonSerializer.Serialize(new { label = args[2], started, ended = DateTimeOffset.UtcNow, content });
    await File.WriteAllTextAsync(args[3], result);
    Console.WriteLine(result);
    return;
}

EnsureProviderWorkerDirectory();
var output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/real-smoke-20260915");
var scenario = args.Length > 1 ? args[1] : "all";
await Smoke.RunAsync(output, scenario);

/// <summary>
/// Provider 已与宿主脱离：Runtime 通过协议拉起同目录/指定目录的 madorin-provider-*.exe。
/// 冒烟若未显式设置 MADORIN_PROVIDER_DIR，则优先使用构建拷贝的 providers/ 目录。
/// </summary>
static void EnsureProviderWorkerDirectory()
{
    if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MADORIN_PROVIDER_DIR")))
    {
        return;
    }

    var bundled = Path.Combine(AppContext.BaseDirectory, "providers");
    var deepseek = Path.Combine(bundled, "madorin-provider-deepseek.exe");
    if (File.Exists(deepseek))
    {
        Environment.SetEnvironmentVariable("MADORIN_PROVIDER_DIR", bundled);
        Console.WriteLine($"[provider] MADORIN_PROVIDER_DIR={bundled}");
    }
}

internal static partial class Smoke
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly ConcurrentDictionary<string, List<RuntimeEventEnvelope>> Events = new();
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<RuntimeEventEnvelope>> Ends = new();
    private static readonly ConcurrentDictionary<string, string> SessionIds = new();
    private static readonly ConcurrentDictionary<string, string> Labels = new();
    private static readonly ConcurrentDictionary<string, byte> CancelOnStep = new();
    private static string _root = "";
    private static string _workspace = "";
    private static string _provider = "";
    private static string _model = "";
    private static RuntimeClient _client = null!;
    private static Task _eventReader = Task.CompletedTask;
    private static bool _interruptMeeting;

    public static async Task RunAsync(string output, string scenario)
    {
        _root = output;
        _workspace = Path.Combine(output, "workspace");
        Directory.CreateDirectory(_workspace);
        await File.WriteAllTextAsync(Path.Combine(_workspace, "sales.csv"), "orderId,market,amount\nA001,CN,1200\nA002,US,800\n");
        await File.WriteAllTextAsync(Path.Combine(_workspace, "refunds.csv"), "refundId,amount\nR001,150\n");
        var config = await new StandaloneConfigLoader().LoadAsync() ?? throw new InvalidOperationException("缺少全局配置");
        _provider = Environment.GetEnvironmentVariable("SMOKE_PROVIDER") ?? config.DefaultProvider;
        _model = Environment.GetEnvironmentVariable("SMOKE_MODEL") ?? config.DefaultModel;
        var entry = config.Providers.Single(p => p.Name == _provider);
        var connection = new ProviderConnectionConfig(entry.Name, entry.Protocol, entry.BaseUrl, entry.ApiKey,
            entry.AuthenticationHeader, entry.AuthenticationScheme, entry.UserAgent, entry.ApiVersion,
            entry.Models, entry.ConfigurationVersion, entry.Capabilities, entry.ProbePath, entry.MaxTools);
        Save("environment.json", new { started = DateTimeOffset.Now, provider = _provider, model = _model,
            endpoint = entry.BaseUrl, protocol = entry.Protocol, sourceCommit = "9d5ad99492efb7d790055ad6f2489cf3fbd36bfb" });
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var workerSupervisor = new ProviderWorkerSupervisor();
        await using var server = await RuntimeServer.StartAsync(new RuntimeServerOptions(_workspace)
        {
            InstanceId = Guid.NewGuid().ToString("N"), PipePrefix = "madorin.real-smoke." + Guid.NewGuid().ToString("N"),
            HandshakeSecret = secret, MemoryUserHome = Path.Combine(output, "isolated-home"),
            WorkerSupervisor = workerSupervisor,
            ProviderResolver = _ => workerSupervisor.CreateAdapter(connection.Protocol, connection),
            MinimumLogLevel = RuntimeDiagnosticLogLevel.Warning,
            RunTimeout = TimeSpan.FromMinutes(8)
        });
        using var shutdown = new CancellationTokenSource();
        var serving = server.RunAsync(shutdown.Token);
        await using var client = new RuntimeClient(new RuntimeClientOptions(server.PipeName, Guid.NewGuid().ToString("N"),
            HandshakeSecret: secret, WorkspaceDirectory: _workspace,
            Capabilities: new RuntimeCapabilities(ReverseRpc: true, ToolCatalog: true, ToolPermissions: true),
            HostCallbacks: Callbacks()));
        _client = client;
        await client.ConnectAsync();
        Save("capabilities.json", client.NegotiatedCapabilities);
        var reading = ReadEventsAsync(shutdown.Token);
        _eventReader = reading;
        try
        {
            if (scenario is "all" or "expert") { await ExpertAsync(); }
            if (scenario is "all" or "work") { await WorkAsync(); }
            if (scenario is "all" or "meeting" or "meeting-strict") { await MeetingAsync(scenario == "meeting-strict"); }
            if (scenario == "timed") { await TimedAsync(); }
            if (scenario == "work-execute") { await ApprovedWorkAsync(WorkSelection()); }
            if (scenario == "work-interrupt") { await InterruptedWorkAsync(WorkSelection()); }
            if (scenario == "work-discuss") { await DiscussWorkAsync(WorkSelection()); }
            if (scenario == "work-revision") { await InterruptedWorkAsync(WorkSelection(), true); }
            if (scenario == "meeting-interrupt") { _interruptMeeting = true; await MeetingAsync(true, true); }
            if (scenario == "meeting-controlled") { await MeetingAsync(true, false, true); }
            if (scenario == "work-confirm") { await WorkConfirmAsync(); }
            if (scenario == "work-set-plan") { await WorkSetPlanAsync(); }
            if (scenario == "work-ask-pending") { await WorkAskPendingAsync(); }
        }
        finally
        {
            await shutdown.CancelAsync();
            try { await reading; } catch (OperationCanceledException) { }
            try { await serving; } catch (OperationCanceledException) { }
        }
    }

    private static AgentRef Agent(string id, string prompt) => new(id, "smoke-v1", prompt, _provider, _model,
        AllowedToolIds: ["mcli_fs_read", "mcli_fs_write", "mcli_fs_inspect", "mcli_fs_list", "mcli_process"]);
    private static NextTurnSelection Select(RuntimeMode mode, ModeOptions options) =>
        new(1, mode, new DefaultSelection(_provider, _model), options);
    private static void Save(string path, object? value) =>
        File.WriteAllText(Path.Combine(_root, path), JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
}
