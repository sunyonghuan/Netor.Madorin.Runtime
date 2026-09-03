using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Server;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class HostProviderInjectionTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task NewSession_WithProviderConfigs_UsesHostRegistryAdapter()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var pipePrefix = $"madorin.ai.runtime.host.{Guid.NewGuid():N}";
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);

        try
        {
            // ProviderResolver null → Server creates HostProviderRegistry (host mode).
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = pipePrefix,
                    HandshakeSecret = secret,
                    MemoryUserHome = Path.Combine(workspace, "home")
                },
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(serverCancellation.Token);
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: instanceId,
                    HandshakeSecret: secret));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await client.ConnectAsync(timeout.Token);

            var providerConfig = new HostProviderConfig(
                "host-openai",
                "OpenAI",
                "https://api.openai.com/v1",
                "sk-host-test",
                Models: ["gpt-5"]);

            var runId = await client.StartNewSessionRunAsync(
                new NewSessionRunRequest(
                    "host-session-1",
                    "host-run-1",
                    RuntimeMode.Expert,
                    new NextTurnSelection(
                        1,
                        RuntimeMode.Expert,
                        new DefaultSelection("host-openai", "gpt-5"),
                        new ExpertModeOptions(
                            new AgentRef("expert", "1", "You are helpful."))),
                    [new TextContentBlock("hello")],
                    ProviderConfigs: [providerConfig]),
                timeout.Token);

            Assert.IsFalse(string.IsNullOrWhiteSpace(runId));

            // Without ProviderConfigs the second provider id must fail validation.
            var missing = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await client.StartNewSessionRunAsync(
                    new NewSessionRunRequest(
                        "host-session-2",
                        "host-run-2",
                        RuntimeMode.Expert,
                        new NextTurnSelection(
                            1,
                            RuntimeMode.Expert,
                            new DefaultSelection("never-registered", "gpt-5"),
                            new ExpertModeOptions(
                                new AgentRef("expert", "1", "You are helpful."))),
                        [new TextContentBlock("hello")]),
                    timeout.Token));
            StringAssert.Contains(missing.Message, "not configured");

            // Primary equality mismatch is rejected.
            var mismatch = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await client.StartNewSessionRunAsync(
                    new NewSessionRunRequest(
                        "host-session-3",
                        "host-run-3",
                        RuntimeMode.Expert,
                        new NextTurnSelection(
                            1,
                            RuntimeMode.Expert,
                            new DefaultSelection("host-openai", "gpt-5"),
                            new ExpertModeOptions(
                                new AgentRef(
                                    "expert",
                                    "1",
                                    "You are helpful.",
                                    ProviderId: "other-provider",
                                    ModelId: "gpt-5"))),
                        [new TextContentBlock("hello")],
                        ProviderConfigs: [providerConfig]),
                    timeout.Token));
            StringAssert.Contains(mismatch.Message, "must equal");

            // providers.upsert pre-registers without a Run.
            await client.UpsertProvidersAsync(
            [
                new HostProviderConfig(
                    "prewarm-provider",
                    "OpenAI Compatible",
                    "https://compat.example/v1",
                    "sk-prewarm")
            ],
            timeout.Token);

            var prewarmRun = await client.StartNewSessionRunAsync(
                new NewSessionRunRequest(
                    "host-session-4",
                    "host-run-4",
                    RuntimeMode.Expert,
                    new NextTurnSelection(
                        1,
                        RuntimeMode.Expert,
                        new DefaultSelection("prewarm-provider", "model-x"),
                        new ExpertModeOptions(
                            new AgentRef("expert", "1", "You are helpful."))),
                    [new TextContentBlock("hello")]),
                timeout.Token);
            Assert.IsFalse(string.IsNullOrWhiteSpace(prewarmRun));

            serverCancellation.Cancel();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        finally
        {
            TryDeleteDirectory(workspace);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task WorkMode_MultiVendorChildAgents_ResolveDistinctProviders()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var pipePrefix = $"madorin.ai.runtime.mv.{Guid.NewGuid():N}";
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);

        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = pipePrefix,
                    HandshakeSecret = secret,
                    MemoryUserHome = Path.Combine(workspace, "home")
                },
                TestContext.CancellationToken);
            _ = server.RunAsync(serverCancellation.Token);
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: instanceId,
                    HandshakeSecret: secret));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await client.ConnectAsync(timeout.Token);

            var configs = new HostProviderConfig[]
            {
                new("gm-provider", "OpenAI", "https://api.openai.com/v1", "sk-gm"),
                new("child-provider", "Anthropic", "https://api.anthropic.com", "sk-child")
            };

            var runId = await client.StartNewSessionRunAsync(
                new NewSessionRunRequest(
                    "work-mv-session",
                    "work-mv-run",
                    RuntimeMode.Work,
                    new NextTurnSelection(
                        1,
                        RuntimeMode.Work,
                        new DefaultSelection("gm-provider", "gpt-5"),
                        new WorkModeOptions(
                            new AgentRef("gm", "1", "GM prompt"),
                            [
                                new AgentRef(
                                    "child",
                                    "1",
                                    "Child prompt",
                                    ProviderId: "child-provider",
                                    ModelId: "claude")
                            ])),
                    [new TextContentBlock("plan this")],
                    ProviderConfigs: configs),
                timeout.Token);

            Assert.IsFalse(string.IsNullOrWhiteSpace(runId));
            serverCancellation.Cancel();
        }
        finally
        {
            TryDeleteDirectory(workspace);
        }
    }

    private static string CreateTemporaryDirectory(
        [CallerMemberName] string? name = null)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-host-provider-tests",
            $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
