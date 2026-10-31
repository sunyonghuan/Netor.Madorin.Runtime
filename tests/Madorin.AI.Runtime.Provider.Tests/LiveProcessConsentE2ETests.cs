using System.Text.Json;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Server.ProviderWorkers;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;

namespace Madorin.AI.Runtime.Provider.Tests;

/// <summary>
/// 使用 ~/.madorin/config.json 真实模型 + 自动同意 Handler，验证
/// mcli_command 授权→scope 定稿→继续执行 全链路（无需点 UI）。
/// </summary>
[TestClass]
public sealed class LiveProcessConsentE2ETests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task Expert_AutoConsent_ProcessDotnetVersion_CompletesSuccessfully()
    {
        var loader = new StandaloneConfigLoader();
        var config = await loader.LoadAsync(TestContext.CancellationToken);
        if (config is null
            || string.IsNullOrWhiteSpace(config.DefaultProvider)
            || string.IsNullOrWhiteSpace(config.DefaultModel))
        {
            Assert.Inconclusive("缺少 ~/.madorin/config.json 或 defaultProvider/defaultModel。");
            return;
        }

        var entry = config.Providers.FirstOrDefault(p =>
            string.Equals(p.Name, config.DefaultProvider, StringComparison.Ordinal));
        if (entry is null || string.IsNullOrWhiteSpace(entry.ApiKey))
        {
            Assert.Inconclusive($"Provider '{config.DefaultProvider}' 未配置或缺少 ApiKey。");
            return;
        }

        var workerSupervisor = new ProviderWorkerSupervisor();
        var provider = StandaloneProviderFactory.CreateAdapter(config, entry.Name, workerSupervisor);

        var root = Path.Combine(
            Path.GetTempPath(),
            "madorin-live-process-consent",
            Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(data);

        var autoConsent = new HostStyleAutoConsentHandler();
        try
        {
            var options = new LocalRuntimeOptions(
                workspace,
                data,
                Path.Combine(data, "logs"),
                Guid.NewGuid().ToString("N"),
                _ => provider)
            {
                WorkerSupervisor = workerSupervisor,
                ToolConsentHandler = autoConsent,
                MinimumLogLevel = RuntimeDiagnosticLogLevel.Warning
            };

            await using var runtime = await LocalRuntime.StartAsync(
                options,
                TestContext.CancellationToken);

            var selection = new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection(config.DefaultProvider, config.DefaultModel),
                new ExpertModeOptions(
                    new AgentRef(
                        "live-process-tester",
                        "1.0",
                        """
                        You are a tool-calling test agent. When asked to run a process, you MUST call the
                        mcli_command tool exactly once with script "dotnet --version".
                        Do not refuse. Do not invent other tools. After the tool result, reply with one short line.
                        """,
                        config.DefaultProvider,
                        config.DefaultModel)));

            var request = new NewSessionRunRequest(
                $"req-{Guid.NewGuid():N}",
                $"turn-{Guid.NewGuid():N}",
                RuntimeMode.Expert,
                selection,
                [
                    new TextContentBlock(
                        "Please run mcli_command now: fileName=dotnet, arguments=[\"--version\"].")
                ]);

            var envelopes = new List<RuntimeEventEnvelope>();
            await foreach (var envelope in runtime.RunAsync(request, TestContext.CancellationToken))
            {
                envelopes.Add(envelope);
            }

            Assert.IsGreaterThanOrEqualTo(
                1,
                autoConsent.PermissionCount,
                "模型应触发 mcli_command，从而走至少一次自动 permission 同意。");

            var allJson = string.Join(
                '\n',
                envelopes.Select(static e => $"{e.MessageType}:{e.Payload.GetRawText()}"));
            var completed = envelopes.Any(static e =>
                string.Equals(e.MessageType, MessageTypes.RunCompleted, StringComparison.Ordinal)
                || string.Equals(e.MessageType, MessageTypes.InvocationCompleted, StringComparison.Ordinal));
            var failed = envelopes.Any(static e =>
                string.Equals(e.MessageType, MessageTypes.RunFailed, StringComparison.Ordinal)
                || string.Equals(e.MessageType, MessageTypes.InvocationFailed, StringComparison.Ordinal));

            Assert.IsFalse(
                allJson.Contains("permission Grant did not make progress", StringComparison.Ordinal),
                "不应再出现 Host 空 AllowedExecutables 导致的二次授权空转。");
            Assert.IsFalse(
                allJson.Contains("No executable is allowed", StringComparison.Ordinal),
                "Grant 定稿后应允许 dotnet 可执行文件。");
            Assert.IsFalse(failed, $"Run 失败：{allJson}");
            Assert.IsTrue(
                completed || allJson.Contains("exitCode", StringComparison.Ordinal),
                $"未看到完成事件或 process 输出。PermissionCount={autoConsent.PermissionCount}, events={envelopes.Count}, dump={allJson}");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                    // 忽略清理失败
                }
            }
        }
    }

    /// <summary>模拟 Host：Granted + 空 AllowedExecutables；Runtime scope 负责补全。</summary>
    private sealed class HostStyleAutoConsentHandler : IToolConsentHandler
    {
        private int _permissionCount;

        public int PermissionCount => Volatile.Read(ref _permissionCount);

        public Task<ToolPermissionResponse> RequestPermissionAsync(
            ToolPermissionConsentContext context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _permissionCount);
            var grantId = $"live-host-{Guid.NewGuid():N}";
            var root = string.IsNullOrWhiteSpace(context.WorkspaceRoot)
                ? Path.GetTempPath()
                : context.WorkspaceRoot;
            var grant = new ToolGrant(
                grantId,
                context.Invocation.RunId,
                root,
                AllowedReadRoots: [root],
                AllowedWriteRoots: [root],
                AllowOverwrite: true,
                AllowMove: true,
                AllowDelete: true,
                AllowedExecutables: [],
                AllowPowerShell: false,
                new NetworkPolicy(DenyAll: true, AllowedHosts: []),
                DateTimeOffset.UtcNow.AddHours(4),
                AllowDelegation: false,
                DelegatedAgentIds: [],
                AllowedToolIds: [context.Invocation.ToolId],
                AllowedCallIds: [context.Invocation.CallId],
                MaximumRisk: context.Descriptor.Risk,
                AllowedEnvironmentVariables: [],
                AgentId: context.Invocation.AgentId,
                ParentGrantId: null,
                RootGrantId: grantId,
                DelegationChain: [],
                ApprovalRequestId: context.Request.ApprovalRequestId);
            return Task.FromResult(new ToolPermissionResponse(
                context.Request.CorrelationId ?? string.Empty,
                context.Request.CallId,
                ToolAuthorizationDecision.Granted,
                grant,
                "live auto-approved (host-empty-executables)"));
        }

        public Task<ApprovalResponse> RequestApprovalAsync(
            ToolApprovalConsentContext context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ApprovalResponse(
                context.Request.CorrelationId,
                context.Request.ApprovalRequestId,
                ToolAuthorizationDecision.Granted,
                Grant: null,
                Reason: "live auto-approved approval"));
        }
    }
}
