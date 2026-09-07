using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Provider.Tests;

/// <summary>
/// 验证 Host 风格「人审 Granted + 空 AllowedExecutables Grant」在 Runtime 用 scope 定稿后，
/// mcli_process 能二次执行成功——不依赖 UI 点击。
/// </summary>
[TestClass]
public sealed class ProcessConsentChainTests
{
    private string _workspace = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _workspace = Path.Combine(
            Path.GetTempPath(),
            "madorin-process-consent",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [TestMethod]
    public async Task HostEmptyExecutableGrant_WithScopeResolver_ProcessRunsAfterAutoConsent()
    {
        await using var harness = await ProcessConsentHarness.CreateAsync(
            _workspace,
            TestContext.CancellationToken);
        var invocation = new ToolInvocation(
            "call-process-1",
            BuiltinToolRegistry.ProcessRunToolId,
            "agent-expert",
            ParentAgentId: null,
            """{"fileName":"dotnet","arguments":["--version"]}""",
            "run-process-1",
            "session-process-1",
            "invocation-process-1");

        var pending = await harness.Gateway.ExecuteAsync(
            invocation,
            permission: null,
            harness.Catalog,
            TestContext.CancellationToken);
        Assert.AreEqual(
            ToolGatewayResultKind.NeedsPermission,
            pending.Kind,
            pending.Error?.Message);

        // 模拟 Workstation HostToolGrantFactory：同意了，但 AllowedExecutables 为空。
        var handler = new HostStyleEmptyExecutableGrantHandler();
        var coordinator = new ToolConsentCoordinator(
            harness.Gateway,
            harness.Authorization,
            harness.Catalog,
            handler,
            new BuiltinToolConsentScopeResolver(),
            _workspace,
            harness.Store);

        var result = await coordinator.ResolveAndExecuteAsync(
            invocation,
            pending,
            TestContext.CancellationToken);

        Assert.AreEqual(1, handler.PermissionCount);
        Assert.AreEqual(
            ToolGatewayResultKind.Success,
            result.Kind,
            result.Error?.Message ?? result.Result?.Error);
        Assert.IsNotNull(result.Result);
        Assert.IsTrue(result.Result.Success, result.Result.Error);
        using var output = JsonDocument.Parse(result.Result.OutputJson!);
        Assert.AreEqual(0, output.RootElement.GetProperty("exitCode").GetInt32());
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(output.RootElement.GetProperty("stdout").GetString()));
    }

    [TestMethod]
    public async Task HostEmptyExecutableGrant_WithoutScopeResolver_FailsWithNoProgress()
    {
        await using var harness = await ProcessConsentHarness.CreateAsync(
            _workspace,
            TestContext.CancellationToken);
        var invocation = new ToolInvocation(
            "call-process-2",
            BuiltinToolRegistry.ProcessRunToolId,
            "agent-expert",
            ParentAgentId: null,
            """{"fileName":"dotnet","arguments":["--version"]}""",
            "run-process-2",
            "session-process-2",
            "invocation-process-2");

        var pending = await harness.Gateway.ExecuteAsync(
            invocation,
            permission: null,
            harness.Catalog,
            TestContext.CancellationToken);
        Assert.AreEqual(ToolGatewayResultKind.NeedsPermission, pending.Kind);

        var handler = new HostStyleEmptyExecutableGrantHandler();
        // 复现修复前 Host 路径：无 scopeResolver，空 Grant 二次仍 NeedsPermission。
        var coordinator = new ToolConsentCoordinator(
            harness.Gateway,
            harness.Authorization,
            harness.Catalog,
            handler,
            scopeResolver: null,
            _workspace,
            harness.Store);

        var result = await coordinator.ResolveAndExecuteAsync(
            invocation,
            pending,
            TestContext.CancellationToken);

        Assert.AreEqual(ToolGatewayResultKind.Failed, result.Kind);
        StringAssert.Contains(
            result.Error?.Message ?? string.Empty,
            "permission Grant did not make progress");
    }

    [TestMethod]
    public async Task FileDelete_HostEmptyGrant_WithScopeResolver_StillSucceeds()
    {
        var target = Path.Combine(_workspace, "to-delete.txt");
        await File.WriteAllTextAsync(target, "x", TestContext.CancellationToken);

        await using var harness = await ProcessConsentHarness.CreateAsync(
            _workspace,
            TestContext.CancellationToken);
        var relative = Path.GetRelativePath(_workspace, target).Replace('\\', '/');
        var invocation = new ToolInvocation(
            "call-delete-1",
            BuiltinToolRegistry.FileDeleteToolId,
            "agent-expert",
            ParentAgentId: null,
            $$"""{"path":"{{relative}}"}""",
            "run-delete-1",
            "session-delete-1",
            "invocation-delete-1");

        var pending = await harness.Gateway.ExecuteAsync(
            invocation,
            permission: null,
            harness.Catalog,
            TestContext.CancellationToken);
        Assert.AreEqual(ToolGatewayResultKind.NeedsPermission, pending.Kind, pending.Error?.Message);

        var coordinator = new ToolConsentCoordinator(
            harness.Gateway,
            harness.Authorization,
            harness.Catalog,
            new HostStyleEmptyExecutableGrantHandler(),
            new BuiltinToolConsentScopeResolver(),
            _workspace,
            harness.Store);

        var result = await coordinator.ResolveAndExecuteAsync(
            invocation,
            pending,
            TestContext.CancellationToken);

        Assert.AreEqual(
            ToolGatewayResultKind.Success,
            result.Kind,
            result.Error?.Message ?? result.Result?.Error);
        Assert.IsFalse(File.Exists(target));
    }

    /// <summary>仿 HostToolGrantFactory：Granted 但 AllowedExecutables 恒为空。</summary>
    private sealed class HostStyleEmptyExecutableGrantHandler : IToolConsentHandler
    {
        private int _permissionCount;

        public int PermissionCount => Volatile.Read(ref _permissionCount);

        public Task<ToolPermissionResponse> RequestPermissionAsync(
            ToolPermissionConsentContext context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _permissionCount);
            var grantId = $"host-{Guid.NewGuid():N}";
            var grant = new ToolGrant(
                grantId,
                context.Invocation.RunId,
                context.WorkspaceRoot,
                AllowedReadRoots: [context.WorkspaceRoot],
                AllowedWriteRoots: [context.WorkspaceRoot],
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
                "host auto-approved for test"));
        }

        public Task<ApprovalResponse> RequestApprovalAsync(
            ToolApprovalConsentContext context,
            CancellationToken ct) =>
            Task.FromException<ApprovalResponse>(new NotSupportedException());
    }

    private sealed class ProcessConsentHarness : IAsyncDisposable
    {
        private ProcessConsentHarness(
            SqliteConnection connection,
            SqliteToolIntentRepository store,
            SqliteEventOutbox outbox,
            ToolAuthorizationService authorization,
            ToolGateway gateway,
            ToolCatalogSnapshot catalog)
        {
            Connection = connection;
            Store = store;
            Outbox = outbox;
            Authorization = authorization;
            Gateway = gateway;
            Catalog = catalog;
        }

        public ToolAuthorizationService Authorization { get; }

        public ToolCatalogSnapshot Catalog { get; }

        public SqliteConnection Connection { get; }

        public ToolGateway Gateway { get; }

        public SqliteEventOutbox Outbox { get; }

        public SqliteToolIntentRepository Store { get; }

        public static async Task<ProcessConsentHarness> CreateAsync(
            string workspace,
            CancellationToken ct)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(ct);
            await SqliteSchema.EnsureCreatedAsync(connection, ct);
            var store = new SqliteToolIntentRepository(connection);
            var outbox = new SqliteEventOutbox(connection);
            var authorization = new ToolAuthorizationService(
                store,
                RuntimeToolPolicy.CreateRestricted(workspace));
            var validator = new JsonSchemaToolValidator();
            var catalogStore = new ToolCatalogStore(new BuiltinToolRegistry(), validator);
            var gateway = new ToolGateway(
                catalogStore,
                validator,
                authorization,
                [
                    new BuiltinFsToolExecutor(),
                    new BuiltinProcessToolExecutor()
                ],
                store,
                outbox);
            return new ProcessConsentHarness(
                connection,
                store,
                outbox,
                authorization,
                gateway,
                catalogStore.CaptureSnapshot());
        }

        public async ValueTask DisposeAsync()
        {
            Gateway.Dispose();
            Outbox.Dispose();
            Store.Dispose();
            await Connection.DisposeAsync();
        }
    }
}
