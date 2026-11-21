using Madorin.AI.Runtime.Server;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class RunRegistryInactivityTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task SilentLease_TimesOutAfterInactivityWindow()
    {
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        using var registry = new RunRegistry(
            1, 1, 1, TimeSpan.FromMilliseconds(300), shutdown.Token);
        var lease = registry.TryAcquire("provider-a", out var capacity);
        Assert.IsNotNull(lease);
        Assert.AreEqual(RunCapacityResult.Acquired, capacity);
        using (lease)
        {
            await WaitUntilAsync(
                () => lease.IsTimedOut, TimeSpan.FromSeconds(5), TestContext.CancellationToken);
            Assert.IsTrue(lease.CancellationToken.IsCancellationRequested);
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task PeriodicActivity_KeepsLeaseAliveBeyondTotalWindow()
    {
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        using var registry = new RunRegistry(
            1, 1, 1, TimeSpan.FromMilliseconds(500), shutdown.Token);
        var lease = registry.TryAcquire("provider-a", out _);
        Assert.IsNotNull(lease);
        using (lease)
        {
            for (var index = 0; index < 8; index++)
            {
                await Task.Delay(150, TestContext.CancellationToken);
                lease.ReportActivity();
                Assert.IsFalse(lease.IsTimedOut, "持续活动的 Run 不应被不活动看门狗取消。");
            }

            Assert.IsFalse(lease.CancellationToken.IsCancellationRequested);
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task WaitingState_SuspendsInactivityWatchdog()
    {
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        using var registry = new RunRegistry(
            1, 1, 1, TimeSpan.FromMilliseconds(500), shutdown.Token);
        var lease = registry.TryAcquire("provider-a", out _);
        Assert.IsNotNull(lease);
        using (lease)
        {
            lease.SetWaiting(true);
            await Task.Delay(1500, TestContext.CancellationToken);
            Assert.IsFalse(lease.IsTimedOut, "等待工具/审批/凭据期间不应计时。");

            lease.SetWaiting(false);
            await WaitUntilAsync(
                () => lease.IsTimedOut, TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ResumeWithActivity_RearmsFullInactivityWindow()
    {
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        using var registry = new RunRegistry(
            1, 1, 1, TimeSpan.FromMilliseconds(500), shutdown.Token);
        var lease = registry.TryAcquire("provider-a", out _);
        Assert.IsNotNull(lease);
        using (lease)
        {
            lease.SetWaiting(true);
            await Task.Delay(800, TestContext.CancellationToken);
            lease.SetWaiting(false);

            for (var index = 0; index < 8; index++)
            {
                await Task.Delay(150, TestContext.CancellationToken);
                lease.ReportActivity();
                Assert.IsFalse(lease.IsTimedOut, "恢复运行后应重新获得完整不活动窗口。");
            }

            await WaitUntilAsync(
                () => lease.IsTimedOut, TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        }
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20, cancellationToken);
        }

        Assert.IsTrue(predicate(), "预期状态在超时前未达成。");
    }
}
