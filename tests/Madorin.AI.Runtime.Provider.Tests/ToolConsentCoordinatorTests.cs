using Madorin.AI.Runtime.Cli.Tools;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ToolConsentCoordinatorTests
{
    private string _workspace = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _workspace = Path.Combine(
            Path.GetTempPath(),
            "madorin-coordinator-tests",
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
    [DataRow("unavailable", "unavailable")]
    [DataRow("denied", "denied")]
    [DataRow("mismatch", "invalid-response")]
    [DataRow("exception", "handler-error")]
    [DataRow("missing-grant", "invalid-grant")]
    [DataRow("invalid-grant", "invalid-grant")]
    public async Task ResolveAndExecuteAsync_PermissionFailureTerminatesIntentAndLedger(
        string behavior,
        string expectedDecision)
    {
        var descriptor = CreateDescriptor(requiresApproval: false);
        await using var harness = await ConsentHarness.CreateAsync(
            _workspace,
            descriptor,
            new CountingExecutor(descriptor.ToolId),
            TestContext.CancellationToken);
        var invocation = CreateInvocation(descriptor.ToolId, $"call-{behavior}");
        var pending = await harness.CreatePendingAsync(
            invocation,
            ToolGatewayResultKind.NeedsPermission,
            TestContext.CancellationToken);
        var handler = CreateFailureHandler(behavior);
        var coordinator = harness.CreateCoordinator(handler);

        var result = await coordinator.ResolveAndExecuteAsync(
            invocation,
            pending,
            TestContext.CancellationToken);

        Assert.AreEqual(ToolGatewayResultKind.Failed, result.Kind);
        await AssertIntentStatusAsync(harness.Store, invocation, ToolIntentStatus.Failed);
        var consent = await harness.Store.GetApprovalAsync(
            ToolConsentCoordinator.CreateConsentRequestId(
                ToolGatewayResultKind.NeedsPermission,
                invocation),
            TestContext.CancellationToken);
        Assert.IsNotNull(consent);
        Assert.AreEqual("permission", consent.RequestKind);
        Assert.AreEqual(expectedDecision, consent.Decision);
        Assert.IsNotNull(consent.DecidedAt);
    }

    [TestMethod]
    public async Task ResolveAndExecuteAsync_ApprovalUnavailableTerminatesApprovalLedger()
    {
        var descriptor = CreateDescriptor(requiresApproval: true);
        await using var harness = await ConsentHarness.CreateAsync(
            _workspace,
            descriptor,
            new CountingExecutor(descriptor.ToolId),
            TestContext.CancellationToken);
        var invocation = CreateInvocation(descriptor.ToolId, "call-approval-unavailable");
        var pending = await harness.CreatePendingAsync(
            invocation,
            ToolGatewayResultKind.NeedsApproval,
            TestContext.CancellationToken);
        var coordinator = harness.CreateCoordinator(new PermissionOnlyHandler());

        var result = await coordinator.ResolveAndExecuteAsync(
            invocation,
            pending,
            TestContext.CancellationToken);

        Assert.AreEqual(ToolGatewayResultKind.Failed, result.Kind);
        await AssertIntentStatusAsync(harness.Store, invocation, ToolIntentStatus.Failed);
        var consent = await harness.Store.GetApprovalAsync(
            ToolConsentCoordinator.CreateConsentRequestId(
                ToolGatewayResultKind.NeedsApproval,
                invocation),
            TestContext.CancellationToken);
        Assert.IsNotNull(consent);
        Assert.AreEqual("approval", consent.RequestKind);
        Assert.AreEqual("unavailable", consent.Decision);
        Assert.IsNotNull(consent.DecidedAt);
    }

    [TestMethod]
    public async Task ResolveAndExecuteAsync_CancellationFinalizesConsentBeforePropagating()
    {
        var descriptor = CreateDescriptor(requiresApproval: false);
        await using var harness = await ConsentHarness.CreateAsync(
            _workspace,
            descriptor,
            new CountingExecutor(descriptor.ToolId),
            TestContext.CancellationToken);
        var invocation = CreateInvocation(descriptor.ToolId, "call-cancelled");
        var pending = await harness.CreatePendingAsync(
            invocation,
            ToolGatewayResultKind.NeedsPermission,
            TestContext.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        var coordinator = harness.CreateCoordinator(new CancellingHandler(cancellation));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await coordinator.ResolveAndExecuteAsync(
                invocation,
                pending,
                cancellation.Token));

        await AssertIntentStatusAsync(harness.Store, invocation, ToolIntentStatus.Cancelled);
        var consent = await harness.Store.GetApprovalAsync(
            ToolConsentCoordinator.CreateConsentRequestId(
                ToolGatewayResultKind.NeedsPermission,
                invocation),
            TestContext.CancellationToken);
        Assert.IsNotNull(consent);
        Assert.AreEqual("cancelled", consent.Decision);
        Assert.IsNotNull(consent.DecidedAt);
    }

    [TestMethod]
    public async Task ResolveAndExecuteAsync_ApprovalThenPermissionExecutesToolOnce()
    {
        var descriptor = CreateDescriptor(requiresApproval: true);
        var executor = new PermissionOnceExecutor(descriptor.ToolId);
        await using var harness = await ConsentHarness.CreateAsync(
            _workspace,
            descriptor,
            executor,
            TestContext.CancellationToken);
        var invocation = CreateInvocation(descriptor.ToolId, "call-two-stage");
        var pending = await harness.CreatePendingAsync(
            invocation,
            ToolGatewayResultKind.NeedsApproval,
            TestContext.CancellationToken);
        var handler = new GrantingHandler();
        var coordinator = harness.CreateCoordinator(handler);

        var result = await coordinator.ResolveAndExecuteAsync(
            invocation,
            pending,
            TestContext.CancellationToken);

        Assert.AreEqual(ToolGatewayResultKind.Success, result.Kind);
        Assert.AreEqual(2, executor.PreparationCount);
        Assert.AreEqual(1, executor.ExecutionCount);
        Assert.AreEqual(1, handler.ApprovalCount);
        Assert.AreEqual(1, handler.PermissionCount);
        await AssertIntentStatusAsync(harness.Store, invocation, ToolIntentStatus.Succeeded);

        var approval = await harness.Store.GetApprovalAsync(
            ToolConsentCoordinator.CreateConsentRequestId(
                ToolGatewayResultKind.NeedsApproval,
                invocation),
            TestContext.CancellationToken);
        var permission = await harness.Store.GetApprovalAsync(
            ToolConsentCoordinator.CreateConsentRequestId(
                ToolGatewayResultKind.NeedsPermission,
                invocation),
            TestContext.CancellationToken);
        Assert.IsNotNull(approval);
        Assert.IsNotNull(permission);
        Assert.AreEqual("approval", approval.RequestKind);
        Assert.AreEqual("granted", approval.Decision);
        Assert.AreEqual("permission", permission.RequestKind);
        Assert.AreEqual("granted", permission.Decision);
        Assert.IsNotNull(approval.DecidedAt);
        Assert.IsNotNull(permission.DecidedAt);
    }

    private static DelegateConsentHandler? CreateFailureHandler(string behavior) => behavior switch
    {
        "unavailable" => null,
        "denied" => new DelegateConsentHandler(permission: context =>
            new ToolPermissionResponse(
                context.Request.CorrelationId ?? string.Empty,
                context.Request.CallId,
                ToolAuthorizationDecision.Denied,
                Reason: "denied")),
        "mismatch" => new DelegateConsentHandler(permission: context =>
            new ToolPermissionResponse(
                "wrong-correlation",
                context.Request.CallId,
                ToolAuthorizationDecision.Denied,
                Reason: "mismatch")),
        "exception" => new DelegateConsentHandler(permissionException: true),
        "missing-grant" => new DelegateConsentHandler(permission: context =>
            new ToolPermissionResponse(
                context.Request.CorrelationId ?? string.Empty,
                context.Request.CallId,
                ToolAuthorizationDecision.Granted,
                Grant: null,
                Reason: "missing")),
        "invalid-grant" => new DelegateConsentHandler(permission: context =>
            new ToolPermissionResponse(
                context.Request.CorrelationId ?? string.Empty,
                context.Request.CallId,
                ToolAuthorizationDecision.Granted,
                CreateGrant(
                    context.Invocation,
                    context.Descriptor,
                    context.WorkspaceRoot,
                    DateTimeOffset.UtcNow.AddMinutes(-1)),
                "expired")),
        _ => throw new ArgumentOutOfRangeException(nameof(behavior), behavior, null)
    };

    private static ToolDescriptor CreateDescriptor(bool requiresApproval) =>
        new(
            "custom.consent",
            "custom",
            "Consent test",
            "Exercises tool consent.",
            """{"type":"object","additionalProperties":false}""",
            """{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":false}""",
            RequiresApproval: requiresApproval,
            Risk: ToolRiskLevel.Low,
            ExecutionTarget: ToolExecutionTarget.Builtin,
            IsIdempotent: false);

    private static ToolInvocation CreateInvocation(string toolId, string callId) =>
        new(
            callId,
            toolId,
            "agent-1",
            ParentAgentId: null,
            "{}",
            "run-1",
            "session-1",
            "invocation-1");

    private static ToolGrant CreateGrant(
        ToolInvocation invocation,
        ToolDescriptor descriptor,
        string workspaceRoot,
        DateTimeOffset? expiresAt = null) =>
        new(
            $"grant-{Guid.NewGuid():N}",
            invocation.RunId,
            workspaceRoot,
            [workspaceRoot],
            [workspaceRoot],
            AllowOverwrite: false,
            AllowMove: false,
            AllowDelete: false,
            AllowedExecutables: [],
            AllowPowerShell: false,
            new NetworkPolicy(true, []),
            expiresAt ?? DateTimeOffset.UtcNow.AddHours(1),
            AllowDelegation: false,
            DelegatedAgentIds: [],
            AllowedToolIds: [descriptor.ToolId],
            AllowedCallIds: [invocation.CallId],
            MaximumRisk: descriptor.Risk,
            AllowedEnvironmentVariables: [],
            AgentId: invocation.AgentId);

    private static async Task AssertIntentStatusAsync(
        SqliteToolIntentRepository store,
        ToolInvocation invocation,
        ToolIntentStatus expected)
    {
        var intent = await store.GetIntentAsync(invocation.CallId);
        Assert.IsNotNull(intent);
        Assert.AreEqual(expected, intent.Status);
        Assert.AreNotEqual(ToolIntentStatus.Pending, intent.Status);
        Assert.IsNotNull(intent.CompletedAt);
    }

    private sealed class ConsentHarness : IAsyncDisposable
    {
        private ConsentHarness(
            string workspace,
            SqliteConnection connection,
            SqliteToolIntentRepository store,
            SqliteEventOutbox outbox,
            ToolAuthorizationService authorization,
            ToolGateway gateway,
            ToolCatalogSnapshot catalog)
        {
            Workspace = workspace;
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

        public string Workspace { get; }

        public static async Task<ConsentHarness> CreateAsync(
            string workspace,
            ToolDescriptor descriptor,
            IToolExecutor executor,
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
            var gateway = new ToolGateway(
                new ToolCatalogStore(new BuiltinToolRegistry(), validator),
                validator,
                authorization,
                [executor],
                store,
                outbox);
            return new ConsentHarness(
                workspace,
                connection,
                store,
                outbox,
                authorization,
                gateway,
                new ToolCatalogSnapshot("host-v1", "custom-v1", [descriptor]));
        }

        public ToolConsentCoordinator CreateCoordinator(IToolConsentHandler? handler) =>
            new(
                Gateway,
                Authorization,
                Catalog,
                handler,
                new FixedScopeResolver(),
                Workspace,
                Store);

        public async Task<ToolGatewayResult> CreatePendingAsync(
            ToolInvocation invocation,
            ToolGatewayResultKind expected,
            CancellationToken ct)
        {
            var pending = await Gateway.ExecuteAsync(
                invocation,
                permission: null,
                Catalog,
                ct);
            Assert.AreEqual(expected, pending.Kind);
            var intent = await Store.GetIntentAsync(invocation.CallId, ct);
            Assert.IsNotNull(intent);
            Assert.AreEqual(ToolIntentStatus.Pending, intent.Status);
            Assert.IsNull(intent.CompletedAt);
            return pending;
        }

        public async ValueTask DisposeAsync()
        {
            Gateway.Dispose();
            Outbox.Dispose();
            Store.Dispose();
            await Connection.DisposeAsync();
        }
    }

    private sealed class FixedScopeResolver : IToolConsentScopeResolver
    {
        public ToolConsentScopeResolution Resolve(
            ToolInvocation invocation,
            ToolDescriptor descriptor,
            string workspaceRoot) =>
            ToolConsentScopeResolution.Success(
                new ToolRequestedScope(
                    $"tool:{descriptor.ToolId}",
                    "fixed test scope",
                    [workspaceRoot],
                    [workspaceRoot],
                    AllowOverwrite: false,
                    AllowMove: false,
                    AllowDelete: false,
                    AllowedExecutables: [],
                    AllowedEnvironmentVariables: [],
                    AllowPowerShell: false,
                    new NetworkPolicy(true, []),
                    CanGrantForRun: false));
    }

    private sealed class DelegateConsentHandler(
        Func<ToolPermissionConsentContext, ToolPermissionResponse>? permission = null,
        bool permissionException = false) : IToolConsentHandler
    {
        public Task<ToolPermissionResponse> RequestPermissionAsync(
            ToolPermissionConsentContext context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return permissionException
                ? Task.FromException<ToolPermissionResponse>(
                    new InvalidOperationException("handler failed"))
                : Task.FromResult(permission!(context));
        }

        public Task<ApprovalResponse> RequestApprovalAsync(
            ToolApprovalConsentContext context,
            CancellationToken ct) =>
            Task.FromException<ApprovalResponse>(new NotSupportedException());
    }

    private sealed class PermissionOnlyHandler : IToolConsentHandler
    {
        public bool CanRequestApproval => false;

        public Task<ToolPermissionResponse> RequestPermissionAsync(
            ToolPermissionConsentContext context,
            CancellationToken ct) =>
            Task.FromException<ToolPermissionResponse>(new NotSupportedException());

        public Task<ApprovalResponse> RequestApprovalAsync(
            ToolApprovalConsentContext context,
            CancellationToken ct) =>
            Task.FromException<ApprovalResponse>(new NotSupportedException());
    }

    private sealed class CancellingHandler(CancellationTokenSource cancellation)
        : IToolConsentHandler
    {
        public Task<ToolPermissionResponse> RequestPermissionAsync(
            ToolPermissionConsentContext context,
            CancellationToken ct)
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation was not propagated.");
        }

        public Task<ApprovalResponse> RequestApprovalAsync(
            ToolApprovalConsentContext context,
            CancellationToken ct) =>
            Task.FromException<ApprovalResponse>(new NotSupportedException());
    }

    private sealed class GrantingHandler : IToolConsentHandler
    {
        private int _approvalCount;
        private int _permissionCount;

        public int ApprovalCount => Volatile.Read(ref _approvalCount);

        public int PermissionCount => Volatile.Read(ref _permissionCount);

        public Task<ToolPermissionResponse> RequestPermissionAsync(
            ToolPermissionConsentContext context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _permissionCount);
            return Task.FromResult(new ToolPermissionResponse(
                context.Request.CorrelationId ?? string.Empty,
                context.Request.CallId,
                ToolAuthorizationDecision.Granted,
                CreateGrant(
                    context.Invocation,
                    context.Descriptor,
                    context.WorkspaceRoot),
                "permission granted"));
        }

        public Task<ApprovalResponse> RequestApprovalAsync(
            ToolApprovalConsentContext context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _approvalCount);
            return Task.FromResult(new ApprovalResponse(
                context.Request.CorrelationId,
                context.Request.ApprovalRequestId,
                ToolAuthorizationDecision.Granted,
                CreateGrant(
                    context.Invocation!,
                    context.Descriptor!,
                    context.WorkspaceRoot),
                "approval granted"));
        }
    }

    private sealed class CountingExecutor(string toolId) : IToolExecutor
    {
        private int _executionCount;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public bool CanExecute(string candidateToolId) =>
            string.Equals(toolId, candidateToolId, StringComparison.Ordinal);

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _executionCount);
            return Task.FromResult(new ToolResult(
                invocation.CallId,
                invocation.ToolId,
                Success: true,
                "{\"ok\":true}"));
        }
    }

    private sealed class PermissionOnceExecutor(string toolId) : IToolExecutor
    {
        private int _executionCount;
        private int _preparationCount;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public int PreparationCount => Volatile.Read(ref _preparationCount);

        public bool CanExecute(string candidateToolId) =>
            string.Equals(toolId, candidateToolId, StringComparison.Ordinal);

        public ValueTask<IPreparedToolExecution> PrepareAsync(
            ToolInvocation invocation,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _preparationCount) == 1)
            {
                return ValueTask.FromException<IPreparedToolExecution>(
                    new ToolPermissionRequiredException("A second permission is required."));
            }

            return ValueTask.FromResult<IPreparedToolExecution>(
                new PreparedExecution(this, invocation));
        }

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken ct = default) =>
            ExecutePreparedAsync(invocation, ct);

        private Task<ToolResult> ExecutePreparedAsync(
            ToolInvocation invocation,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _executionCount);
            return Task.FromResult(new ToolResult(
                invocation.CallId,
                invocation.ToolId,
                Success: true,
                "{\"ok\":true}"));
        }

        private sealed class PreparedExecution(
            PermissionOnceExecutor owner,
            ToolInvocation invocation) : IPreparedToolExecution
        {
            public string TargetSummary => "tool:custom.consent";

            public Task<ToolResult> ExecuteAsync(CancellationToken ct = default) =>
                owner.ExecutePreparedAsync(invocation, ct);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
