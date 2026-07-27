using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.MConsole.HostClient;

namespace Madorin.AI.Runtime.MConsole.Tests.HostClient;

[TestClass]
public sealed class HostConnectionOptionsTests
{
    [TestMethod]
    public void Validate_AttachWithoutSecret_ThrowsArgumentException()
    {
        var options = new HostConnectionOptions(
            "host",
            Environment.CurrentDirectory,
            "madorin",
            RuntimeStartPolicy.AttachExisting,
            RuntimeInstanceId: "runtime-a");

        Assert.ThrowsExactly<ArgumentException>(options.Validate);
    }

    [TestMethod]
    public void Validate_AlwaysStartWithoutAttachSecret_DoesNotThrow()
    {
        var options = new HostConnectionOptions(
            "host",
            Environment.CurrentDirectory,
            "madorin",
            RuntimeStartPolicy.AlwaysStart);

        options.Validate();
    }
}
