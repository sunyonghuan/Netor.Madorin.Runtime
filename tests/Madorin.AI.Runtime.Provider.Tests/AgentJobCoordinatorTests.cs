using Madorin.AI.Runtime.Services.AgentJobs;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class AgentJobCoordinatorTests
{
    private static readonly string[] ExpectedParallelResults = ["dev:A", "qa:B", "docs:C"];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task CreateMany_Wait_CollectsParallelResults()
    {
        await using var coordinator = new AgentJobCoordinator(maxConcurrent: 4);
        var started = 0;
        coordinator.BindWorker(async (jobId, agentName, task, runId, sessionId, parentAgentId, ct) =>
        {
            _ = jobId;
            _ = runId;
            _ = sessionId;
            _ = parentAgentId;
            Interlocked.Increment(ref started);
            await Task.Delay(40, ct);
            return new AgentJobWorkerResult(true, $"{agentName}:{task}", null);
        });

        var snapshots = coordinator.CreateMany(
            [
                new AgentJobCreateRequest("dev", "A"),
                new AgentJobCreateRequest("qa", "B"),
                new AgentJobCreateRequest("docs", "C")
            ],
            runId: "run-1",
            sessionId: "session-1",
            parentAgentId: "expert");

        Assert.HasCount(3, snapshots);

        var wait = await coordinator.WaitAsync(
            snapshots.Select(static s => s.JobId).ToArray(),
            timeoutMs: 5_000,
            TestContext.CancellationToken);

        Assert.IsTrue(wait.AllTerminal);
        Assert.IsTrue(wait.AllSucceeded);
        Assert.IsFalse(wait.TimedOut);
        Assert.AreEqual(3, started);

        var collected = coordinator.CollectMany(snapshots.Select(static s => s.JobId).ToArray());
        Assert.IsTrue(collected.All(static c => c.Ready && c.Success));
        CollectionAssert.AreEquivalent(
            ExpectedParallelResults,
            collected.Select(static c => c.Result).ToArray());
    }

    [TestMethod]
    public async Task Collect_BeforeTerminal_ReturnsNotReady()
    {
        await using var coordinator = new AgentJobCoordinator(maxConcurrent: 1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.BindWorker(async (jobId, agentName, task, runId, sessionId, parentAgentId, ct) =>
        {
            _ = jobId;
            _ = agentName;
            _ = task;
            _ = runId;
            _ = sessionId;
            _ = parentAgentId;
            await gate.Task.WaitAsync(ct);
            return new AgentJobWorkerResult(true, "done", null);
        });

        var job = coordinator.Create(
            new AgentJobCreateRequest("dev", "slow"),
            "run-1",
            "session-1",
            "expert");

        var early = coordinator.Collect(job.JobId);
        Assert.IsFalse(early.Ready);
        Assert.IsFalse(early.Success);

        gate.SetResult();
        var wait = await coordinator.WaitAsync([job.JobId], 5_000, TestContext.CancellationToken);
        Assert.IsTrue(wait.AllTerminal);
        Assert.IsTrue(wait.AllSucceeded);
        var done = coordinator.Collect(job.JobId);
        Assert.IsTrue(done.Ready);
        Assert.IsTrue(done.Success);
        Assert.AreEqual("done", done.Result);
    }

    [TestMethod]
    public async Task Cancel_GoesCancellingThenCancelled()
    {
        await using var coordinator = new AgentJobCoordinator(maxConcurrent: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.BindWorker(async (jobId, agentName, task, runId, sessionId, parentAgentId, ct) =>
        {
            _ = jobId;
            _ = agentName;
            _ = task;
            _ = runId;
            _ = sessionId;
            _ = parentAgentId;
            started.TrySetResult();
            await Task.Delay(30_000, ct);
            return new AgentJobWorkerResult(true, "should-not-finish", null);
        });

        var job = coordinator.Create(
            new AgentJobCreateRequest("dev", "long"),
            "run-1",
            "session-1",
            "expert");

        await started.Task.WaitAsync(TestContext.CancellationToken);
        Assert.IsTrue(coordinator.TryCancel(job.JobId));

        // cancel 后瞬时可能已是 cancelling 或已落到 cancelled（Worker 很快响应 CTS）。
        var mid = coordinator.GetSnapshot(job.JobId);
        Assert.IsNotNull(mid);
        Assert.IsTrue(
            mid.State is AgentJobStates.Cancelling or AgentJobStates.Cancelled,
            $"unexpected state={mid.State}");
        var midCollect = coordinator.Collect(job.JobId);
        if (string.Equals(mid.State, AgentJobStates.Cancelling, StringComparison.Ordinal))
        {
            Assert.IsFalse(midCollect.Ready);
        }
        else
        {
            Assert.IsTrue(midCollect.Ready);
            Assert.IsFalse(midCollect.Success);
        }

        var wait = await coordinator.WaitAsync([job.JobId], 5_000, TestContext.CancellationToken);
        Assert.IsTrue(wait.AllTerminal);
        Assert.IsFalse(wait.AllSucceeded);
        var collected = coordinator.Collect(job.JobId);
        Assert.IsTrue(collected.Ready);
        Assert.IsFalse(collected.Success);
        Assert.AreEqual(AgentJobStates.Cancelled, collected.State);
    }

    [TestMethod]
    public async Task Wait_UnknownJobId_FailsFast()
    {
        await using var coordinator = new AgentJobCoordinator();
        coordinator.BindWorker(static (_, _, _, _, _, _, _) =>
            Task.FromResult(new AgentJobWorkerResult(true, "x", null)));

        var job = coordinator.Create(
            new AgentJobCreateRequest("dev", "ok"),
            "run-1",
            "session-1",
            "expert");

        var ex = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => coordinator.WaitAsync(
                [job.JobId, "missing-job-id"],
                timeoutMs: 5_000,
                TestContext.CancellationToken));
        StringAssert.Contains(ex.Message, "missing-job-id");
    }

    [TestMethod]
    public async Task Create_ExceedsMaxJobsPerRun_Throws()
    {
        await using var coordinator = new AgentJobCoordinator(maxConcurrent: 2, maxJobsPerRun: 2);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.BindWorker(async (_, _, _, _, _, _, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return new AgentJobWorkerResult(true, "ok", null);
        });

        _ = coordinator.Create(new AgentJobCreateRequest("a", "1"), "r", "s", "p");
        _ = coordinator.Create(new AgentJobCreateRequest("b", "2"), "r", "s", "p");

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => coordinator.Create(new AgentJobCreateRequest("c", "3"), "r", "s", "p"));
        StringAssert.Contains(ex.Message, "limit exceeded");
        gate.SetResult();
    }

    [TestMethod]
    public async Task CreateMany_ValidatesBeforeEnqueue_OnInvalidItem()
    {
        await using var coordinator = new AgentJobCoordinator();
        var started = 0;
        coordinator.BindWorker((_, _, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref started);
            return Task.FromResult(new AgentJobWorkerResult(true, "ok", null));
        });

        Assert.ThrowsExactly<ArgumentException>(
            () => coordinator.CreateMany(
                [
                    new AgentJobCreateRequest("dev", "ok"),
                    new AgentJobCreateRequest(" ", "bad")
                ],
                "r",
                "s",
                "p"));
        Assert.AreEqual(0, started);
    }

    [TestMethod]
    public async Task Wait_FailedJob_AllSucceededFalse()
    {
        await using var coordinator = new AgentJobCoordinator();
        coordinator.BindWorker(static (_, _, _, _, _, _, _) =>
            Task.FromResult(new AgentJobWorkerResult(false, null, "boom")));

        var job = coordinator.Create(new AgentJobCreateRequest("dev", "x"), "r", "s", "p");
        var wait = await coordinator.WaitAsync([job.JobId], 5_000, TestContext.CancellationToken);
        Assert.IsTrue(wait.AllTerminal);
        Assert.IsFalse(wait.AllSucceeded);
    }

    [TestMethod]
    public async Task Dispose_DrainsRunningJobs()
    {
        var coordinator = new AgentJobCoordinator(maxConcurrent: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.BindWorker(async (_, _, _, _, _, _, ct) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(30_000, ct);
            }
            catch (OperationCanceledException)
            {
                finished.TrySetResult();
                throw;
            }

            return new AgentJobWorkerResult(true, "ok", null);
        });

        var job = coordinator.Create(new AgentJobCreateRequest("dev", "long"), "r", "s", "p");
        await started.Task.WaitAsync(TestContext.CancellationToken);
        await coordinator.DisposeAsync();

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        // Dispose 后不再允许新作业。
        Assert.ThrowsExactly<ObjectDisposedException>(
            () => coordinator.Create(new AgentJobCreateRequest("dev", "after"), "r", "s", "p"));
        _ = job;
    }
}
