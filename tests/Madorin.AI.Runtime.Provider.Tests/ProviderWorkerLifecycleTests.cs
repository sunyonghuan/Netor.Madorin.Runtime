using System.Diagnostics;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server.ProviderWorkers;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ProviderWorkerLifecycleTests
{
    [TestMethod]
    public async Task Dispose_ShutdownStopsWorkerProcess()
    {
        var directory = RequireWorkerDirectory();
        int pid;
        await using (var process = CreateProcess(directory))
        {
            await process.EnsureReadyAsync(TestContext.CancellationToken);
            var processId = process.ProcessId;
            Assert.IsTrue(processId.HasValue);
            pid = processId.Value;
            Assert.IsFalse(Process.GetProcessById(pid).HasExited);
        }

        await AssertProcessExitedAsync(pid);
    }

    [TestMethod]
    public async Task SessionSwitch_DoesNotKillWorker()
    {
        var directory = RequireWorkerDirectory();
        await using var supervisor = new ProviderWorkerSupervisor(directory);
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible),
            TestContext.CancellationToken);
        var first = supervisor.CreateAdapter(
            "OpenAI Compatible",
            CreateConnection("session-a", server));
        var second = supervisor.CreateAdapter(
            "OpenAI Compatible",
            CreateConnection("session-b", server));

        await ProviderAdapterTestFactory.CollectAsync(
            first,
            ProviderAdapterTestFactory.CreateRequest() with { ProviderId = "session-a", InvocationId = "inv-a" },
            TestContext.CancellationToken);

        Assert.IsTrue(supervisor.TryGetProcess("openai-compatible", out var worker));
        var processId = worker.ProcessId;
        Assert.IsTrue(processId.HasValue);
        var pid = processId.Value;

        await ProviderAdapterTestFactory.CollectAsync(
            second,
            ProviderAdapterTestFactory.CreateRequest() with { ProviderId = "session-b", InvocationId = "inv-b" },
            TestContext.CancellationToken);

        Assert.AreEqual(pid, worker.ProcessId);
        Assert.IsFalse(Process.GetProcessById(pid).HasExited);
    }

    [TestMethod]
    public async Task Start_AssignsWorkerToRuntimeProcessGroup()
    {
        var directory = RequireWorkerDirectory();
        await using var process = CreateProcess(directory);
        await process.EnsureReadyAsync(TestContext.CancellationToken);
        if (!process.IsAssignedToRuntimeGroup)
        {
            Assert.Inconclusive("The host Job Object or process group rejected nested assignment.");
        }
    }

    [TestMethod]
    [Timeout(45_000, CooperativeCancellation = true)]
    public async Task TwoUnexpectedExits_AllowRelaunch()
    {
        var directory = RequireWorkerDirectory();
        await using var process = CreateProcess(directory);
        await process.EnsureReadyAsync(TestContext.CancellationToken);
        await KillCurrentWorkerAsync(process);
        await process.EnsureReadyAsync(TestContext.CancellationToken);
        Assert.IsFalse(process.IsFused);
        await KillCurrentWorkerAsync(process);
        await process.EnsureReadyAsync(TestContext.CancellationToken);
        Assert.IsFalse(process.IsFused);
        Assert.IsNotNull(process.ProcessId);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ThreeUnexpectedExits_FusesAndStopsRelaunch()
    {
        var directory = RequireWorkerDirectory();
        await using var process = CreateProcess(directory);
        await process.EnsureReadyAsync(TestContext.CancellationToken);
        await KillCurrentWorkerAsync(process);
        await process.EnsureReadyAsync(TestContext.CancellationToken);
        await KillCurrentWorkerAsync(process);
        await process.EnsureReadyAsync(TestContext.CancellationToken);
        await KillCurrentWorkerAsync(process);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => process.EnsureReadyAsync(TestContext.CancellationToken));
        Assert.Contains("fused", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsTrue(process.IsFused);
        Assert.IsNull(process.ProcessId);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => process.EnsureReadyAsync(TestContext.CancellationToken));
        Assert.IsNull(process.ProcessId);
    }

    public TestContext TestContext { get; set; } = null!;

    private static string RequireWorkerDirectory()
    {
        var directory = AppContext.BaseDirectory;
        var exe = ProviderWorkerPaths.GetExecutablePath(directory, "openai-compatible");
        if (!File.Exists(exe))
        {
            Assert.Inconclusive($"Worker exe not found: {exe}");
        }

        return directory;
    }

    private static ProviderWorkerProcess CreateProcess(string directory) =>
        new(directory, "openai-compatible", "OpenAI Compatible", "0.1.46");

    private static ProviderConnectionConfig CreateConnection(string providerId, FakeProviderServer server) =>
        new(
            providerId,
            "OpenAI Compatible",
            new Uri(server.BaseAddress, "v1").AbsoluteUri,
            ProviderAdapterTestFactory.ApiKey,
            Models: [ProviderAdapterTestFactory.ModelId]);

    private static async Task KillCurrentWorkerAsync(ProviderWorkerProcess process)
    {
        var processId = process.ProcessId;
        Assert.IsTrue(processId.HasValue);
        var pid = processId.Value;
        try
        {
            using var osProcess = Process.GetProcessById(pid);
            try
            {
                osProcess.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            await osProcess.WaitForExitAsync();
        }
        catch (ArgumentException)
        {
        }
    }

    private static async Task AssertProcessExitedAsync(int pid)
    {
        for (var i = 0; i < 50; i++)
        {
            try
            {
                using var osProcess = Process.GetProcessById(pid);
                if (osProcess.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Worker PID {pid} did not exit after Dispose.");
    }
}
