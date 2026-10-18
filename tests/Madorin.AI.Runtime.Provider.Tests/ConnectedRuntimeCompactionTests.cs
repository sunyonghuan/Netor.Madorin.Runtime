using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Server.ProviderWorkers;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ConnectedRuntimeCompactionTests(TestContext testContext)
{
    private const string Summary = """{"goals":["retained-summary-fact"],"completedFacts":[],"decisionsAndConstraints":[],"pendingWork":[],"identifiers":[]}""";

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task HostRegistryWorkerHttp_ManualSummarySurvivesTurnsRecompactionAndRestart()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "madorin-compaction-worker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var summaryCalls = 0;
        await using var http = await FakeProviderServer.StartAsync(request =>
        {
            using var body = JsonDocument.Parse(request.Body);
            var isSummary = body.RootElement.GetProperty("model").GetString() == "summary-model";
            if (isSummary)
            {
                Interlocked.Increment(ref summaryCalls);
            }
            return TextResponse(isSummary ? Summary : "main response");
        }, testContext.CancellationToken);
        var config = new HostProviderConfig("host-provider", "OpenAI Compatible",
            new Uri(http.BaseAddress, "v1").AbsoluteUri, "test-key", Models: ["chat-model", "summary-model"],
            ModelDescriptors: [new("chat-model", 32768, 2048), new("summary-model", 16384, 1024)]);
        string sessionId;
        SessionCompactionRequest compactRequest;
        SessionCompactionResultDto secondProjection;
        try
        {
            await using (var runtime = await RuntimeFixture.StartAsync(workspace, testContext.CancellationToken))
            {
                var runId = await runtime.Client.StartNewSessionRunAsync(new NewSessionRunRequest(
                    Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), RuntimeMode.Expert,
                    new NextTurnSelection(1, RuntimeMode.Expert, new DefaultSelection("host-provider", "chat-model"),
                        new ExpertModeOptions(new AgentRef("agent", "v1", "Answer using the conversation facts."),
                            contextPolicy: new ExpertContextPolicy(Strategy: "TailWindow", TailMessageCount: 3,
                                SummaryProviderId: "host-provider", SummaryModelId: "summary-model"),
                            requestControl: new ExpertRequestControl { MaxTokens = 512 })),
                    [new TextContentBlock("original-history-marker " + new string('x', 3000))],
                    ProviderConfigs: [config]), testContext.CancellationToken);
                await ReadRunAsync(runtime.Client, runId, expectedProjection: false, testContext.CancellationToken);
                sessionId = (await runtime.Client.QueryRunAsync(runId, testContext.CancellationToken)).SessionId!;
                compactRequest = new SessionCompactionRequest(sessionId, ProviderId: "host-provider",
                    ModelId: "summary-model", OperationId: Guid.NewGuid().ToString("N"), RawTailSize: 0);

                var first = await runtime.Client.CompactSessionAsync(compactRequest, testContext.CancellationToken);
                Assert.AreEqual("Applied", first.Outcome);
                Assert.IsLessThan(first.BeforeEstimatedTokens, first.AfterEstimatedTokens!.Value);
                var repeated = await runtime.Client.CompactSessionAsync(compactRequest, testContext.CancellationToken);
                Assert.AreEqual(first.ProjectionRevision, repeated.ProjectionRevision);
                Assert.AreEqual(1, summaryCalls);
                var conflict = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                    await runtime.Client.CompactSessionAsync(compactRequest with { RawTailSize = 1 }, testContext.CancellationToken));
                StringAssert.Contains(conflict.Message, "operation_id_conflict");

                for (var turn = 1; turn <= 3; turn++)
                {
                    var next = await runtime.Client.StartExistingSessionRunAsync(new ExistingSessionRunRequest(
                        sessionId, Guid.NewGuid().ToString("N"), InputOverride:
                        [new TextContentBlock($"incremental-turn-{turn} " + new string('y', 1000))]), testContext.CancellationToken);
                    await ReadRunAsync(runtime.Client, next, expectedProjection: true, testContext.CancellationToken);
                    AssertProjectedRequest(http.Requests[^1].Body, turn);
                }

                compactRequest = compactRequest with { OperationId = Guid.NewGuid().ToString("N") };
                secondProjection = await runtime.Client.CompactSessionAsync(compactRequest, testContext.CancellationToken);
                Assert.AreEqual("Applied", secondProjection.Outcome);
                Assert.AreNotEqual(first.ProjectionRevision, secondProjection.ProjectionRevision);
                Assert.IsGreaterThan(first.CoveredThroughSequence!.Value, secondProjection.CoveredThroughSequence!.Value);
                Assert.AreEqual(2, summaryCalls);
            }

            var interruptedId = Guid.NewGuid().ToString("N");
            await WriteRunningOperationAsync(workspace, sessionId, compactRequest.OperationId!, testContext.CancellationToken);
            await WriteRunningOperationAsync(workspace, sessionId, interruptedId, testContext.CancellationToken);
            await using var restarted = await RuntimeFixture.StartAsync(workspace, testContext.CancellationToken);
            await restarted.Client.UpsertProvidersAsync([config], testContext.CancellationToken);
            var status = await restarted.Client.GetSessionContextStatusAsync(
                new SessionContextStatusRequest(sessionId, compactRequest.OperationId), testContext.CancellationToken);
            Assert.AreEqual("Succeeded", status.OperationState);
            Assert.AreEqual(secondProjection.ProjectionRevision, status.ProjectionRevision);
            var interrupted = await restarted.Client.GetSessionContextStatusAsync(
                new SessionContextStatusRequest(sessionId, interruptedId), testContext.CancellationToken);
            Assert.AreEqual("Interrupted", interrupted.OperationState);
            Assert.AreEqual("runtime_restarted_before_compaction_completed", interrupted.ReasonCode);
            var replayConflict = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await restarted.Client.CompactSessionAsync(compactRequest with { RawTailSize = 1 }, testContext.CancellationToken));
            StringAssert.Contains(replayConflict.Message, "operation_id_conflict");
            var replay = await restarted.Client.CompactSessionAsync(compactRequest, testContext.CancellationToken);
            Assert.AreEqual(secondProjection.ProjectionRevision, replay.ProjectionRevision);
            Assert.AreEqual(2, summaryCalls);
            var rehydrated = await restarted.Client.RehydrateSessionAsync(new SessionRehydrateParameters(sessionId,
                [new AgentDefinition(new AgentRef("agent", "v1", "Answer using the conversation facts."))]), testContext.CancellationToken);
            Assert.AreEqual("Ready", rehydrated.Status);
            var resumedRun = await restarted.Client.StartExistingSessionRunAsync(new ExistingSessionRunRequest(
                sessionId, Guid.NewGuid().ToString("N"), InputOverride: [new TextContentBlock("after-restart")]), testContext.CancellationToken);
            await ReadRunAsync(restarted.Client, resumedRun, expectedProjection: true, testContext.CancellationToken);
            StringAssert.Contains(http.Requests[^1].Body, "retained-summary-fact");
            Assert.IsFalse(http.Requests[^1].Body.Contains("original-history-marker", StringComparison.Ordinal));
            using var resumedBody = JsonDocument.Parse(http.Requests[^1].Body);
            Assert.AreEqual(512, resumedBody.RootElement.GetProperty("max_completion_tokens").GetInt32());
            Assert.AreEqual(2, summaryCalls);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestMethod]
    [Timeout(45_000, CooperativeCancellation = true)]
    public async Task SlowCompaction_DeduplicatesAndRejectsSameSessionMutations_WithoutBlockingOtherSessions()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "madorin-compaction-concurrency-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var provider = new ControlledSummaryProvider();
        try
        {
            await using var runtime = await RuntimeFixture.StartAsync(workspace, testContext.CancellationToken, provider);
            var firstSession = await CreateSessionAsync(runtime.Client, testContext.CancellationToken);
            var otherSession = await CreateSessionAsync(runtime.Client, testContext.CancellationToken);
            var request = new SessionCompactionRequest(firstSession, OperationId: Guid.NewGuid().ToString("N"), RawTailSize: 0);
            using var disconnectedWaiter = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
            var first = runtime.Client.CompactSessionAsync(request, disconnectedWaiter.Token).AsTask();
            await provider.Started.Task.WaitAsync(testContext.CancellationToken);
            var duplicate = runtime.Client.CompactSessionAsync(request, testContext.CancellationToken).AsTask();

            try
            {
                var conflicting = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                    await runtime.Client.CompactSessionAsync(request with { OperationId = Guid.NewGuid().ToString("N") }, testContext.CancellationToken));
                StringAssert.Contains(conflicting.Message, "session_busy");
                var runConflict = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                    await runtime.Client.StartExistingSessionRunAsync(new ExistingSessionRunRequest(firstSession,
                        Guid.NewGuid().ToString("N"), InputOverride: [new TextContentBlock("blocked")]), testContext.CancellationToken));
                StringAssert.Contains(runConflict.Message, "session_busy");
                var selectionConflict = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                    await runtime.Client.UpdateSessionSelectionAsync(new SessionSelectionUpdateParameters(
                        firstSession, 1, CreateSelection() with { SelectionVersion = 2 }), testContext.CancellationToken));
                StringAssert.Contains(selectionConflict.Message, "session_busy");

                var other = await runtime.Client.CompactSessionAsync(new SessionCompactionRequest(otherSession,
                    OperationId: Guid.NewGuid().ToString("N"), RawTailSize: 0), testContext.CancellationToken)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(5), testContext.CancellationToken);
                Assert.AreEqual("Applied", other.Outcome);
                Assert.AreEqual(2, provider.SummaryCalls);
                Assert.IsFalse(first.IsCompleted);
                Assert.IsFalse(duplicate.IsCompleted);

                await disconnectedWaiter.CancelAsync();
                await Assert.ThrowsAsync<OperationCanceledException>(() => first);
                var running = await runtime.Client.GetSessionContextStatusAsync(
                    new SessionContextStatusRequest(firstSession, request.OperationId), testContext.CancellationToken);
                Assert.AreEqual("Running", running.OperationState);
            }
            finally
            {
                provider.Release.TrySetResult();
            }

            var completed = await duplicate.WaitAsync(testContext.CancellationToken);
            Assert.AreEqual("Applied", completed.Outcome);
            var status = await runtime.Client.GetSessionContextStatusAsync(
                new SessionContextStatusRequest(firstSession, request.OperationId), testContext.CancellationToken);
            Assert.AreEqual("Succeeded", status.OperationState);
            Assert.AreEqual(completed.ProjectionRevision, status.ProjectionRevision);
            Assert.AreEqual(2, provider.SummaryCalls);
        }
        finally
        {
            provider.Release.TrySetResult();
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("bad-json")]
    [DataRow("<｜DSML｜invoke name=\"memory_get_original\">continue</｜DSML｜invoke>")]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task InvalidSummary_RetriesOnceAndKeepsPreviousProjectionAndOriginalHistory(string invalidSummary)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "madorin-compaction-validation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var provider = new ControlledSummaryProvider();
        provider.Release.TrySetResult();
        try
        {
            await using var runtime = await RuntimeFixture.StartAsync(workspace, testContext.CancellationToken, provider);
            var sessionId = await CreateSessionAsync(runtime.Client, testContext.CancellationToken);
            var request = new SessionCompactionRequest(sessionId, OperationId: Guid.NewGuid().ToString("N"), RawTailSize: 0);
            var original = await runtime.Client.CompactSessionAsync(request, testContext.CancellationToken);
            Assert.AreEqual("Applied", original.Outcome);
            var cachePath = Path.Combine(workspace, ".madorin", "messages", sessionId + ".compact.json");
            var historyPath = Path.Combine(workspace, ".madorin", "messages", sessionId + ".jsonl");
            var cacheBefore = await File.ReadAllBytesAsync(cachePath, testContext.CancellationToken);
            var historyBefore = await File.ReadAllBytesAsync(historyPath, testContext.CancellationToken);
            provider.SummaryText = invalidSummary;
            request = request with { OperationId = Guid.NewGuid().ToString("N"), Force = true };

            var failed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await runtime.Client.CompactSessionAsync(request, testContext.CancellationToken));

            StringAssert.Contains(failed.Message, "summary_invalid");
            Assert.AreEqual(3, provider.SummaryCalls);
            CollectionAssert.AreEqual(cacheBefore, await File.ReadAllBytesAsync(cachePath, testContext.CancellationToken));
            CollectionAssert.AreEqual(historyBefore, await File.ReadAllBytesAsync(historyPath, testContext.CancellationToken));
            var status = await runtime.Client.GetSessionContextStatusAsync(
                new SessionContextStatusRequest(sessionId, request.OperationId), testContext.CancellationToken);
            Assert.AreEqual("Failed", status.OperationState);
            Assert.AreEqual(original.ProjectionRevision, status.ProjectionRevision);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestMethod]
    public async Task ShortHistory_ReturnsNotNeededWithoutCallingSummaryModel()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "madorin-compaction-short-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var provider = new ControlledSummaryProvider();
        try
        {
            await using var runtime = await RuntimeFixture.StartAsync(workspace, testContext.CancellationToken, provider);
            var sessionId = await CreateSessionAsync(runtime.Client, testContext.CancellationToken);
            var result = await runtime.Client.CompactSessionAsync(new SessionCompactionRequest(sessionId,
                OperationId: Guid.NewGuid().ToString("N")), testContext.CancellationToken);
            Assert.AreEqual("NotNeeded", result.Outcome);
            Assert.IsFalse(result.CacheWritten);
            Assert.AreEqual(0, provider.SummaryCalls);
        }
        finally
        {
            provider.Release.TrySetResult();
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestMethod]
    public async Task ManualFollowSelection_UsesActualAgentOverrideModelAndPersistsItsIdentity()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "madorin-compaction-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var provider = new ControlledSummaryProvider();
        provider.Release.TrySetResult();
        try
        {
            await using var runtime = await RuntimeFixture.StartAsync(workspace, testContext.CancellationToken, provider);
            var selection = CreateSelection() with
            {
                ModeOptions = new ExpertModeOptions(new AgentRef("agent", "v1", "Use the agent model.", "controlled", "agent-model"),
                    contextPolicy: new ExpertContextPolicy(Strategy: "TailWindow"))
            };
            var run = await runtime.Client.StartNewSessionRunAsync(new NewSessionRunRequest(Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"), RuntimeMode.Expert, selection,
                [new TextContentBlock("original-history-marker " + new string('x', 3000))]), testContext.CancellationToken);
            await ReadRunAsync(runtime.Client, run, expectedProjection: false, testContext.CancellationToken);
            var sessionId = (await runtime.Client.QueryRunAsync(run, testContext.CancellationToken)).SessionId!;

            var compacted = await runtime.Client.CompactSessionAsync(new SessionCompactionRequest(sessionId,
                OperationId: Guid.NewGuid().ToString("N"), RawTailSize: 0), testContext.CancellationToken);

            Assert.AreEqual("Applied", compacted.Outcome);
            Assert.AreEqual("controlled", compacted.ProviderId);
            Assert.AreEqual("agent-model", compacted.ModelId);
            Assert.AreEqual("agent-model", provider.LastSummaryRequest!.ModelId);
            Assert.AreEqual(512, provider.LastSummaryRequest.MaxTokens);
            await using var cache = File.OpenRead(Path.Combine(workspace, ".madorin", "messages", sessionId + ".compact.json"));
            using var json = await JsonDocument.ParseAsync(cache, cancellationToken: testContext.CancellationToken);
            Assert.AreEqual("agent-model", json.RootElement.GetProperty("modelId").GetString());
            var next = await runtime.Client.StartExistingSessionRunAsync(new ExistingSessionRunRequest(sessionId,
                Guid.NewGuid().ToString("N"), InputOverride: [new TextContentBlock("next turn")]), testContext.CancellationToken);
            await ReadRunAsync(runtime.Client, next, expectedProjection: true, testContext.CancellationToken);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static NextTurnSelection CreateSelection() => new(1, RuntimeMode.Expert,
        new DefaultSelection("controlled", "model"), new ExpertModeOptions(
            new AgentRef("agent", "v1", "Answer using the conversation facts."),
            contextPolicy: new ExpertContextPolicy(Strategy: "TailWindow")));

    private static async Task<string> CreateSessionAsync(RuntimeClient client, CancellationToken ct)
    {
        var runId = await client.StartNewSessionRunAsync(new NewSessionRunRequest(Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"), RuntimeMode.Expert, CreateSelection(),
            [new TextContentBlock("original-history-marker " + new string('x', 3000))]), ct);
        await ReadRunAsync(client, runId, expectedProjection: false, ct);
        return (await client.QueryRunAsync(runId, ct)).SessionId!;
    }

    private static async Task WriteRunningOperationAsync(string workspace, string sessionId, string operationId, CancellationToken ct)
    {
        static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        var directory = Path.Combine(workspace, ".madorin", "compaction-operations", Hash(sessionId));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Hash(operationId) + ".json");
        SessionContextStatusDto? previous = null;
        if (File.Exists(path))
        {
            await using var existing = File.OpenRead(path);
            previous = await JsonSerializer.DeserializeAsync(existing, RuntimeJsonContext.Default.SessionContextStatusDto, ct);
        }
        var state = new SessionContextStatusDto(sessionId, operationId, "Running", null, null,
            null, null, null, "unknown", true, null, DateTimeOffset.UtcNow, RequestFingerprint: previous?.RequestFingerprint);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, state, RuntimeJsonContext.Default.SessionContextStatusDto, ct);
    }

    private static void AssertProjectedRequest(string body, int turn)
    {
        StringAssert.Contains(body, "retained-summary-fact");
        Assert.IsFalse(body.Contains("original-history-marker", StringComparison.Ordinal));
        for (var previous = 1; previous <= turn; previous++)
        {
            StringAssert.Contains(body, $"incremental-turn-{previous}");
        }
        using var json = JsonDocument.Parse(body);
        Assert.AreEqual(512, json.RootElement.GetProperty("max_completion_tokens").GetInt32());
    }

    private static FakeProviderResponse TextResponse(string text)
    {
        var escaped = JsonSerializer.Serialize(text, RuntimeJsonContext.Default.String);
        var payload = "data: {\"id\":\"test\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":" + escaped + "},\"finish_reason\":null}]}\n\n" +
            "data: {\"id\":\"test\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        return FakeProviderResponse.EventStream(new FakeProviderChunk(payload));
    }

    private static async Task ReadRunAsync(RuntimeClient client, string runId, bool expectedProjection, CancellationToken ct)
    {
        InvocationStartedEvent? started = null;
        await foreach (var envelope in client.ReadEventsAsync(0, ct))
        {
            if (envelope.RunId != runId)
            {
                continue;
            }
            if (envelope.MessageType == MessageTypes.InvocationStarted)
            {
                started = envelope.Payload.Deserialize(RuntimeJsonContext.Default.InvocationStartedEvent);
            }
            if (envelope.MessageType is MessageTypes.RunCompleted or MessageTypes.RunFailed or MessageTypes.RunCancelled)
            {
                Assert.AreEqual(MessageTypes.RunCompleted, envelope.MessageType, envelope.Payload.ToString());
                Assert.IsNotNull(started?.Snapshot.ContextProjection);
                Assert.AreEqual(expectedProjection, started.Snapshot.ContextProjection.CacheHit);
                Assert.IsLessThan(int.MaxValue, started.Snapshot.ContextProjection.TokenLimit);
                return;
            }
        }
        Assert.Fail("No terminal Run event was returned.");
    }

    private sealed class ControlledSummaryProvider : IRuntimeProviderAdapter
    {
        private int _summaryCalls;
        public string ProviderId => "controlled";
        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);
        public IReadOnlyList<ProviderModel> Models { get; } = [new("model", "Model", ContextWindow: 32768, MaxOutputTokens: 2048),
            new("agent-model", "Agent model", ContextWindow: 8192, MaxOutputTokens: 512)];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SummaryCalls => Volatile.Read(ref _summaryCalls);
        public string SummaryText { get; set; } = Summary;
        public RuntimeProviderRequest? LastSummaryRequest { get; private set; }

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var isSummary = request.AgentId == "session-compactor";
            if (isSummary)
            {
                LastSummaryRequest = request;
            }
            if (isSummary && Interlocked.Increment(ref _summaryCalls) == 1)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            yield return new TextDeltaProviderEvent(request.InvocationId, isSummary ? SummaryText : "main response");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(ContentBlock[] input, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private readonly RuntimeServer _server;
        private readonly CancellationTokenSource _shutdown;
        private readonly Task _serverTask;

        private RuntimeFixture(RuntimeServer server, RuntimeClient client,
            CancellationTokenSource shutdown, Task serverTask)
        {
            _server = server;
            Client = client;
            _shutdown = shutdown;
            _serverTask = serverTask;
        }

        public RuntimeClient Client { get; }

        public static async Task<RuntimeFixture> StartAsync(string workspace, CancellationToken ct,
            IRuntimeProviderAdapter? provider = null)
        {
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var server = await RuntimeServer.StartAsync(new RuntimeServerOptions(workspace)
            {
                InstanceId = Guid.NewGuid().ToString("N"),
                PipePrefix = "madorin.compaction-worker." + Guid.NewGuid().ToString("N"),
                HandshakeSecret = secret,
                MemoryUserHome = Path.Combine(workspace, "home"),
                WorkerSupervisor = new ProviderWorkerSupervisor(AppContext.BaseDirectory),
                ProviderResolver = provider is null ? null : _ => provider
            }, ct);
            var shutdown = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var serverTask = server.RunAsync(shutdown.Token);
            var client = new RuntimeClient(new RuntimeClientOptions(server.PipeName, Guid.NewGuid().ToString("N"),
                ExpectedRuntimeInstanceId: server.InstanceId, HandshakeSecret: secret, EnableBackgroundHeartbeat: false));
            try
            {
                await client.ConnectAsync(ct);
                return new RuntimeFixture(server, client, shutdown, serverTask);
            }
            catch
            {
                await client.DisposeAsync();
                await shutdown.CancelAsync();
                await serverTask;
                await server.DisposeAsync();
                shutdown.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _shutdown.CancelAsync();
            await _serverTask;
            await _server.DisposeAsync();
            _shutdown.Dispose();
        }
    }
}
