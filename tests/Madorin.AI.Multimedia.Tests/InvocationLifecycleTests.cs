using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Execution;

namespace Madorin.AI.Multimedia.Tests;

[TestClass]
public sealed class InvocationLifecycleTests
{
    [TestMethod]
    public async Task CliExitsWhileHostKeepsCancellationPipeOpen()
    {
        using var workspace = new TestWorkspace();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var repository = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        var dll = Path.Combine(repository, "src", "Multimedia", "Madorin.AI.OpenAI.Image", "bin", configuration, "net10.0", "skill-openai-image.dll");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = workspace.Root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        foreach (var arg in new[] { dll, "generate", "--request-id", "pipe", "--prompt", "hello", "--api-key", "placeholder", "--try-run" }) { process.StartInfo.ArgumentList.Add(arg); }
        Assert.IsTrue(process.Start());
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, process.ExitCode);
            Assert.AreEqual("validated", JsonNode.Parse(await stdout)!["status"]!.GetValue<string>());
            Assert.AreEqual(string.Empty, await stderr);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
        }
    }

    [TestMethod]
    public async Task DeadlineWinsOverLateCancellationAndTerminalCannotChange()
    {
        var clock = new ManualClock();
        await using var invocation = new SkillInvocation(clock) { ResponseId = "race" };
        invocation.SetTimeout(1);
        clock.Advance(TimeSpan.FromSeconds(2));
        using var input = CancelFrame("race");
        invocation.ListenForCancellation(input);
        Assert.AreEqual("timeout", invocation.CommitTerminal());
        Assert.AreEqual("timeout", invocation.CommitTerminal("cancelled"));
    }

    [TestMethod]
    public async Task AcceptedCancellationSurvivesLaterDeadlineAndBudgetCannotReviveExpiredCall()
    {
        var clock = new ManualClock();
        await using var invocation = new SkillInvocation(clock) { ResponseId = "cancel" };
        invocation.SetTimeout(1);
        using var input = CancelFrame("cancel");
        invocation.ListenForCancellation(input);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.AreEqual("cancelled", invocation.CommitTerminal());
        await using var expired = new SkillInvocation(clock);
        clock.Advance(TimeSpan.FromSeconds(301));
        Assert.ThrowsExactly<OperationCanceledException>(() => expired.SetTimeout(600));
        Assert.AreEqual("timeout", expired.CommitTerminal());
    }

    private static MemoryStream CancelFrame(string id) => new(Encoding.UTF8.GetBytes("{\"schema_version\":\"1.0\",\"type\":\"cancel\",\"request_id\":\"" + id + "\"}\n"));

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }
}
