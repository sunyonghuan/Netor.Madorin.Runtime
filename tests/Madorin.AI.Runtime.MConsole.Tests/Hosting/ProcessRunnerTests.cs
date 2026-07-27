using Madorin.AI.Runtime.MConsole.Hosting;

namespace Madorin.AI.Runtime.MConsole.Tests.Hosting;

[TestClass]
public sealed class ProcessRunnerTests
{
    [TestMethod]
    public async Task RunAsync_DotnetVersion_CapturesSeparatedStreamsAndExitCode()
    {
        var runner = new ProcessRunner();

        var result = await runner.RunAsync(
            "dotnet",
            ["--version"],
            timeout: TimeSpan.FromSeconds(30));

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.StandardOutput));
        Assert.IsFalse(result.TimedOut);
        Assert.IsFalse(result.Canceled);
    }

    [TestMethod]
    public async Task RunAsync_SensitiveArgument_RedactsRecordedArguments()
    {
        var runner = new ProcessRunner();

        var result = await runner.RunAsync(
            "dotnet",
            ["--version", "--api-key", "not-a-real-secret"],
            timeout: TimeSpan.FromSeconds(30));

        CollectionAssert.DoesNotContain(result.Arguments.ToArray(), "not-a-real-secret");
        CollectionAssert.Contains(result.Arguments.ToArray(), "<redacted>");
    }
}
