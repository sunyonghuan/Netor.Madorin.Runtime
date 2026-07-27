using System.Text.Json;

using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.MConsole.Callbacks;
using Madorin.AI.Runtime.MConsole.Tests.TestSupport;

namespace Madorin.AI.Runtime.MConsole.Tests.Callbacks;

[TestClass]
public sealed class CallbackCoordinatorTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task ManualPermission_QueuesAndRecordsAuditableResponse()
    {
        using var fixture = new CoordinatorFixture(CallbackPolicy.Manual);
        var callbacks = fixture.Coordinator.CreateCallbacks("runtime-a");
        var request = CreatePermissionRequest("manual-call");

        var pendingResponse = callbacks.ToolPermissionRequestedAsync!(
            request,
            TestContext.CancellationToken).AsTask();
        var pending = await WaitForPendingAsync(fixture.Coordinator);
        var response = new ToolPermissionResponse(
            request.CorrelationId!,
            request.CallId,
            ToolAuthorizationDecision.Denied,
            Reason: "operator denied");

        Assert.IsTrue(fixture.Coordinator.RespondToPermission(pending.CallbackId, response));
        Assert.AreEqual(response, await pendingResponse);
        Assert.IsEmpty(fixture.Coordinator.Pending);
        var history = fixture.Coordinator.History.Single();
        Assert.IsTrue(history.WasManual);
        Assert.AreEqual("Denied", history.Outcome);
    }

    [TestMethod]
    public async Task AllowListPermission_CreatesNarrowNonDelegableGrant()
    {
        using var fixture = new CoordinatorFixture(new CallbackPolicy(
            CallbackPolicyMode.AllowListedTools,
            new HashSet<string>(StringComparer.Ordinal) { "host.echo" }));
        var callbacks = fixture.Coordinator.CreateCallbacks("runtime-a");
        var request = CreatePermissionRequest("allowed-call");

        var response = await callbacks.ToolPermissionRequestedAsync!(
            request,
            TestContext.CancellationToken);

        Assert.AreEqual(ToolAuthorizationDecision.Granted, response.Decision);
        Assert.IsNotNull(response.Grant);
        Assert.IsFalse(response.Grant.AllowDelegation);
        Assert.IsTrue(response.Grant.NetworkPolicy.DenyAll);
        CollectionAssert.AreEqual(new[] { request.ToolId }, response.Grant.AllowedToolIds);
        CollectionAssert.AreEqual(new[] { request.CallId }, response.Grant.AllowedCallIds);
        Assert.IsEmpty(response.Grant.AllowedReadRoots);
        Assert.IsEmpty(response.Grant.AllowedWriteRoots);
    }

    [TestMethod]
    public async Task AutomaticPolicy_DeniesDangerousApprovalByDefault()
    {
        using var fixture = new CoordinatorFixture(new CallbackPolicy(
            CallbackPolicyMode.AllowListedTools,
            new HashSet<string>(StringComparer.Ordinal) { "host.echo" },
            AllowLowRiskApprovals: true));
        var callbacks = fixture.Coordinator.CreateCallbacks("runtime-a");
        var request = new ApprovalRequest(
            "correlation",
            "approval",
            "call",
            "run",
            "agent",
            null,
            "host.echo",
            "hash",
            ToolRiskLevel.Destructive,
            "target",
            "reason");

        var response = await callbacks.ApprovalRequestedAsync!(
            request,
            TestContext.CancellationToken);

        Assert.AreEqual(ToolAuthorizationDecision.Denied, response.Decision);
        Assert.IsNull(response.Grant);
    }

    [TestMethod]
    public async Task ToolFailure_IsIsolatedAndLaterCallbackSucceeds()
    {
        var executor = new FailOnceExecutor();
        using var fixture = new CoordinatorFixture(
            new CallbackPolicy(
                CallbackPolicyMode.AllowListedTools,
                new HashSet<string>(StringComparer.Ordinal) { "host.echo" }),
            executor);
        var callbacks = fixture.Coordinator.CreateCallbacks("runtime-a");

        var failed = await callbacks.ToolCallRequestedAsync!(
            CreateToolCallRequest("failed"),
            TestContext.CancellationToken);
        var succeeded = await callbacks.ToolCallRequestedAsync!(
            CreateToolCallRequest("succeeded"),
            TestContext.CancellationToken);

        Assert.AreEqual(ToolCallStatus.Failed, failed.Status);
        Assert.AreEqual(ToolCallStatus.Succeeded, succeeded.Status);
        Assert.HasCount(2, fixture.Coordinator.History);
    }

    [TestMethod]
    public void CreateCallbacks_TransfersConcurrencyAndTimeoutConfiguration()
    {
        using var fixture = new CoordinatorFixture(CallbackPolicy.DenyAll);

        var callbacks = fixture.Coordinator.CreateCallbacks(
            "runtime-a",
            maxConcurrency: 7,
            callbackTimeout: TimeSpan.FromSeconds(12));

        Assert.AreEqual(7, callbacks.MaxConcurrency);
        Assert.AreEqual(TimeSpan.FromSeconds(12), callbacks.CallbackTimeout);
    }

    private static async Task<CallbackRequestEnvelope> WaitForPendingAsync(
        CallbackCoordinator coordinator)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var items = coordinator.Pending;
            if (items.Count > 0)
            {
                return items[0];
            }

            await Task.Delay(10);
        }

        throw new AssertFailedException("The callback was not queued.");
    }

    private static ToolPermissionRequest CreatePermissionRequest(string callId) => new(
        "approval-" + callId,
        "agent",
        null,
        "host.echo",
        callId,
        "hash",
        "Low",
        "once",
        "test",
        "correlation-" + callId,
        "run");

    private static ToolCallRequest CreateToolCallRequest(string callId)
    {
        using var document = JsonDocument.Parse("{}");
        return new ToolCallRequest(
            "correlation-" + callId,
            callId,
            "run",
            "session",
            "invocation",
            "agent",
            "host.echo",
            "v1",
            document.RootElement.Clone(),
            5_000,
            "grant");
    }

    private sealed class CoordinatorFixture : IDisposable
    {
        private readonly OwnedTestDirectory _directory = new();
        private readonly ToolCallResultStore _store;

        public CoordinatorFixture(CallbackPolicy policy, IToolCallExecutor? executor = null)
        {
            _store = new ToolCallResultStore(_directory.Path);
            Coordinator = new CallbackCoordinator(
                policy,
                new ConservativeToolGrantFactory(_directory.Path),
                new ToolCallExecutionCoordinator(_store),
                executor ?? new FailOnceExecutor(startAt: 1));
        }

        public CallbackCoordinator Coordinator { get; }

        public void Dispose()
        {
            _store.Dispose();
            _directory.Dispose();
        }
    }

    private sealed class FailOnceExecutor(int startAt = 0) : IToolCallExecutor
    {
        private int _count = startAt;

        public ValueTask<ToolCallResponse> ExecuteAsync(
            ToolCallRequest request,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _count) == 1)
            {
                throw new InvalidOperationException("expected failure");
            }

            return ValueTask.FromResult(new ToolCallResponse(
                request.CorrelationId,
                request.CallId,
                ToolCallStatus.Succeeded));
        }
    }
}
