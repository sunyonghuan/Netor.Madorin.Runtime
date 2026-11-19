using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

/// <summary>
/// Verifies the streaming contract introduced by the Outbox refactor: deltas reach the host
/// as GSN 0 envelopes without touching <c>event_outbox</c>, while critical events keep GSNs.
/// </summary>
[TestClass]
public sealed class StreamingDeltaDeliveryTests
{
    private const int DeltaCount = 25;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task StreamingRun_DeliversDeltasWithoutGsnAndKeepsThemOutOfOutbox()
    {
        var workspace = CreateTemporaryDirectory();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var provider = new StreamingProvider(DeltaCount);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = Guid.NewGuid().ToString("N"),
                    PipePrefix = $"madorin.streaming.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = _ => provider
                },
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    HandshakeSecret: secret,
                    ExpectedRuntimeInstanceId: server.InstanceId,
                    EnableBackgroundHeartbeat: false));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await client.ConnectAsync(timeout.Token);

            var runId = await client.StartNewSessionRunAsync(CreateRequest(), timeout.Token);
            var received = new List<RuntimeEventEnvelope>();
            await foreach (var envelope in client.ReadEventsAsync(0, timeout.Token))
            {
                if (!string.Equals(envelope.RunId, runId, StringComparison.Ordinal))
                {
                    continue;
                }

                received.Add(envelope);
                if (envelope.Gsn != 0)
                {
                    await client.AcknowledgeEventsAsync(envelope.Gsn, timeout.Token);
                }

                if (envelope.MessageType is MessageTypes.RunCompleted
                    or MessageTypes.RunFailed
                    or MessageTypes.RunCancelled)
                {
                    break;
                }
            }

            var deltas = received
                .Where(static envelope => envelope.MessageType == MessageTypes.TextDelta)
                .ToArray();
            Assert.HasCount(DeltaCount, deltas, "Every streaming delta must reach the host.");
            Assert.IsTrue(
                deltas.All(static envelope => envelope.Gsn == 0),
                "Streaming deltas are not persisted and therefore carry no GSN.");
            Assert.IsTrue(
                deltas.Zip(deltas.Skip(1)).All(static pair => pair.First.RunSequence < pair.Second.RunSequence),
                "Streaming deltas keep a monotonic Run sequence.");
            Assert.AreEqual(MessageTypes.RunCompleted, received[^1].MessageType);
            Assert.IsGreaterThan(0L, received[^1].Gsn, "Terminal events stay in the Outbox with a GSN.");
            Assert.IsTrue(
                received.Where(static envelope => envelope.Gsn != 0)
                    .Zip(received.Where(static envelope => envelope.Gsn != 0).Skip(1))
                    .All(static pair => pair.First.Gsn < pair.Second.Gsn),
                "Persisted events keep a monotonic GSN.");

            shutdown.Cancel();
            await serverTask;

            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(workspace, ".madorin", "state.db"),
                    Mode = SqliteOpenMode.ReadOnly
                }.ToString());
            await connection.OpenAsync(TestContext.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM event_outbox
                WHERE message_type IN ('output.delta', 'reasoning.delta', 'tool.call.delta');
                """;
            var persistedDeltas = Convert.ToInt64(
                await command.ExecuteScalarAsync(TestContext.CancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.AreEqual(0L, persistedDeltas, "Deltas must never be written to event_outbox.");
        }
        finally
        {
            shutdown.Cancel();
            TryDelete(workspace);
        }
    }

    private static NewSessionRunRequest CreateRequest() =>
        new(
            "streaming-session",
            "streaming-run",
            RuntimeMode.Expert,
            new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection("streaming", "streaming-model"),
                new ExpertModeOptions(new AgentRef("streaming-agent", "v1", "streaming"))),
            [new TextContentBlock("start")]);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-runtime-streaming-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class StreamingProvider(int deltaCount) : IRuntimeProviderAdapter
    {
        public string ProviderId => "streaming";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("streaming-model", "Streaming Model", ContextWindow: 8192)];

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var index = 0; index < deltaCount; index++)
            {
                ct.ThrowIfCancellationRequested();
                yield return new TextDeltaProviderEvent(request.InvocationId, $"chunk-{index} ");
                await Task.Yield();
            }

            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
