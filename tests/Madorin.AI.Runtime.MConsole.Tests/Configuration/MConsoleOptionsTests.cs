using Madorin.AI.Runtime.MConsole.Configuration;

namespace Madorin.AI.Runtime.MConsole.Tests.Configuration;

[TestClass]
public sealed class MConsoleOptionsTests
{
    [TestMethod]
    public void Validate_DefaultOptions_DoesNotThrow()
    {
        var options = new MConsoleOptions();

        options.Validate();
    }

    [TestMethod]
    public void Validate_ZeroCallbackConcurrency_ThrowsInvalidDataException()
    {
        var options = new MConsoleOptions { MaxCallbackConcurrency = 0 };

        Assert.ThrowsExactly<InvalidDataException>(options.Validate);
    }

    [TestMethod]
    public void Create_RelativeHome_ReturnsAbsoluteOwnedDirectories()
    {
        var paths = MConsolePaths.Create(".mconsole-test-home");

        Assert.IsTrue(Path.IsPathFullyQualified(paths.HomeDirectory));
        StringAssert.StartsWith(paths.HistoryDirectory, paths.HomeDirectory);
        StringAssert.StartsWith(paths.CasesDirectory, paths.HomeDirectory);
    }
}
