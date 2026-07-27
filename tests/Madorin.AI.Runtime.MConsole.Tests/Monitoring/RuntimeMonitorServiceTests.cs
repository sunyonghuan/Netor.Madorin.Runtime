using Madorin.AI.Runtime.MConsole.Monitoring;
using Madorin.AI.Runtime.MConsole.Tests.TestSupport;

namespace Madorin.AI.Runtime.MConsole.Tests.Monitoring;

[TestClass]
public sealed class RuntimeMonitorServiceTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task CaptureAndCancel_UseSdkSurface()
    {
        var client = new FakeRuntimeClient();
        var monitor = new RuntimeMonitorService(client);

        var snapshot = await monitor.CaptureAsync(TestContext.CancellationToken);
        var cancelled = await monitor.CancelRunAsync(
            "run",
            "operator request",
            TestContext.CancellationToken);

        Assert.AreEqual("runtime", snapshot.Binding.RuntimeInstanceId);
        Assert.AreEqual("1.1", snapshot.Status.ProtocolVersion);
        Assert.IsTrue(cancelled);
    }
}
