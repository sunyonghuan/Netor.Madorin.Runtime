using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Modes.Work;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services.Memory;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Modes.Tests;

[TestClass]
public sealed class WorkPendingUserInputTests
{
    private readonly TestContext _testContext;

    public WorkPendingUserInputTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    public async Task PendingInputQueue_EnqueuePeekConsume_UsesFifoOrder()
    {
        var ct = _testContext.CancellationToken;
        await using var connection = await OpenWorkDbAsync(ct);
        var workRepo = new SqliteWorkRepository(connection);

        await workRepo.EnqueuePendingUserInputAsync("session-1", "first", ct: ct);
        await workRepo.EnqueuePendingUserInputAsync("session-1", "second", ct: ct);

        var peeked = await workRepo.PeekPendingUserInputsAsync("session-1", ct: ct);
        Assert.HasCount(2, peeked);
        Assert.AreEqual("first", peeked[0].ContentText);
        Assert.AreEqual("second", peeked[1].ContentText);
        Assert.AreEqual(WorkPendingUserInputState.Pending, peeked[0].State);

        var consumed = await workRepo.ConsumePendingUserInputsAsync(
            "session-1",
            "run-consume",
            limit: 1,
            ct: ct);
        Assert.HasCount(1, consumed);
        Assert.AreEqual("first", consumed[0].ContentText);
        Assert.AreEqual(WorkPendingUserInputState.Consumed, consumed[0].State);
        Assert.AreEqual("run-consume", consumed[0].ConsumedByRunId);

        var remaining = await workRepo.PeekPendingUserInputsAsync("session-1", ct: ct);
        Assert.HasCount(1, remaining);
        Assert.AreEqual("second", remaining[0].ContentText);
        Assert.AreEqual(1, await workRepo.CountPendingUserInputsAsync("session-1", ct));
    }

    [TestMethod]
    public async Task PlanningTurn_ConsumesPendingInputs_IntoManagerContext()
    {
        var ct = _testContext.CancellationToken;
        var dataDirectory = CreateTempDir("work-pending-plan");
        try
        {
            await using var connection = await OpenWorkDbAsync(ct, dataDirectory);
            var workRepo = new SqliteWorkRepository(connection);
            using var outbox = new SqliteEventOutbox(connection);
            var sessionRepo = new SqliteSessionRepository(connection);
            var store = new ConversationStore(dataDirectory);
            await SeedMemoryAsync(dataDirectory, ct);

            await workRepo.EnqueuePendingUserInputAsync(
                "session-1",
                "修改计划：把目标改成 Ship revised。",
                ct: ct);

            const string planJson =
                "{\"planVersion\":\"1\",\"goal\":\"Ship revised\",\"steps\":["
                + "{\"stepId\":\"step-a\",\"goal\":\"Prepare notes\",\"targetAgentId\":\"worker\"}]}";
            var provider = new ScriptedProvider(planJson);
            var orchestrator = new WorkModeOrchestrator(
                _ => provider,
                store,
                sessionRepo,
                outbox,
                new AgentContextComposer(
                    new MemoryFileService(
                        Path.Join(dataDirectory, "home"),
                        Path.Join(dataDirectory, "workspace"))),
                workRepo: workRepo,
                connection: connection);

            await EnsureSessionRunAsync(connection, "session-1", "run-1", ct);
            var events = new List<RuntimeEventEnvelope>();
            await foreach (var envelope in orchestrator.RunAsync(
                "run-1",
                "session-1",
                CreateRequest("Prepare the release notes."),
                "runtime-1",
                ct))
            {
                events.Add(envelope);
            }

            Assert.AreEqual(MessageTypes.RunCompleted, events[^1].MessageType);
            Assert.AreEqual(0, await workRepo.CountPendingUserInputsAsync("session-1", ct));
            var managerText = ((TextContentBlock)provider.CapturedRequests[0].Messages[1].Content[0]).Text;
            StringAssert.Contains(managerText, "Pending user inputs");
            StringAssert.Contains(managerText, "修改计划：把目标改成 Ship revised。");
            var revisions = await workRepo.ListPlanRevisionsAsync("session-1", ct);
            Assert.HasCount(1, revisions);
        }
        finally
        {
            DeleteTempDir(dataDirectory);
        }
    }

    [TestMethod]
    public async Task ContinueWithPendingRevision_PublishesNPlusOneAndReusesCompletedStep()
    {
        var ct = _testContext.CancellationToken;
        var dataDirectory = CreateTempDir("work-pending-revision");
        try
        {
            await using var connection = await OpenWorkDbAsync(ct, dataDirectory);
            var workRepo = new SqliteWorkRepository(connection);
            using var outbox = new SqliteEventOutbox(connection);
            using var toolStateStore = new SqliteToolIntentRepository(connection);
            var sessionRepo = new SqliteSessionRepository(connection);
            var store = new ConversationStore(dataDirectory);
            await SeedMemoryAsync(dataDirectory, ct);
            var composer = new AgentContextComposer(
                new MemoryFileService(
                    Path.Join(dataDirectory, "home"),
                    Path.Join(dataDirectory, "workspace")));

            const string firstPlan =
                "{\"planVersion\":\"1\",\"goal\":\"Ship v1\",\"steps\":["
                + "{\"stepId\":\"step-a\",\"goal\":\"Prepare notes\",\"targetAgentId\":\"worker\"},"
                + "{\"stepId\":\"step-b\",\"goal\":\"Publish notes\",\"targetAgentId\":\"worker\",\"dependsOn\":[\"step-a\"]}]}";
            await RunTurnAsync(
                connection,
                store,
                sessionRepo,
                outbox,
                workRepo,
                toolStateStore,
                composer,
                "run-1",
                firstPlan,
                "Prepare the release notes.",
                ct);

            var v1Steps = await workRepo.ListStepsAsync("session-1", "1", ct);
            Assert.HasCount(2, v1Steps);
            Assert.IsTrue(v1Steps.All(static s => s.Status == WorkStepLifecycleStatus.Completed));

            await workRepo.EnqueuePendingUserInputAsync(
                "session-1",
                "修改计划并确认执行：保留 step-a，只重做后续。",
                ct: ct);

            const string secondPlan =
                "{\"planVersion\":\"1\",\"goal\":\"Ship v2\",\"steps\":["
                + "{\"stepId\":\"step-a\",\"goal\":\"Prepare notes\",\"targetAgentId\":\"worker\",\"reusesStepId\":\"step-a\"},"
                + "{\"stepId\":\"step-c\",\"goal\":\"Rewrite notes\",\"targetAgentId\":\"worker\"}]}";
            await RunTurnAsync(
                connection,
                store,
                sessionRepo,
                outbox,
                workRepo,
                toolStateStore,
                composer,
                "run-2",
                secondPlan,
                "Continue after interrupt.",
                ct);

            var revisions = await workRepo.ListPlanRevisionsAsync("session-1", ct);
            Assert.HasCount(2, revisions);
            Assert.AreEqual("2", revisions[1].PlanVersion);
            Assert.AreEqual("1", revisions[1].PreviousPlanVersion);
            Assert.AreEqual(0, await workRepo.CountPendingUserInputsAsync("session-1", ct));

            var v2Steps = await workRepo.ListStepsAsync("session-1", "2", ct);
            Assert.HasCount(2, v2Steps);
            var reused = v2Steps.Single(static s => s.StepId == "step-a");
            Assert.AreEqual(WorkStepLifecycleStatus.Completed, reused.Status);
            Assert.AreEqual("step-a", reused.ReusesStepId);
            Assert.AreEqual(
                WorkStepLifecycleStatus.Completed,
                v2Steps.Single(static s => s.StepId == "step-c").Status);

            var resume = await workRepo.GetResumeStateAsync("session-1", ct);
            Assert.IsNotNull(resume);
            Assert.AreEqual(0, resume.PendingUserInputCount);
        }
        finally
        {
            DeleteTempDir(dataDirectory);
        }
    }

    private static async Task RunTurnAsync(
        SqliteConnection connection,
        ConversationStore store,
        SqliteSessionRepository sessionRepo,
        SqliteEventOutbox outbox,
        SqliteWorkRepository workRepo,
        SqliteToolIntentRepository toolStateStore,
        AgentContextComposer composer,
        string runId,
        string managerOutput,
        string userText,
        CancellationToken ct)
    {
        var provider = new ScriptedProvider(managerOutput);
        var orchestrator = new WorkModeOrchestrator(
            _ => provider,
            store,
            sessionRepo,
            outbox,
            composer,
            toolStateStore: toolStateStore,
            workRepo: workRepo,
            connection: connection);
        await EnsureSessionRunAsync(connection, "session-1", runId, ct);
        await foreach (var _ in orchestrator.RunAsync(
            runId,
            "session-1",
            CreateRequest(userText),
            "runtime-1",
            ct))
        {
        }
    }

    private string CreateTempDir(string prefix)
    {
        var path = Path.Combine(
            _testContext.TestRunDirectory ?? Path.GetTempPath(),
            $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDir(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static async Task SeedMemoryAsync(string dataDirectory, CancellationToken ct)
    {
        var memoryFiles = new MemoryFileService(
            Path.Join(dataDirectory, "home"),
            Path.Join(dataDirectory, "workspace"));
        await memoryFiles.ReplaceAsync(MemoryScope.Global, "# Memory\n\n- Shared global rule.\n", ct);
        await memoryFiles.ReplaceAsync(MemoryScope.Project, "# Memory\n\n- Shared project rule.\n", ct);
    }

    private static async Task<SqliteConnection> OpenWorkDbAsync(
        CancellationToken ct,
        string? dataDirectory = null)
    {
        dataDirectory ??= Path.Combine(Path.GetTempPath(), $"work-pending-db-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "state.db"),
            Pooling = false
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        await SqliteSchema.EnsureCreatedAsync(connection, ct);
        return connection;
    }

    private static async Task EnsureSessionRunAsync(
        SqliteConnection connection,
        string sessionId,
        string runId,
        CancellationToken ct)
    {
        await using var sessionCommand = connection.CreateCommand();
        sessionCommand.CommandText = """
            INSERT OR IGNORE INTO sessions(session_id, mode, status, created_at, updated_at)
            VALUES($sessionId, 'Work', 'Active', '2026-07-23T00:00:00.000Z', '2026-07-23T00:00:00.000Z');
            """;
        sessionCommand.Parameters.AddWithValue("$sessionId", sessionId);
        await sessionCommand.ExecuteNonQueryAsync(ct);

        await using var runCommand = connection.CreateCommand();
        runCommand.CommandText = """
            INSERT INTO runs(run_id, session_id, status, run_sequence, created_at, updated_at)
            VALUES($runId, $sessionId, 'Running', 1, '2026-07-23T00:00:00.000Z', '2026-07-23T00:00:00.000Z');
            """;
        runCommand.Parameters.AddWithValue("$runId", runId);
        runCommand.Parameters.AddWithValue("$sessionId", sessionId);
        await runCommand.ExecuteNonQueryAsync(ct);
    }

    private static NewSessionRunRequest CreateRequest(string userText)
    {
        var manager = new AgentRef("manager", "v1", "Create a plan.", "fake", "test-model");
        var worker = new AgentRef("worker", "v1", "Execute the step.", "fake", "test-model");
        return new NewSessionRunRequest(
            "session-key",
            "run-key",
            RuntimeMode.Work,
            new NextTurnSelection(
                1,
                RuntimeMode.Work,
                new DefaultSelection("fake", "test-model"),
                new WorkModeOptions(manager, [worker]),
                ToolCatalogVersion: "tools-v1"),
            [new TextContentBlock(userText)]);
    }

    private sealed class ScriptedProvider : IRuntimeProviderAdapter
    {
        private readonly string _managerOutput;
        private readonly List<RuntimeProviderRequest> _capturedRequests = [];

        public ScriptedProvider(string managerOutput)
        {
            _managerOutput = managerOutput;
        }

        public string ProviderId => "fake";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } = [new("test-model", "Test Model")];

        public List<RuntimeProviderRequest> CapturedRequests => _capturedRequests;

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _capturedRequests.Add(request);
            yield return new TextDeltaProviderEvent(
                request.InvocationId,
                string.Equals(request.AgentId, "manager", StringComparison.Ordinal)
                    ? _managerOutput
                    : $"result:{request.AgentId}");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
            await Task.CompletedTask;
        }
    }
}
