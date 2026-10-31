using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Expert;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Modes.Expert;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services.AgentJobs;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ExpertOrchestrationRegressionTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Run_UnknownWindowReachesProviderAndPreservesServerError(bool serverRejects)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Primary.ContextWindow = null;
        var expectedError = new RuntimeError("provider_context_limit", "provider",
            "The server rejected the request context.", false, null, "server-diagnostic");
        fixture.Primary.Failure = serverRejects ? expectedError : null;

        var events = await fixture.RunWithoutContextPolicyAsync();

        var request = Assert.ContainsSingle(fixture.Primary.Requests);
        Assert.AreEqual("primary-model", request.ModelId);
        Assert.AreEqual(128, request.MaxTokens);
        Assert.AreEqual(128, request.RequestControl?.MaxTokens);
        Assert.IsTrue(request.Messages.SelectMany(message => message.Content)
            .OfType<TextContentBlock>().Any(block => block.Text == "hello without a configured window"));
        Assert.IsFalse(events.Any(item => item.MessageType == MessageTypes.ContextProjectionAdjusted));
        if (serverRejects)
        {
            var failed = Assert.ContainsSingle(events.Where(item => item.MessageType == MessageTypes.RunFailed));
            var payload = failed.Payload.Deserialize(RuntimeJsonContext.Default.RunFailedEvent);
            Assert.AreEqual(expectedError, payload!.Error);
        }
        else
        {
            Assert.ContainsSingle(events.Where(item => item.MessageType == MessageTypes.RunCompleted));
            Assert.IsFalse(events.Any(item => item.MessageType == MessageTypes.RunFailed));
        }
    }

    [TestMethod]
    public async Task Run_Delegation_InvokesResolvedProviderAndItsDefaultModel()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.StartAsync();
        Assert.HasCount(0, fixture.Primary.Requests);
        Assert.HasCount(1, fixture.Child.Requests);
        var request = fixture.Child.Requests.Single();
        Assert.AreEqual("child", request.ProviderId);
        Assert.AreEqual("child-model", request.ModelId);
        Assert.AreEqual("Reviewer", request.AgentId);
    }

    [TestMethod]
    public async Task CreateIfMissing_CapabilitiesReuseExistingAgent_InBothRoutes()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.StartAsync();
        var result = await fixture.RouteAsync("RouteDelegateAgentToolAsync",
            "mcli_delegate_agent",
            """{"agentName":"missing","task":"review","createIfMissing":true,"temporary":true,"capabilities":["REVIEW","code"]}""");
        Assert.IsTrue(result.Success);
        var job = fixture.Jobs.Create(new AgentJobCreateRequest("missing", "review",
            CreateIfMissing: true, Temporary: true, Capabilities: ["review", "CODE"]), "r", "s", "primary");
        var wait = await fixture.Jobs.WaitAsync([job.JobId], 5_000, TestContext.CancellationToken);
        Assert.IsTrue(wait.AllSucceeded);
        Assert.HasCount(3, fixture.Child.Requests);
        Assert.IsTrue(fixture.Child.Requests.All(r => r.AgentId == "Reviewer"));
    }

    [TestMethod]
    [DataRow("code reviewer", "missing-capability", "Reviewer")]
    [DataRow("REVIEWER", "missing-capability", "Reviewer")]
    [DataRow("missing", "missing-capability", "temporary")]
    public async Task Delegate_MatchesNamesIgnoringCase_OrCreatesOnlyWhenUnmatched(string name, string capability, string expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.StartAsync();
        var json = $$"""{"agentName":"{{name}}","task":"review","createIfMissing":true,"temporary":true,"capabilities":["{{capability}}"]}""";
        var result = await fixture.RouteAsync("RouteDelegateAgentToolAsync", "mcli_delegate_agent", json);
        Assert.IsTrue(result.Success);
        if (expected == "temporary")
        {
            Assert.HasCount(1, fixture.Primary.Requests);
            Assert.AreNotEqual("primary", fixture.Primary.Requests.Single().AgentId);
            Assert.HasCount(1, fixture.Child.Requests);
        }
        else
        {
            Assert.HasCount(2, fixture.Child.Requests);
            Assert.AreEqual(expected, fixture.Child.Requests.Last().AgentId);
            Assert.HasCount(0, fixture.Primary.Requests);
        }
    }

    [TestMethod]
    public async Task CompletedAndSkipped_PersistsCompleted_AndDoesNotRestoreOlderPlan()
    {
        await using var fixture = await Fixture.CreateAsync();
        var manager = new ExpertPlanManager(fixture.Root);
        var draft = new CreateExpertPlanRequest("review", [new("one", "one"), new("two", "two")]);
        await manager.CreatePlanAsync("s", "r", draft);
        await manager.CreatePlanAsync("s", "r", draft);
        await manager.UpdateStepStatusAsync("one", "r", ExpertStepStatus.Completed);
        var completed = await manager.UpdateStepStatusAsync("two", "r", ExpertStepStatus.Skipped);
        Assert.AreEqual(ExpertPlanStatus.Completed, completed.Status);
        Assert.IsTrue(completed.Steps.All(step => step.CompletedAt is not null));
        var restored = await new ExpertPlanManager(fixture.Root).RestoreActivePlanAsync("s");
        Assert.IsNull(restored);
        Assert.HasCount(1, Directory.GetFiles(fixture.Root, "*-completed.json", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task Plan_CancelledSavePreservesState_AndMixedCasePolicyPersistsEvidence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var manager = new ExpertPlanManager(fixture.Root);
        await manager.CreatePlanAsync("s", "r", new("review",
            [new("one", "one", ExecutionPolicy: "primaryagent"), new("two", "two")]));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => manager.UpdateStepStatusAsync(
            "one", "r", ExpertStepStatus.Completed, ct: cancelled.Token));
        Assert.AreEqual(ExpertStepStatus.Pending, manager.ActivePlan!.Steps[0].Status);
        var restored = await new ExpertPlanManager(fixture.Root).RestoreActivePlanAsync("s");
        Assert.AreEqual(ExpertStepStatus.Pending, restored!.Steps[0].Status);
        await manager.UpdateStepStatusAsync("one", "r", ExpertStepStatus.Running);
        await manager.RecordToolEvidenceAsync([new("primary", "call", "read", true, DateTimeOffset.UtcNow)], CancellationToken.None);
        restored = await new ExpertPlanManager(fixture.Root).RestoreActivePlanAsync("s");
        Assert.HasCount(1, restored!.Steps[0].Evidence!);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExecuteStep_Cancellation_PersistsFailureAndCancelsChildren(bool parallel)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.StartAsync();
        var policy = parallel ? "PreferParallelTemporaryAgents" : "ExistingAgentOnly";
        var json = "{\"goal\":\"review\",\"steps\":[{\"title\":\"review\",\"agentName\":\"Reviewer\",\"executionPolicy\":\"" + policy
            + "\",\"parallelTasks\":[\"one\",\"two\"],\"completionMode\":\"analysis\",\"expectedOutput\":\"review done\"},{\"title\":\"finish\"}]}";
        Assert.IsTrue((await fixture.RouteAsync("RouteExpertPlanToolAsync", "mcli_expert_create_plan", json)).Success);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var stopped = 0;
        var expected = parallel ? 2 : 1;
        fixture.Child.BeforeOutput = async ct =>
        {
            if (Interlocked.Increment(ref count) == expected)
            {
                entered.TrySetResult();
            }
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally
            {
                if (Interlocked.Increment(ref stopped) == expected)
                {
                    exited.TrySetResult();
                }
            }
        };
        using var cancellation = new CancellationTokenSource();
        var execution = fixture.RouteAsync("RouteExpertPlanToolAsync", "mcli_expert_execute_step",
            """{"stepIndex":0}""", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => execution);
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        Assert.HasCount(1, Directory.GetFiles(fixture.Root, "*-failed.json", SearchOption.AllDirectories));
        Assert.IsNull(await new ExpertPlanManager(fixture.Root).RestoreActivePlanAsync("s"));
        Assert.IsFalse((await fixture.RouteAsync("RouteExpertPlanToolAsync", "mcli_expert_execute_step",
            """{"stepIndex":0}""")).Success);
        Assert.AreEqual(expected, count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExecuteStep_Failure_IsTerminalAndNotRestored(bool providerThrows)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.StartAsync();
        var created = await fixture.RouteAsync("RouteExpertPlanToolAsync", "mcli_expert_create_plan",
            """{"goal":"review","steps":[{"title":"review","agentName":"Reviewer","executionPolicy":"ExistingAgentOnly","completionMode":"analysis","expectedOutput":"not produced"},{"title":"finish"}]}""");
        Assert.IsTrue(created.Success);
        if (providerThrows)
        {
            fixture.Child.BeforeOutput = _ => throw new InvalidOperationException("provider failed");
        }
        var result = await fixture.RouteAsync("RouteExpertPlanToolAsync", "mcli_expert_execute_step",
            """{"stepIndex":0}""");
        Assert.IsFalse(result.Success);
        Assert.HasCount(1, Directory.GetFiles(fixture.Root, "*-failed.json", SearchOption.AllDirectories));
        Assert.IsNull(await new ExpertPlanManager(fixture.Root).RestoreActivePlanAsync("s"));
        var calls = fixture.Child.Requests.Count;
        Assert.IsFalse((await fixture.RouteAsync("RouteExpertPlanToolAsync", "mcli_expert_execute_step",
            """{"stepIndex":0}""")).Success);
        Assert.HasCount(calls, fixture.Child.Requests);
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Delegate_TimeoutStopsChild_WhileCallerCancellationPropagates(bool cancelParent)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.StartAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Child.BeforeOutput = async ct =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { exited.TrySetResult(); }
        };
        using var parent = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        parent.CancelAfter(TimeSpan.FromSeconds(5));
        var execution = fixture.RouteAsync(
            "RouteDelegateAgentToolAsync", "mcli_delegate_agent",
            """{"agentName":"Reviewer","task":"review","timeoutMs":1000}""", parent.Token);
        await entered.Task.WaitAsync(parent.Token);
        if (cancelParent)
        {
            parent.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => execution);
        }
        else
        {
            var result = await execution;
            Assert.IsFalse(result.Success);
            Assert.Contains("timed out", ((TextContentBlock)result.Content.Single()).Text);
            Assert.IsFalse(parent.IsCancellationRequested);
        }
        await exited.Task.WaitAsync(TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task ExecuteStep_DoesNotReuseEvidenceOwnedByAnotherRunningStep()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.StartAsync();
        Assert.IsTrue((await fixture.RouteAsync(
            "RouteExpertPlanToolAsync", "mcli_expert_create_plan",
            """{"goal":"review","steps":[{"title":"one"},{"title":"two"}]}""")).Success);
        var manager = (ExpertPlanManager)typeof(ExpertModeOrchestrator)
            .GetField("_planManager", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Orchestrator)!;
        foreach (var step in manager.ActivePlan!.Steps)
        {
            await manager.UpdateStepStatusAsync(step.StepId, "r", ExpertStepStatus.Running);
        }
        var proof = new ExpertToolEvidence("primary", "first-step-call", "read", true, DateTimeOffset.UtcNow);
        await manager.RecordToolEvidenceAsync([proof], TestContext.CancellationToken);
        var cache = (List<ExpertToolEvidence>)typeof(ExpertModeOrchestrator)
            .GetField("_primaryToolEvidence", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Orchestrator)!;
        cache.Add(proof);
        var second = await fixture.RouteAsync("RouteExpertPlanToolAsync", "mcli_expert_execute_step",
            """{"stepIndex":1,"result":"done"}""");
        Assert.IsFalse(second.Success);
        Assert.AreEqual(ExpertStepStatus.Running, manager.ActivePlan.Steps[1].Status);
        var first = await fixture.RouteAsync("RouteExpertPlanToolAsync", "mcli_expert_execute_step",
            """{"stepIndex":0,"result":"done"}""");
        Assert.IsTrue(first.Success);
        Assert.HasCount(1, manager.ActivePlan.Steps[0].Evidence!);
    }

    [TestMethod]
    public async Task ExecuteStep_ResetToPending_DoesNotReusePreviousAttemptEvidence()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.StartAsync();
        Assert.IsTrue((await fixture.RouteAsync(
            "RouteExpertPlanToolAsync", "mcli_expert_create_plan",
            """{"goal":"review","steps":[{"title":"one"},{"title":"two"}]}""")).Success);
        var manager = (ExpertPlanManager)typeof(ExpertModeOrchestrator)
            .GetField("_planManager", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Orchestrator)!;
        var id = manager.ActivePlan!.Steps[0].StepId;
        await manager.UpdateStepStatusAsync(id, "r", ExpertStepStatus.Running);
        await manager.RecordToolEvidenceAsync(
            [new("primary", "old-call", "read", true, DateTimeOffset.UtcNow)], TestContext.CancellationToken);
        await manager.UpdateStepStatusAsync(id, "r", ExpertStepStatus.Pending);
        var restored = await new ExpertPlanManager(fixture.Root).RestoreActivePlanAsync("s");
        Assert.IsNull(restored!.Steps[0].StartedAt);
        Assert.IsTrue(restored.Steps[0].Evidence is null or { Length: 0 });
        await manager.UpdateStepStatusAsync(id, "r", ExpertStepStatus.Running);
        var result = await fixture.RouteAsync("RouteExpertPlanToolAsync", "mcli_expert_execute_step",
            """{"stepIndex":0,"result":"done"}""");
        Assert.IsFalse(result.Success);
        Assert.AreEqual(ExpertStepStatus.Running, manager.ActivePlan.Steps[0].Status);
    }

    [TestMethod]
    public async Task ParallelTemporaryJob_PreservesExplicitParentAuthorizedWriteTools()
    {
        await using var fixture = await Fixture.CreateAsync(withTools: true);
        await fixture.StartAsync();
        var job = fixture.Jobs.Create(new AgentJobCreateRequest(
            "unmatched", "inspect", CreateIfMissing: true, Temporary: true,
            AllowedToolIds: ["mcli_fs_read", "mcli_fs_write", "mcli_memory_write", "mcli_agent_job_cancel"]),
            "r", "s", "primary");
        var wait = await fixture.Jobs.WaitAsync([job.JobId], 5_000, TestContext.CancellationToken);
        Assert.IsTrue(wait.AllSucceeded);
        var request = fixture.Primary.Requests.Single();
        Assert.AreNotEqual("primary", request.AgentId);
        string[] expected = ["mcli_fs_read", "mcli_fs_write"];
        CollectionAssert.AreEquivalent(expected, request.Tools!.Select(tool => tool.ToolId).ToArray());
    }

    [TestMethod]
    public async Task Delegate_TemporaryAgent_PreservesExplicitWriteButBlocksMemoryAndJobControl()
    {
        await using var fixture = await Fixture.CreateAsync(withTools: true);
        await fixture.StartAsync();
        var result = await fixture.RouteAsync("RouteDelegateAgentToolAsync", "mcli_delegate_agent",
            """{"agentName":"unmatched","task":"inspect","createIfMissing":true,"temporary":true,"allowedToolIds":["mcli_fs_read","mcli_fs_write","mcli_memory_write","mcli_agent_job_cancel"]}""");
        Assert.IsTrue(result.Success);
        string[] expected = ["mcli_fs_read", "mcli_fs_write"];
        CollectionAssert.AreEquivalent(expected,
            fixture.Primary.Requests.Single().Tools!.Select(tool => tool.ToolId).ToArray());
    }

    [TestMethod]
    public async Task UpdateStepStatus_RejectsAmbiguousPrimaryEvidenceOwnership()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.StartAsync();
        Assert.IsTrue((await fixture.RouteAsync("RouteExpertPlanToolAsync", "mcli_expert_create_plan",
            """{"goal":"review","steps":[{"title":"one"},{"title":"two"}]}""")).Success);
        Assert.IsTrue((await fixture.RouteAsync("RouteExpertPlanToolAsync", "mcli_expert_update_step_status",
            """{"stepIndex":0,"newStatus":"running"}""")).Success);
        Assert.IsFalse((await fixture.RouteAsync("RouteExpertPlanToolAsync", "mcli_expert_update_step_status",
            """{"stepIndex":1,"newStatus":"running"}""")).Success);
        Assert.IsTrue((await fixture.RouteAsync("RouteExpertPlanToolAsync", "mcli_expert_update_step_status",
            """{"stepIndex":0,"newStatus":"pending"}""")).Success);
        Assert.IsTrue((await fixture.RouteAsync("RouteExpertPlanToolAsync", "mcli_expert_update_step_status",
            """{"stepIndex":1,"newStatus":"running"}""")).Success);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Summary_UnavailableModel_DoesNotBlockShortConversation(bool missingProvider)
    {
        await using var fixture = await Fixture.CreateAsync();
        var events = await fixture.RunWithSummaryAsync(
            missingProvider ? "missing" : "child", "host-internal-model-id",
            required: true, oversized: false);

        Assert.IsTrue(events.Any(item => item.MessageType == MessageTypes.InvocationCompleted));
        Assert.HasCount(1, fixture.Primary.Requests);
        Assert.HasCount(0, fixture.Child.Requests);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Summary_UnavailableModel_FallsBackForOptionalCompaction(bool missingProvider)
    {
        await using var fixture = await Fixture.CreateAsync();
        var events = await fixture.RunWithSummaryAsync(
            missingProvider ? "missing" : "child", "host-internal-model-id",
            required: false, oversized: true);

        Assert.IsTrue(events.Any(item => item.MessageType == MessageTypes.InvocationCompleted));
        Assert.HasCount(1, fixture.Primary.Requests);
        Assert.HasCount(0, fixture.Child.Requests);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Summary_UnavailableModel_FailsWhenCompactionIsRequired(bool missingProvider)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (missingProvider)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunWithSummaryAsync(
                "missing", "host-internal-model-id", required: true, oversized: true));
        }
        else
        {
            await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.RunWithSummaryAsync(
                "child", "host-internal-model-id", required: true, oversized: true));
        }
        Assert.HasCount(0, fixture.Primary.Requests);
        Assert.HasCount(0, fixture.Child.Requests);
    }

    [TestMethod]
    public async Task Summary_ValidCrossProvider_StillGeneratesSummary()
    {
        await using var fixture = await Fixture.CreateAsync();
        var events = await fixture.RunWithSummaryAsync(
            "child", "child-model", required: true, oversized: true);

        Assert.IsTrue(events.Any(item => item.MessageType == MessageTypes.InvocationCompleted));
        Assert.HasCount(1, fixture.Primary.Requests);
        Assert.HasCount(1, fixture.Child.Requests);
        Assert.AreEqual("child-model", fixture.Child.Requests.Single().ModelId);
    }

    [TestMethod]
    public async Task Summary_DefaultTailPolicyAndChangedModel_UsesSummaryAcrossThreeReopenedTurns()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.RunWithSummaryAsync("child", "child-model", required: true, oversized: true);
        for (var turn = 1; turn <= 3; turn++)
        {
            await fixture.ContinueSummarySessionAsync("follow-up-" + turn);
            var request = fixture.Primary.Requests.Last();
            Assert.AreEqual("replacement-model", request.ModelId);
            var text = string.Join("\n", request.Messages.SelectMany(message => message.Content)
                .OfType<TextContentBlock>().Select(block => block.Text));
            Assert.DoesNotContain(new string('x', 100), text, StringComparison.Ordinal);
            StringAssert.Contains(text, "task-42");
            StringAssert.Contains(text, "hello");
            for (var previous = 1; previous <= turn; previous++)
            {
                StringAssert.Contains(text, "follow-up-" + previous);
            }
        }
        Assert.HasCount(1, fixture.Child.Requests);
    }

    [TestMethod]
    public async Task Summary_OversizedCurrentInputDoesNotDiscardItOrReplaceExistingProjection()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.RunWithSummaryAsync("child", "child-model", required: true, oversized: true);
        var before = await fixture.ReadSummaryCacheAsync();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ContinueSummarySessionAsync(new string('z', 20_000), "primary-model"));
        StringAssert.Contains(error.Message, "summary_not_reduced");
        Assert.AreEqual(before!.ProjectionRevision, (await fixture.ReadSummaryCacheAsync())!.ProjectionRevision);
        Assert.HasCount(2, fixture.Primary.Requests);
        var summaryRequest = fixture.Primary.Requests.Last();
        Assert.AreEqual("session-compactor", summaryRequest.AgentId);
        var data = ((TextContentBlock)summaryRequest.Messages[1].Content[0]).Text;
        Assert.DoesNotContain(new string('x', 100), data, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('z', 100), data, StringComparison.Ordinal);
        StringAssert.Contains(data, "task-42");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "expert-regression-" + Guid.NewGuid().ToString("N"));
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private SqliteEventOutbox _outbox = null!;
        public RecordingProvider Primary { get; } = new("primary");
        public RecordingProvider Child { get; } = new("child");
        public AgentJobCoordinator Jobs { get; } = new();
        public ExpertModeOptions Options { get; } = new(new("primary", "1", "primary", "primary"),
            [new("PRIMARY", "1", "primary", "primary", Capabilities: ["Review", "Code"]),
                new("Reviewer", "1", "reviewer", "child", DisplayName: "Code Reviewer", Capabilities: ["Review", "Code"])]);
        public ExpertModeOrchestrator Orchestrator { get; private set; } = null!;

        private ToolGateway? _gateway;
        private SqliteToolIntentRepository? _intents;
        private string? _summarySessionId;

        public static async Task<Fixture> CreateAsync(bool withTools = false)
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture.Root);
            await fixture._connection.OpenAsync();
            await SqliteSchema.EnsureCreatedAsync(fixture._connection);
            fixture._outbox = new SqliteEventOutbox(fixture._connection);
            ToolCatalogStore? catalog = null;
            if (withTools)
            {
                var validator = new JsonSchemaToolValidator();
                catalog = new ToolCatalogStore(new BuiltinToolRegistry(), validator);
                fixture._intents = new SqliteToolIntentRepository(fixture._connection);
                fixture._gateway = new ToolGateway(catalog, validator,
                    new ToolAuthorizationService(fixture._intents, RuntimeToolPolicy.CreateRestricted(fixture.Root)),
                    [], fixture._intents, fixture._outbox);
            }
            fixture.Orchestrator = new ExpertModeOrchestrator(fixture.Primary,
                new ConversationStore(fixture.Root), new SqliteSessionRepository(fixture._connection),
                fixture._outbox, fixture.Root, toolGateway: fixture._gateway,
                liveCatalogProvider: catalog is null ? null : catalog.CaptureSnapshot,
                agentJobCoordinator: fixture.Jobs,
                providerResolver: id => id == "child" ? fixture.Child : throw new InvalidOperationException(id));
            return fixture;
        }

        public async Task StartAsync()
        {
            var request = new NewSessionRunRequest("s", "r", RuntimeMode.Expert,
                new NextTurnSelection(1, RuntimeMode.Expert, new("primary", "primary-model"), Options),
                [new TextContentBlock("@rEVIEWER review")]);
            await using var events = Orchestrator.RunAsync("r", "s", request, "runtime").GetAsyncEnumerator();
            Assert.IsTrue(await events.MoveNextAsync());
        }

        public async Task<List<RuntimeEventEnvelope>> RunWithSummaryAsync(
            string summaryProvider, string summaryModel, bool required, bool oversized)
        {
            var repository = new SqliteSessionRepository(_connection);
            var sessionId = await repository.CreateSessionAsync(
                RuntimeMode.Expert, "summary-session", TimeSpan.FromHours(1));
            _summarySessionId = sessionId;
            var runId = await repository.CreateRunAsync(sessionId, "summary-run", TimeSpan.FromHours(1));
            await repository.TransitionRunStatusAsync(runId, RunStatus.Accepted, RunStatus.Preparing);
            await repository.TransitionRunStatusAsync(runId, RunStatus.Preparing, RunStatus.Running);
            if (oversized)
            {
                await new ConversationStore(Root).AppendMessageAsync(sessionId,
                    new ConversationMessageDraft("previous", "primary", "user",
                        JsonSerializer.SerializeToElement<ContentBlock[]>(
                            [new TextContentBlock(new string('x', 40_000))],
                            RuntimeJsonContext.Default.ContentBlockArray), DateTimeOffset.UtcNow));
            }
            var options = Options with
            {
                ContextPolicy = new ExpertContextPolicy(Strategy: "Summary", MaxTokens: 2_000,
                    TailMessageCount: 1,
                    SummaryProviderId: summaryProvider, SummaryModelId: summaryModel,
                    SummaryRequired: required)
            };
            var request = new NewSessionRunRequest("s", "r", RuntimeMode.Expert,
                new NextTurnSelection(1, RuntimeMode.Expert, new("primary", "primary-model"), options),
                [new TextContentBlock("hello")]);
            var events = new List<RuntimeEventEnvelope>();
            await foreach (var item in Orchestrator.RunAsync(runId, sessionId, request, "runtime"))
            {
                events.Add(item);
            }
            return events;
        }

        public Task<ConversationCompactCacheV1?> ReadSummaryCacheAsync() =>
            new ConversationStore(Root).ReadCompactCacheAsync(_summarySessionId!);

        public async Task<List<RuntimeEventEnvelope>> RunWithoutContextPolicyAsync()
        {
            var repository = new SqliteSessionRepository(_connection);
            var sessionId = await repository.CreateSessionAsync(
                RuntimeMode.Expert, "unknown-window-session", TimeSpan.FromHours(1));
            var runId = await repository.CreateRunAsync(sessionId, "unknown-window-run", TimeSpan.FromHours(1));
            await repository.TransitionRunStatusAsync(runId, RunStatus.Accepted, RunStatus.Preparing);
            await repository.TransitionRunStatusAsync(runId, RunStatus.Preparing, RunStatus.Running);
            var options = Options with { RequestControl = new ExpertRequestControl { MaxTokens = 128 } };
            var request = new NewSessionRunRequest("s", "r", RuntimeMode.Expert,
                new NextTurnSelection(1, RuntimeMode.Expert, new("primary", "primary-model"), options),
                [new TextContentBlock("hello without a configured window")]);
            var events = new List<RuntimeEventEnvelope>();
            await foreach (var item in Orchestrator.RunAsync(runId, sessionId, request, "runtime"))
            {
                events.Add(item);
            }
            return events;
        }

        public async Task ContinueSummarySessionAsync(string text, string modelId = "replacement-model")
        {
            var repository = new SqliteSessionRepository(_connection);
            var sessionId = _summarySessionId!;
            var runId = await repository.CreateRunAsync(sessionId, Guid.NewGuid().ToString("N"), TimeSpan.FromHours(1));
            await repository.TransitionRunStatusAsync(runId, RunStatus.Accepted, RunStatus.Preparing);
            await repository.TransitionRunStatusAsync(runId, RunStatus.Preparing, RunStatus.Running);
            var reopened = new ExpertModeOrchestrator(Primary, new ConversationStore(Root), repository,
                _outbox, Root, agentJobCoordinator: Jobs);
            var options = Options with { ContextPolicy = new ExpertContextPolicy(MaxTokens: 2000, TailMessageCount: 1) };
            var request = new NewSessionRunRequest("s", "r", RuntimeMode.Expert,
                new NextTurnSelection(1, RuntimeMode.Expert, new("primary", modelId), options),
                [new TextContentBlock(text)]);
            await foreach (var _ in reopened.RunAsync(runId, sessionId, request, "runtime"))
            {
            }
        }

        // The routes remain private; invoke production methods without duplicating their logic.
        public Task<ToolResultContentBlock> RouteAsync(string method, string toolId, string json, CancellationToken ct = default)
        {
            using var document = JsonDocument.Parse(json);
            var call = new ToolCallContentBlock(Guid.NewGuid().ToString("N"), toolId, toolId,
                document.RootElement.Clone());
            var route = typeof(ExpertModeOrchestrator).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (Task<ToolResultContentBlock>)route.Invoke(Orchestrator, [call, Options, "r", "s", "invocation", ct])!;
        }

        public async ValueTask DisposeAsync()
        {
            await Jobs.DisposeAsync();
            _gateway?.Dispose();
            _intents?.Dispose();
            _outbox.Dispose();
            await _connection.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class RecordingProvider(string id) : IRuntimeProviderAdapter
    {
        public string ProviderId => id;
        public ProviderCapabilities Capabilities { get; } = new();
        public int? ContextWindow { get; set; } = 128_000;
        public RuntimeError? Failure { get; set; }
        public IReadOnlyList<ProviderModel> Models => [new(id + "-model", id, ContextWindow: ContextWindow)];
        public System.Collections.Concurrent.ConcurrentQueue<RuntimeProviderRequest> Requests { get; } = new();
        public Func<CancellationToken, Task>? BeforeOutput { get; set; }
        public Task ValidateCapabilitiesAsync(ContentBlock[] input, CancellationToken ct = default) => Task.CompletedTask;
        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            if (Failure is { } failure)
            {
                yield return new InvocationFailedProviderEvent(request.InvocationId, failure);
                yield break;
            }

            if (BeforeOutput is not null)
            {
                await BeforeOutput(ct);
            }
            yield return new TextDeltaProviderEvent(request.InvocationId,
                request.AgentId == "session-compactor" ? SummaryProjectionTests.ValidJson : "review done");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }
    }
}
