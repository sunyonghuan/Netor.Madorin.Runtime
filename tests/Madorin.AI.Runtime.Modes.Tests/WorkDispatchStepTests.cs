using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Work;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Modes.Work;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Tools.Builtin.Work;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Modes.Tests;

[TestClass]
public sealed class WorkDispatchStepTests
{
    private readonly TestContext _testContext;

    public WorkDispatchStepTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    public async Task SyncDispatch_RejectsNonPendingAndPersistsChildLifecycleEvents()
    {
        var ct = _testContext.CancellationToken;
        var dataDirectory = Path.Combine(
            _testContext.TestRunDirectory ?? Path.GetTempPath(),
            $"work-dispatch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(dataDirectory, "state.db"),
                Pooling = false
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await SqliteSchema.EnsureCreatedAsync(connection, ct);
            using var outbox = new SqliteEventOutbox(connection);
            var sessionRepo = new SqliteSessionRepository(connection);
            var workRepo = new SqliteWorkRepository(connection);
            var store = new ConversationStore(dataDirectory);
            var memoryFiles = new MemoryFileService(
                Path.Join(dataDirectory, "home"),
                Path.Join(dataDirectory, "workspace"));
            await memoryFiles.ReplaceAsync(MemoryScope.Global, "# Memory\n\n- g\n", ct);
            await memoryFiles.ReplaceAsync(MemoryScope.Project, "# Memory\n\n- p\n", ct);

            WorkDispatchStepResult? dispatchB = null;
            WorkDispatchStepResult? redispatchB = null;
            WorkDispatchStepResult? dispatchA = null;
            var bridge = new WorkStepDispatchBridge();
            var provider = new DispatchProbeProvider(
                async (request, token) =>
                {
                    dispatchB = await bridge.RunAsync(
                        new WorkDispatchStepRequest(
                            "session-1",
                            "run-1",
                            "manager",
                            "call-dispatch-b",
                            "1",
                            "2.1",
                            "worker",
                            "Prepare B",
                            "do B now",
                            StepIndex: 2,
                            SubStepIndex: 1,
                            InvocationId: request.InvocationId),
                        token);
                    redispatchB = await bridge.RunAsync(
                        new WorkDispatchStepRequest(
                            "session-1",
                            "run-1",
                            "manager",
                            "call-dispatch-b2",
                            "1",
                            "2.1",
                            "worker",
                            "Prepare B",
                            "again",
                            2,
                            1),
                        token);
                    dispatchA = await bridge.RunAsync(
                        new WorkDispatchStepRequest(
                            "session-1",
                            "run-1",
                            "manager",
                            "call-dispatch-a",
                            "1",
                            "1.1",
                            "worker",
                            "Prepare A",
                            "steal",
                            1,
                            1),
                        token);
                });

            var orchestrator = new WorkModeOrchestrator(
                _ => provider,
                store,
                sessionRepo,
                outbox,
                new AgentContextComposer(memoryFiles),
                workRepo: workRepo,
                connection: connection);
            orchestrator.AttachDispatchBridge(bridge);

            await EnsureSessionRunAsync(connection, "session-1", "run-1", ct);
            const string planJson =
                "{\"planVersion\":\"1\",\"goal\":\"Ship\",\"steps\":["
                + "{\"stepId\":\"1\",\"title\":\"Prepare A\",\"description\":\"Prepare A\","
                + "\"subSteps\":[{\"subStepId\":\"1.1\",\"goal\":\"Prepare A\",\"instruction\":\"Prepare A\",\"targetAgentId\":\"worker\"}]},"
                + "{\"stepId\":\"2\",\"title\":\"Prepare B\",\"description\":\"Prepare B\","
                + "\"subSteps\":[{\"subStepId\":\"2.1\",\"goal\":\"Prepare B\",\"instruction\":\"Prepare B\",\"targetAgentId\":\"worker\"}]}]}";
            provider.ManagerPlanJson = planJson;

            var events = new List<RuntimeEventEnvelope>();
            await foreach (var envelope in orchestrator.RunAsync(
                "run-1",
                "session-1",
                CreateRequest(),
                "runtime-1",
                ct))
            {
                events.Add(envelope);
            }

            Assert.IsNotNull(dispatchB);
            Assert.IsTrue(dispatchB!.Executed, dispatchB.ErrorMessage);
            Assert.IsFalse(string.IsNullOrWhiteSpace(dispatchB.ChildInvocationId));
            StringAssert.Contains(dispatchB.OutputText ?? string.Empty, "result:worker");

            Assert.IsNotNull(redispatchB);
            Assert.IsFalse(redispatchB!.Executed);
            Assert.AreEqual("step_not_dispatchable", redispatchB.ErrorCode);

            Assert.IsNotNull(dispatchA);
            Assert.IsFalse(dispatchA!.Executed);
            Assert.AreEqual("step_not_dispatchable", dispatchA.ErrorCode);

            var pending = await outbox.LoadPendingAsync(ct);
            Assert.IsTrue(
                pending.Any(item =>
                    item.MessageType == MessageTypes.InvocationStarted
                    && item.PayloadJson.Contains(dispatchB.ChildInvocationId!, StringComparison.Ordinal)),
                "dispatch child invocation.started should land in outbox");
            Assert.IsTrue(
                pending.Any(item =>
                    item.MessageType == MessageTypes.TextDelta
                    && item.PayloadJson.Contains(dispatchB.ChildInvocationId!, StringComparison.Ordinal)),
                "dispatch child text.delta should land in outbox");
            Assert.IsTrue(
                pending.Any(item =>
                    item.MessageType == MessageTypes.InvocationCompleted
                    && item.PayloadJson.Contains(dispatchB.ChildInvocationId!, StringComparison.Ordinal)),
                "dispatch child invocation.completed should land in outbox");

            var steps = await workRepo.ListStepsAsync("session-1", "1", ct);
            Assert.AreEqual(
                WorkStepLifecycleStatus.Completed,
                steps.Single(static step => step.StepId == "1.1").Status);
            Assert.AreEqual(
                WorkStepLifecycleStatus.Completed,
                steps.Single(static step => step.StepId == "2.1").Status);
            Assert.IsTrue(events.Any(static item => item.MessageType == MessageTypes.RunCompleted));
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
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
        await sessionCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var runCommand = connection.CreateCommand();
        runCommand.CommandText = """
            INSERT INTO runs(run_id, session_id, status, run_sequence, created_at, updated_at)
            VALUES($runId, $sessionId, 'Running', 1, '2026-07-23T00:00:00.000Z', '2026-07-23T00:00:00.000Z');
            """;
        runCommand.Parameters.AddWithValue("$runId", runId);
        runCommand.Parameters.AddWithValue("$sessionId", sessionId);
        await runCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static NewSessionRunRequest CreateRequest()
    {
        var manager = new AgentRef(
            "manager",
            "v1",
            "Create a plan.",
            "fake",
            "test-model");
        var worker = new AgentRef(
            "worker",
            "v1",
            "Execute the assigned step.",
            "fake",
            "test-model");
        var policy = WorkflowPolicy.Default with
        {
            RequirePlanConfirmation = false,
            MaxConcurrentSteps = 1
        };
        return new NewSessionRunRequest(
            "session-key",
            "run-key",
            RuntimeMode.Work,
            new NextTurnSelection(
                1,
                RuntimeMode.Work,
                new DefaultSelection("fake", "test-model"),
                new WorkModeOptions(manager, [worker], policy),
                ToolCatalogVersion: "tools-v1"),
            [new TextContentBlock("Prepare the release notes.")]);
    }

    private sealed class DispatchProbeProvider : IRuntimeProviderAdapter
    {
        private readonly Func<RuntimeProviderRequest, CancellationToken, Task> _onFirstWorker;
        private int _workerInvocationCount;

        public DispatchProbeProvider(
            Func<RuntimeProviderRequest, CancellationToken, Task> onFirstWorker)
        {
            _onFirstWorker = onFirstWorker;
        }

        public string? ManagerPlanJson { get; set; }

        public string ProviderId => "fake";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } = [new("test-model", "Test Model")];

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(request.AgentId, "worker", StringComparison.Ordinal)
                && Interlocked.Increment(ref _workerInvocationCount) == 1)
            {
                await _onFirstWorker(request, ct).ConfigureAwait(false);
            }

            await Task.Yield();
            yield return new TextDeltaProviderEvent(
                request.InvocationId,
                string.Equals(request.AgentId, "manager", StringComparison.Ordinal)
                    ? ManagerPlanJson
                        ?? throw new InvalidOperationException("Manager plan JSON was not set.")
                    : $"result:{request.AgentId}");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }
    }
}
