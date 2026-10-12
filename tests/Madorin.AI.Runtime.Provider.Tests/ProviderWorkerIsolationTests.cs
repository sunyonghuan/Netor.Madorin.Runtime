using System.Diagnostics;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Server.ProviderWorkers;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ProviderWorkerIsolationTests
{
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task TwoConnections_ShareWorkerAndDoNotMixKeys()
    {
        var directory = RequireWorkerDirectory();
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible),
            TestContext.CancellationToken);
        await using var supervisor = new ProviderWorkerSupervisor(directory);
        var first = supervisor.CreateAdapter(
            "OpenAI Compatible",
            CreateConnection("conn-a", server, "key-a"));
        var second = supervisor.CreateAdapter(
            "OpenAI Compatible",
            CreateConnection("conn-b", server, "key-b"));

        await ProviderAdapterTestFactory.CollectAsync(
            first,
            ProviderAdapterTestFactory.CreateRequest() with { ProviderId = "conn-a", InvocationId = "inv-a" },
            TestContext.CancellationToken);
        Assert.IsTrue(supervisor.TryGetProcess("openai-compatible", out var worker));
        var processId = worker.ProcessId;
        Assert.IsTrue(processId.HasValue);

        await ProviderAdapterTestFactory.CollectAsync(
            second,
            ProviderAdapterTestFactory.CreateRequest() with { ProviderId = "conn-b", InvocationId = "inv-b" },
            TestContext.CancellationToken);

        Assert.AreEqual(processId, worker.ProcessId);
        Assert.HasCount(2, server.Requests);
        Assert.AreEqual("Bearer key-a", server.Requests[0].Headers["Authorization"]);
        Assert.AreEqual("Bearer key-b", server.Requests[1].Headers["Authorization"]);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ConcurrentInvocations_CompleteOnSameWorker()
    {
        var directory = RequireWorkerDirectory();
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.SlowText(
                ProviderAdapterKind.OpenAICompatible,
                TimeSpan.FromMilliseconds(150)),
            TestContext.CancellationToken);
        await using var supervisor = new ProviderWorkerSupervisor(directory);
        var adapter = supervisor.CreateAdapter(
            "OpenAI Compatible",
            CreateConnection("conn-concurrent", server, ProviderAdapterTestFactory.ApiKey));
        var firstTask = ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest() with { InvocationId = "inv-first" },
            TestContext.CancellationToken);
        var secondTask = ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest() with { InvocationId = "inv-second" },
            TestContext.CancellationToken);
        await Task.WhenAll(firstTask, secondTask);

        Assert.ContainsSingle(firstTask.Result.OfType<InvocationCompletedProviderEvent>());
        Assert.ContainsSingle(secondTask.Result.OfType<InvocationCompletedProviderEvent>());
        Assert.IsTrue(supervisor.TryGetProcess("openai-compatible", out var worker));
        Assert.IsTrue(worker.ProcessId.HasValue);
        Assert.HasCount(2, server.Requests);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task KillDuringInvoke_FailsPendingInvocation()
    {
        var directory = RequireWorkerDirectory();
        var invocationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await FakeProviderServer.StartAsync(
            _ =>
            {
                invocationStarted.TrySetResult();
                return ProviderProtocolScripts.SlowText(
                    ProviderAdapterKind.OpenAICompatible,
                    TimeSpan.FromSeconds(2));
            },
            TestContext.CancellationToken);
        await using var supervisor = new ProviderWorkerSupervisor(directory);
        var adapter = supervisor.CreateAdapter(
            "OpenAI Compatible",
            CreateConnection("conn-kill", server, ProviderAdapterTestFactory.ApiKey));
        var collect = ProviderAdapterTestFactory.CollectAsync(
            adapter,
            ProviderAdapterTestFactory.CreateRequest() with { InvocationId = "inv-kill" },
            TestContext.CancellationToken);
        await invocationStarted.Task.WaitAsync(TestContext.CancellationToken);
        Assert.IsTrue(supervisor.TryGetProcess("openai-compatible", out var worker));
        Assert.IsTrue(worker.ProcessId.HasValue);
        await KillPidAsync(worker.ProcessId.Value);
        var events = await collect;
        var failed = Assert.ContainsSingle(events.OfType<InvocationFailedProviderEvent>());
        Assert.AreEqual("ProviderWorkerExited", failed.Error.Code);
    }

    [TestMethod]
    [Timeout(45_000, CooperativeCancellation = true)]
    public async Task KillThenRelaunch_KeepsWorkspaceLock()
    {
        var directory = RequireWorkerDirectory();
        var workspace = Path.Combine(
            Path.GetTempPath(),
            "madorin-worker-lock",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            await using var workspaceLock = await WorkspaceWriteLock.AcquireAsync(
                workspace,
                "runtime-lock-test",
                TimeSpan.Zero,
                TestContext.CancellationToken);
            await using var process = new ProviderWorkerProcess(
                directory,
                "openai-compatible",
                "OpenAI Compatible",
                "0.1.46");
            await process.EnsureReadyAsync(TestContext.CancellationToken);
            var firstId = process.ProcessId;
            Assert.IsTrue(firstId.HasValue);
            await KillPidAsync(firstId.Value);
            await process.EnsureReadyAsync(TestContext.CancellationToken);
            Assert.IsTrue(process.ProcessId.HasValue);
            Assert.AreNotEqual(firstId, process.ProcessId);
            Assert.IsFalse(process.IsFused);

            var conflict = await Assert.ThrowsExactlyAsync<WorkspaceBusyException>(
                () => WorkspaceWriteLock.AcquireAsync(
                    workspace,
                    "runtime-lock-other",
                    TimeSpan.Zero,
                    TestContext.CancellationToken));
            Assert.Contains("active Runtime instance", conflict.Message, StringComparison.Ordinal);
            Assert.AreEqual(workspaceLock.LockPath, conflict.LockPath);
            Assert.IsTrue(File.Exists(workspaceLock.LockPath));
        }
        finally
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task TwoSupervisors_StartTwoWorkerProcesses()
    {
        var directory = RequireWorkerDirectory();
        await using var first = new ProviderWorkerSupervisor(directory);
        await using var second = new ProviderWorkerSupervisor(directory);
        await using var server = await FakeProviderServer.StartAsync(
            _ => ProviderProtocolScripts.NormalText(ProviderAdapterKind.OpenAICompatible),
            TestContext.CancellationToken);

        await ProviderAdapterTestFactory.CollectAsync(
            first.CreateAdapter("OpenAI Compatible", CreateConnection("a", server, "key-a")),
            ProviderAdapterTestFactory.CreateRequest() with { ProviderId = "a", InvocationId = "inv-a" },
            TestContext.CancellationToken);
        await ProviderAdapterTestFactory.CollectAsync(
            second.CreateAdapter("OpenAI Compatible", CreateConnection("b", server, "key-b")),
            ProviderAdapterTestFactory.CreateRequest() with { ProviderId = "b", InvocationId = "inv-b" },
            TestContext.CancellationToken);

        Assert.IsTrue(first.TryGetProcess("openai-compatible", out var firstWorker));
        Assert.IsTrue(second.TryGetProcess("openai-compatible", out var secondWorker));
        Assert.IsTrue(firstWorker.ProcessId.HasValue);
        Assert.IsTrue(secondWorker.ProcessId.HasValue);
        Assert.AreNotEqual(firstWorker.ProcessId, secondWorker.ProcessId);
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

    private static ProviderConnectionConfig CreateConnection(
        string providerId,
        FakeProviderServer server,
        string apiKey) =>
        new(
            providerId,
            "OpenAI Compatible",
            new Uri(server.BaseAddress, "v1").AbsoluteUri,
            apiKey,
            Models: [ProviderAdapterTestFactory.ModelId]);

    private static async Task KillPidAsync(int pid)
    {
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
}
