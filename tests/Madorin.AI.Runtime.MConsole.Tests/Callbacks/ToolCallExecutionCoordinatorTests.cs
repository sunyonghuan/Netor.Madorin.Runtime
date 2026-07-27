using System.Text.Json;

using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.MConsole.Callbacks;
using Madorin.AI.Runtime.MConsole.Tests.TestSupport;

namespace Madorin.AI.Runtime.MConsole.Tests.Callbacks;

[TestClass]
public sealed class ToolCallExecutionCoordinatorTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task ExecuteAsync_SameCallConcurrently_ExecutesSideEffectOnce()
    {
        using var directory = new OwnedTestDirectory();
        using var store = new ToolCallResultStore(directory.Path);
        var coordinator = new ToolCallExecutionCoordinator(store);
        var executor = new CountingExecutor();
        var request = CreateRequest("same-call");

        var first = coordinator.ExecuteAsync("runtime-a", request, executor, TestContext.CancellationToken);
        var second = coordinator.ExecuteAsync("runtime-a", request, executor, TestContext.CancellationToken);
        var results = await Task.WhenAll(first, second);

        Assert.AreEqual(1, executor.Count);
        Assert.AreEqual(ToolCallStatus.Succeeded, results[0].Status);
        Assert.AreEqual(results[0].CallId, results[1].CallId);
        Assert.IsTrue(results[0].Result!.Value.GetProperty("ok").GetBoolean());
        Assert.IsTrue(results[1].Result!.Value.GetProperty("ok").GetBoolean());
    }

    [TestMethod]
    public async Task ExecuteAsync_Failure_IsPersistedAndNotRetried()
    {
        using var directory = new OwnedTestDirectory();
        using var store = new ToolCallResultStore(directory.Path);
        var coordinator = new ToolCallExecutionCoordinator(store);
        var executor = new ThrowingExecutor();
        var request = CreateRequest("failed-call");

        var first = await coordinator.ExecuteAsync(
            "runtime-a",
            request,
            executor,
            TestContext.CancellationToken);
        var second = await coordinator.ExecuteAsync(
            "runtime-a",
            request,
            executor,
            TestContext.CancellationToken);

        Assert.AreEqual(1, executor.Count);
        Assert.AreEqual(ToolCallStatus.Failed, first.Status);
        Assert.AreEqual(first, second);
    }

    [TestMethod]
    public async Task QueryAsync_ReadsDurableResultWithoutExecutingTool()
    {
        using var directory = new OwnedTestDirectory();
        using var store = new ToolCallResultStore(directory.Path);
        var coordinator = new ToolCallExecutionCoordinator(store);
        var executor = new CountingExecutor();
        var request = CreateRequest("query-call");
        await coordinator.ExecuteAsync(
            "runtime-a",
            request,
            executor,
            TestContext.CancellationToken);

        var response = await coordinator.QueryAsync(
            "runtime-a",
            new ToolResultQueryRequest("query", request.CallId),
            TestContext.CancellationToken);

        Assert.AreEqual(1, executor.Count);
        Assert.AreEqual(ToolCallStatus.Succeeded, response.Status);
    }

    [TestMethod]
    public async Task CancelAsync_CancelsOnlyTargetCall()
    {
        using var directory = new OwnedTestDirectory();
        using var store = new ToolCallResultStore(directory.Path);
        var coordinator = new ToolCallExecutionCoordinator(store);
        var executor = new BlockingExecutor();
        var first = coordinator.ExecuteAsync(
            "runtime-a",
            CreateRequest("call-a"),
            executor,
            TestContext.CancellationToken);
        var second = coordinator.ExecuteAsync(
            "runtime-a",
            CreateRequest("call-b"),
            executor,
            TestContext.CancellationToken);
        await executor.BothStarted.Task.WaitAsync(TestContext.CancellationToken);

        var outcome = await coordinator.CancelAsync(
            "runtime-a",
            "call-a",
            TestContext.CancellationToken);
        executor.Release.TrySetResult();
        var results = await Task.WhenAll(first, second);

        Assert.AreEqual(ToolCallCancellationOutcome.CancellationRequested, outcome);
        Assert.AreEqual(ToolCallStatus.Cancelled, results.Single(item => item.CallId == "call-a").Status);
        Assert.AreEqual(ToolCallStatus.Succeeded, results.Single(item => item.CallId == "call-b").Status);
    }

    private static ToolCallRequest CreateRequest(string callId)
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

    private sealed class CountingExecutor : IToolCallExecutor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public ValueTask<ToolCallResponse> ExecuteAsync(
            ToolCallRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            using var document = JsonDocument.Parse("{\"ok\":true}");
            return ValueTask.FromResult(new ToolCallResponse(
                request.CorrelationId,
                request.CallId,
                ToolCallStatus.Succeeded,
                document.RootElement.Clone()));
        }
    }

    private sealed class ThrowingExecutor : IToolCallExecutor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public ValueTask<ToolCallResponse> ExecuteAsync(
            ToolCallRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            throw new InvalidOperationException("expected failure");
        }
    }

    private sealed class BlockingExecutor : IToolCallExecutor
    {
        private int _started;

        public TaskCompletionSource BothStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ToolCallResponse> ExecuteAsync(
            ToolCallRequest request,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _started) == 2)
            {
                BothStarted.TrySetResult();
            }

            await Release.Task.WaitAsync(cancellationToken);
            return new ToolCallResponse(
                request.CorrelationId,
                request.CallId,
                ToolCallStatus.Succeeded);
        }
    }
}
