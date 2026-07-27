using Madorin.AI.Runtime.MConsole.HostClient;
using Madorin.AI.Runtime.MConsole.Tests.TestSupport;

namespace Madorin.AI.Runtime.MConsole.Tests.HostClient;

[TestClass]
public sealed class RuntimeCursorStoreTests
{
    [TestMethod]
    public async Task AdvanceAsync_IncreasingGsn_PersistsLatestValue()
    {
        using var directory = new OwnedTestDirectory();
        using var store = new RuntimeCursorStore(directory.Path);

        await store.AdvanceAsync("runtime-a", 2);
        await store.AdvanceAsync("runtime-a", 7);
        var state = await store.GetAsync("runtime-a");

        Assert.AreEqual(7, state.LastConfirmedGsn);
        Assert.AreEqual("runtime-a", state.RuntimeInstanceId);
    }

    [TestMethod]
    public async Task AdvanceAsync_BackwardGsn_ThrowsInvalidOperationException()
    {
        using var directory = new OwnedTestDirectory();
        using var store = new RuntimeCursorStore(directory.Path);
        await store.AdvanceAsync("runtime-a", 7);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => store.AdvanceAsync("runtime-a", 6));
    }
}
