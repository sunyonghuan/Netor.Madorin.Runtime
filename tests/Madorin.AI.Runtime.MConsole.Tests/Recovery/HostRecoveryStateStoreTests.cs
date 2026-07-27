using Madorin.AI.Runtime.MConsole.Recovery;
using Madorin.AI.Runtime.MConsole.Tests.TestSupport;

namespace Madorin.AI.Runtime.MConsole.Tests.Recovery;

[TestClass]
public sealed class HostRecoveryStateStoreTests
{
    private static readonly string[] ExpectedRuntimeIds = ["runtime-a", "runtime-b"];

    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task SaveGetAndList_IsolateRuntimeInstances()
    {
        using var directory = new OwnedTestDirectory();
        using var store = new HostRecoveryStateStore(directory.Path);
        await store.SaveAsync(CreateState("runtime-a", 4), TestContext.CancellationToken);
        await store.SaveAsync(CreateState("runtime-b", 8), TestContext.CancellationToken);

        var first = await store.GetAsync("runtime-a", TestContext.CancellationToken);
        var all = await store.ListAsync(TestContext.CancellationToken);

        Assert.IsNotNull(first);
        Assert.AreEqual(4, first.LastConfirmedGsn);
        Assert.HasCount(2, all);
        CollectionAssert.AreEquivalent(
            ExpectedRuntimeIds,
            all.Select(static item => item.RuntimeInstanceId).ToArray());
        var persistedText = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(directory.Path, "recovery-*.json")
                .Select(File.ReadAllText));
        Assert.DoesNotContain("secret", persistedText, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task SaveAsync_BackwardGsn_IsRejectedWithoutChangingState()
    {
        using var directory = new OwnedTestDirectory();
        using var store = new HostRecoveryStateStore(directory.Path);
        await store.SaveAsync(CreateState("runtime-a", 9), TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            store.SaveAsync(CreateState("runtime-a", 8), TestContext.CancellationToken));

        Assert.AreEqual(
            9,
            (await store.GetAsync("runtime-a", TestContext.CancellationToken))!.LastConfirmedGsn);
    }

    private static HostRecoveryState CreateState(string runtimeInstanceId, long gsn) => new(
        runtimeInstanceId,
        "host-" + runtimeInstanceId,
        Environment.CurrentDirectory,
        Environment.ProcessId,
        "1.1",
        gsn,
        "run",
        "session",
        DateTimeOffset.UtcNow);
}
