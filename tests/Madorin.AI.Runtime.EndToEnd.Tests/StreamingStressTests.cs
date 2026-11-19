using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

/// <summary>
/// Stage 5 verification for the Outbox refactor. Reproduces the production failure shape
/// (long reasoning streams with a host that ACKs in batches of ten) and asserts that the
/// Runtime no longer hits SQLite lock errors, keeps <c>event_outbox</c> small, and persists
/// the complete final message to JSONL.
/// </summary>
[TestClass]
public sealed class StreamingStressTests
{
    private const int ConcurrentRuns = 3;
    private const int HostAckBatchSize = 10;
    private const int DeltasPerBurst = 10;
    private const int MaxTextChunksPerInvocation = 4000;
    private static readonly TimeSpan BurstInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan DefaultStreamDuration = TimeSpan.FromSeconds(12);
    private static readonly string[] LockErrorMarkers =
    [
        "database table is locked",
        "database is locked",
        "SQLITE_LOCKED",
        "SQLITE_BUSY"
    ];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    [Timeout(900_000, CooperativeCancellation = true)]
    public async Task ConcurrentLongStreamingRuns_CompleteWithoutLockErrors_AndKeepOutboxSmall()
    {
        var duration = ResolveStreamDuration();
        var workspace = CreateTemporaryDirectory();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var provider = new BurstStreamingProvider(duration);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = Guid.NewGuid().ToString("N"),
                    PipePrefix = $"madorin.stress.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MaxConcurrentRuns = ConcurrentRuns,
                    MaxConcurrentRunsPerProvider = ConcurrentRuns,
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
                    ExpectedRuntimeInstanceId: server.InstanceId));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            timeout.CancelAfter(duration + TimeSpan.FromMinutes(2));
            await client.ConnectAsync(timeout.Token);

            var stopwatch = Stopwatch.StartNew();
            var runIds = new List<string>(ConcurrentRuns);
            for (var index = 0; index < ConcurrentRuns; index++)
            {
                runIds.Add(await client.StartNewSessionRunAsync(CreateRequest(index), timeout.Token));
            }

            var observed = await ConsumeLikeHostAsync(client, runIds, timeout.Token);
            stopwatch.Stop();

            foreach (var runId in runIds)
            {
                var terminal = observed.Terminals[runId];
                Assert.AreEqual(
                    MessageTypes.RunCompleted,
                    terminal.MessageType,
                    $"Run {runId} ended with {terminal.MessageType}: {terminal.Payload.GetRawText()}");
            }

            var emittedDeltas = provider.EmittedDeltaCount;
            var receivedDeltas = observed.DeltaCount;
            TestContext.WriteLine(
                $"duration={duration.TotalSeconds:F0}s wall={stopwatch.Elapsed.TotalSeconds:F1}s " +
                $"runs={ConcurrentRuns} deltasEmitted={emittedDeltas} deltasReceived={receivedDeltas} " +
                $"unsequencedDeltas={observed.UnsequencedDeltaCount} " +
                $"deltaRate={emittedDeltas / Math.Max(1, duration.TotalSeconds):F0}/s " +
                $"criticalEvents={observed.CriticalCount} acks={observed.AckCount}");
            Assert.IsGreaterThan(0L, emittedDeltas);
            Assert.IsGreaterThanOrEqualTo(
                emittedDeltas * 9 / 10,
                receivedDeltas,
                "A fast host should receive essentially every streaming delta.");
            Assert.AreEqual(
                receivedDeltas,
                observed.UnsequencedDeltaCount,
                "Every streaming delta must arrive without a GSN.");

            foreach (var runId in runIds)
            {
                var run = await client.QueryRunAsync(runId, timeout.Token);
                Assert.AreEqual(RunStatus.Completed, run.Status);
                var expectedText = provider.GetFinalText(observed.InvocationIds[runId]);
                await AssertHistoryHoldsFinalTextAsync(client, run.SessionId, expectedText, timeout.Token);
            }

            shutdown.Cancel();
            await serverTask;

            var dataDirectory = Path.Combine(workspace, ".madorin");
            var outbox = await InspectOutboxAsync(Path.Combine(dataDirectory, "state.db"));
            TestContext.WriteLine(
                $"outboxRows={outbox.TotalRows} deltaRows={outbox.DeltaRows} " +
                $"stateDb={outbox.DatabaseBytes / 1024}KB types=[{string.Join(", ", outbox.MessageTypes)}]");
            Assert.AreEqual(0L, outbox.DeltaRows, $"Deltas must not be persisted in event_outbox (rows={outbox.TotalRows}).");
            Assert.IsLessThan(
                ConcurrentRuns * 20L,
                outbox.TotalRows,
                "Only a handful of critical events per Run may remain in event_outbox.");

            var lockErrors = FindLockErrors(Path.Combine(dataDirectory, "logs"));
            Assert.IsEmpty(lockErrors, string.Join(Environment.NewLine, lockErrors));
        }
        finally
        {
            shutdown.Cancel();
            TryDelete(workspace);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task HostDisconnectDuringStreaming_ReconnectReplaysOnlyCriticalEvents_HistoryHoldsFinalMessage()
    {
        var workspace = CreateTemporaryDirectory();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var provider = new BurstStreamingProvider(TimeSpan.FromSeconds(4));
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = Guid.NewGuid().ToString("N"),
                    PipePrefix = $"madorin.stress.reconnect.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    HostLeaseTimeout = TimeSpan.FromSeconds(30),
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
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await client.ConnectAsync(timeout.Token);

            var runId = await client.StartNewSessionRunAsync(CreateRequest(0), timeout.Token);
            long lastAckedGsn = 0;
            var deltasBeforeDisconnect = 0;
            string? invocationId = null;
            await foreach (var envelope in client.ReadEventsAsync(0, timeout.Token))
            {
                if (!string.Equals(envelope.RunId, runId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (IsDelta(envelope.MessageType))
                {
                    deltasBeforeDisconnect++;
                    invocationId ??= ReadInvocationId(envelope);
                    if (deltasBeforeDisconnect >= 20)
                    {
                        break;
                    }

                    continue;
                }

                await client.AcknowledgeEventsAsync(envelope.Gsn, timeout.Token);
                lastAckedGsn = envelope.Gsn;
            }

            Assert.IsGreaterThan(0L, lastAckedGsn, "run.accepted must be acknowledged before the disconnect.");
            Assert.IsNotNull(invocationId);

            await server.DisconnectEventChannelsAsync();
            await server.DisconnectControlChannelsAsync();
            await WaitForRunStatusAsync(Path.Combine(workspace, ".madorin", "state.db"), runId, RunStatus.Completed, timeout.Token);
            var deltasEmitted = provider.EmittedDeltaCount;

            await client.ReconnectAsync(timeout.Token);
            var replayed = new List<RuntimeEventEnvelope>();
            await foreach (var envelope in client.ReadEventsAsync(lastAckedGsn, timeout.Token))
            {
                if (!string.Equals(envelope.RunId, runId, StringComparison.Ordinal))
                {
                    continue;
                }

                replayed.Add(envelope);
                if (envelope.Gsn != 0)
                {
                    await client.AcknowledgeEventsAsync(envelope.Gsn, timeout.Token);
                }

                if (IsTerminal(envelope.MessageType))
                {
                    break;
                }
            }

            var replayedByType = replayed
                .GroupBy(static item => item.MessageType, StringComparer.Ordinal)
                .Select(static group => $"{group.Key}x{group.Count()}");
            TestContext.WriteLine(
                $"deltasEmitted={deltasEmitted} deltasBeforeDisconnect={deltasBeforeDisconnect} " +
                $"replayedAfterReconnect={replayed.Count} types=[{string.Join(", ", replayedByType)}]");
            Assert.IsTrue(replayed.All(static item => item.Gsn > 0), "Replay must only contain persisted critical events.");
            Assert.IsTrue(
                replayed.All(static item => item.MessageType is not (MessageTypes.TextDelta or MessageTypes.ReasoningDelta)),
                "Streaming deltas are never replayed.");
            Assert.IsTrue(replayed.All(item => item.Gsn > lastAckedGsn), "Replay starts after the acknowledged GSN.");
            Assert.AreEqual(MessageTypes.RunCompleted, replayed[^1].MessageType);
            Assert.IsLessThan(20, replayed.Count, "Replay stays bounded to a handful of critical events.");

            var run = await client.QueryRunAsync(runId, timeout.Token);
            await AssertHistoryHoldsFinalTextAsync(client, run.SessionId, provider.GetFinalText(invocationId), timeout.Token);

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            shutdown.Cancel();
            TryDelete(workspace);
        }
    }

    private static async Task<HostObservation> ConsumeLikeHostAsync(
        RuntimeClient client,
        IReadOnlyList<string> runIds,
        CancellationToken ct)
    {
        var pending = new HashSet<string>(runIds, StringComparer.Ordinal);
        var observation = new HostObservation();
        var unacked = 0;
        long batchMaxGsn = 0;
        await foreach (var envelope in client.ReadEventsAsync(0, ct))
        {
            if (IsDelta(envelope.MessageType))
            {
                observation.DeltaCount++;
                if (envelope.Gsn == 0)
                {
                    observation.UnsequencedDeltaCount++;
                }

                if (!observation.InvocationIds.ContainsKey(envelope.RunId))
                {
                    var invocationId = ReadInvocationId(envelope);
                    if (invocationId is not null)
                    {
                        observation.InvocationIds[envelope.RunId] = invocationId;
                    }
                }
            }
            else
            {
                observation.CriticalCount++;
            }

            if (envelope.Gsn == 0)
            {
                continue;
            }

            batchMaxGsn = Math.Max(batchMaxGsn, envelope.Gsn);
            if (++unacked >= HostAckBatchSize)
            {
                await client.AcknowledgeEventsAsync(batchMaxGsn, ct);
                observation.AckCount++;
                unacked = 0;
            }

            if (IsTerminal(envelope.MessageType) && pending.Remove(envelope.RunId))
            {
                observation.Terminals[envelope.RunId] = envelope;
                if (pending.Count == 0)
                {
                    break;
                }
            }
        }

        if (unacked > 0)
        {
            await client.AcknowledgeEventsAsync(batchMaxGsn, ct);
            observation.AckCount++;
        }

        return observation;
    }

    private static async Task AssertHistoryHoldsFinalTextAsync(
        RuntimeClient client,
        string sessionId,
        string expectedText,
        CancellationToken ct)
    {
        var history = await client.ReadSessionHistoryRoundsAsync(
            new SessionHistoryRoundsReadParameters(sessionId, RoundCount: 5, IncludeReasoning: false),
            ct);
        var assistantText = string.Concat(history.Messages
            .Where(static message => string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            .SelectMany(static message => message.Content)
            .OfType<TextContentBlock>()
            .Select(static block => block.Text));
        Assert.AreEqual(
            expectedText.Length,
            assistantText.Length,
            $"JSONL history for session {sessionId} must hold the complete final assistant text.");
        Assert.AreEqual(expectedText, assistantText);
    }

    private static async Task<OutboxInspection> InspectOutboxAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        await using var totalCommand = connection.CreateCommand();
        totalCommand.CommandText = "SELECT COUNT(*) FROM event_outbox;";
        var total = Convert.ToInt64(await totalCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

        await using var deltaCommand = connection.CreateCommand();
        deltaCommand.CommandText = """
            SELECT COUNT(*)
            FROM event_outbox
            WHERE message_type IN ('output.delta', 'reasoning.delta', 'tool.call.delta');
            """;
        var deltas = Convert.ToInt64(await deltaCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

        await using var typesCommand = connection.CreateCommand();
        typesCommand.CommandText = "SELECT DISTINCT message_type FROM event_outbox ORDER BY message_type;";
        var types = new List<string>();
        await using var reader = await typesCommand.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            types.Add(reader.GetString(0));
        }

        var databaseBytes = new FileInfo(databasePath).Length;
        var walPath = databasePath + "-wal";
        if (File.Exists(walPath))
        {
            databaseBytes += new FileInfo(walPath).Length;
        }

        return new OutboxInspection(total, deltas, types, databaseBytes);
    }

    private static List<string> FindLockErrors(string logDirectory)
    {
        var hits = new List<string>();
        if (!Directory.Exists(logDirectory))
        {
            return hits;
        }

        foreach (var path in Directory.EnumerateFiles(logDirectory, "*", SearchOption.AllDirectories))
        {
            string content;
            try
            {
                content = File.ReadAllText(path);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var marker in LockErrorMarkers)
            {
                if (content.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add($"{Path.GetFileName(path)}: {marker}");
                }
            }
        }

        return hits;
    }

    private async Task WaitForRunStatusAsync(
        string databasePath,
        string runId,
        RunStatus expected,
        CancellationToken ct)
    {
        var lastLogged = Stopwatch.StartNew();
        RunStatus? lastStatus = null;
        while (true)
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            await connection.OpenAsync(ct);
            var snapshot = await new Persistence.Sqlite.SqliteSessionRepository(connection)
                .GetRunSnapshotAsync(runId, ct);
            if (snapshot?.Status == expected)
            {
                return;
            }

            if (snapshot?.Status != lastStatus || lastLogged.Elapsed > TimeSpan.FromSeconds(5))
            {
                TestContext.WriteLine($"run {runId} status={snapshot?.Status.ToString() ?? "<missing>"} waiting for {expected}");
                lastStatus = snapshot?.Status;
                lastLogged.Restart();
            }

            await Task.Delay(50, ct);
        }
    }

    private static string? ReadInvocationId(RuntimeEventEnvelope envelope) =>
        envelope.Payload.TryGetProperty("invocationId", out var property)
        && property.ValueKind == System.Text.Json.JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool IsTerminal(string messageType) =>
        messageType is MessageTypes.RunCompleted or MessageTypes.RunFailed or MessageTypes.RunCancelled;

    private static bool IsDelta(string messageType) =>
        messageType is MessageTypes.TextDelta or MessageTypes.ReasoningDelta or MessageTypes.ToolCallDelta;

    private static TimeSpan ResolveStreamDuration()
    {
        var raw = Environment.GetEnvironmentVariable("MADORIN_STRESS_SECONDS");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultStreamDuration;
    }

    private static NewSessionRunRequest CreateRequest(int index) =>
        new(
            $"stress-session-{index}",
            $"stress-run-{index}",
            RuntimeMode.Expert,
            new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection("stress", "stress-model"),
                new ExpertModeOptions(new AgentRef($"stress-agent-{index}", "v1", "stress"))),
            [new TextContentBlock("start")]);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-runtime-stress-tests",
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

    private sealed class HostObservation
    {
        public long DeltaCount { get; set; }

        public long UnsequencedDeltaCount { get; set; }

        public long CriticalCount { get; set; }

        public int AckCount { get; set; }

        public Dictionary<string, RuntimeEventEnvelope> Terminals { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> InvocationIds { get; } = new(StringComparer.Ordinal);
    }

    private sealed record OutboxInspection(
        long TotalRows,
        long DeltaRows,
        IReadOnlyList<string> MessageTypes,
        long DatabaseBytes);

    /// <summary>
    /// Emits bursts of text and reasoning deltas for a fixed duration (about 400 deltas per second),
    /// then completes. Text is capped so the final message stays well under the 1 MiB control frame
    /// limit of <c>session.history.*</c>; reasoning deltas carry the remaining load. The full text per
    /// invocation is kept so JSONL history can be verified byte for byte.
    /// </summary>
    private sealed class BurstStreamingProvider(TimeSpan duration) : IRuntimeProviderAdapter
    {
        private readonly ConcurrentDictionary<string, StringBuilder> _texts = new(StringComparer.Ordinal);
        private long _emittedDeltas;

        public string ProviderId => "stress";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("stress-model", "Stress Model", ContextWindow: 128_000)];

        public long EmittedDeltaCount => Interlocked.Read(ref _emittedDeltas);

        public string GetFinalText(string invocationId) =>
            _texts.TryGetValue(invocationId, out var text) ? text.ToString() : string.Empty;

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var text = _texts.GetOrAdd(request.InvocationId, static _ => new StringBuilder());
            var deadline = Stopwatch.StartNew();
            var sequence = 0;
            var textChunks = 0;
            while (deadline.Elapsed < duration)
            {
                ct.ThrowIfCancellationRequested();
                for (var burst = 0; burst < DeltasPerBurst; burst++)
                {
                    var chunk = $"[{request.InvocationId[..8]}:{sequence++:D6}] lorem ipsum dolor sit amet ";
                    Interlocked.Increment(ref _emittedDeltas);
                    if (sequence % 2 == 0 || textChunks >= MaxTextChunksPerInvocation)
                    {
                        yield return new ReasoningDeltaProviderEvent(request.InvocationId, chunk);
                    }
                    else
                    {
                        textChunks++;
                        text.Append(chunk);
                        yield return new TextDeltaProviderEvent(request.InvocationId, chunk);
                    }
                }

                await Task.Delay(BurstInterval, ct);
            }

            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
