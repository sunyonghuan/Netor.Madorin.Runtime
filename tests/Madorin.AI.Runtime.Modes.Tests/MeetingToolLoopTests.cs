using System.Runtime.CompilerServices;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Modes.Meeting;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;

namespace Madorin.AI.Runtime.Modes.Tests;

[TestClass]
public sealed class MeetingToolLoopTests
{
    private static readonly string[] ExpectedContinuationRoles =
    [
        RuntimeProviderRoles.System,
        RuntimeProviderRoles.User,
        RuntimeProviderRoles.Assistant,
        RuntimeProviderRoles.Tool
    ];
    private static readonly string[] ExpectedHostSelectionAgentIds =
        ["agent-host", "agent-b"];
    private static readonly string[] ExpectedHostConclusionAgentIds = ["agent-host"];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RunAsync_ParticipantToolCall_ContinuesSameInvocationAndCompletesMeeting()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"madorin-meeting-tool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            await using var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken);
            var sessionRepository = new SqliteSessionRepository(connection);
            var (sessionId, runId) = await CreateRunningRunAsync(sessionRepository);
            using var outbox = new SqliteEventOutbox(connection);
            using var toolState = new SqliteToolIntentRepository(connection);
            var registry = new TestToolRegistry(requiresApproval: true);
            var validator = new JsonSchemaToolValidator();
            var catalogStore = new ToolCatalogStore(registry, validator);
            var catalog = catalogStore.CaptureSnapshot();
            var authorization = new ToolAuthorizationService(
                toolState,
                RuntimeToolPolicy.CreateRestricted(dataDirectory));
            var executor = new TestToolExecutor();
            using var gateway = new ToolGateway(
                catalogStore,
                validator,
                authorization,
                [executor],
                toolState,
                outbox);
            var approvalRequestCount = 0;
            string? approvalRequestId = null;
            Task<ApprovalResponse> ApproveAsync(
                ApprovalRequest approval,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                approvalRequestCount++;
                approvalRequestId = approval.ApprovalRequestId;
                return Task.FromResult(new ApprovalResponse(
                    approval.CorrelationId,
                    approval.ApprovalRequestId,
                    ToolAuthorizationDecision.Granted,
                    CreateGrant(approval, dataDirectory)));
            }

            var consentCoordinator = new ToolConsentCoordinator(
                gateway,
                authorization,
                catalog,
                approvalHandler: ApproveAsync,
                toolStateStore: toolState);
            var provider = new ToolLoopProvider();
            var orchestrator = new MeetingModeOrchestrator(
                _ => provider,
                new ConversationStore(dataDirectory),
                sessionRepository,
                new SqliteMeetingRepository(connection),
                outbox,
                toolGateway: gateway,
                toolCatalogSnapshot: catalog,
                toolConsentCoordinator: consentCoordinator);

            var events = new List<RuntimeEventEnvelope>();
            await foreach (var envelope in orchestrator.RunAsync(
                runId,
                sessionId,
                CreateRequest(TestToolRegistry.ToolId),
                "runtime-1",
                TestContext.CancellationToken))
            {
                events.Add(envelope);
            }

            Assert.AreEqual(2, provider.CallCount);
            Assert.AreEqual(1, executor.CallCount);
            Assert.AreEqual(1, approvalRequestCount);
            Assert.IsNotNull(approvalRequestId);
            var approvalState = await toolState.GetApprovalAsync(
                approvalRequestId,
                TestContext.CancellationToken);
            Assert.IsNotNull(approvalState);
            Assert.AreEqual("granted", approvalState.Decision);
            Assert.AreEqual(RunStatus.Completed, await sessionRepository.GetRunStatusAsync(runId));
            Assert.IsTrue(events.Any(static item =>
                item.MessageType == MessageTypes.ToolCallCompleted));
            Assert.IsTrue(events.Any(static item =>
                item.MessageType == MessageTypes.RunCompleted));
            Assert.IsNotNull(provider.FirstRequest);
            Assert.IsNotNull(provider.SecondRequest);
            Assert.AreEqual(
                provider.FirstRequest.InvocationId,
                provider.SecondRequest.InvocationId);
            CollectionAssert.AreEqual(
                ExpectedContinuationRoles,
                provider.SecondRequest.Messages.Select(static message => message.Role).ToArray());
            var result = (ToolResultContentBlock)provider.SecondRequest.Messages[^1].Content[0];
            Assert.IsTrue(result.Success);
            Assert.AreEqual("call-1", result.CallId);
            StringAssert.Contains(((TextContentBlock)result.Content[0]).Text, "42");
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RunAsync_ParticipantToolsUnsupportedByProvider_RejectsBeforePersistenceAndInvocation()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"madorin-meeting-tool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var ct = TestContext.CancellationToken;
            await using var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                ct);
            var sessionRepository = new SqliteSessionRepository(connection);
            var meetingRepository = new SqliteMeetingRepository(connection);
            var (sessionId, runId) = await CreateRunningRunAsync(sessionRepository);
            using var outbox = new SqliteEventOutbox(connection);
            using var toolState = new SqliteToolIntentRepository(connection);
            var validator = new JsonSchemaToolValidator();
            var catalogStore = new ToolCatalogStore(new TestToolRegistry(), validator);
            var catalog = catalogStore.CaptureSnapshot();
            var authorization = new ToolAuthorizationService(
                toolState,
                RuntimeToolPolicy.CreateRestricted(dataDirectory));
            using var gateway = new ToolGateway(
                catalogStore,
                validator,
                authorization,
                [new TestToolExecutor()],
                toolState,
                outbox);
            var provider = new ToolLoopProvider(supportsToolCalling: false);
            var orchestrator = new MeetingModeOrchestrator(
                _ => provider,
                new ConversationStore(dataDirectory),
                sessionRepository,
                meetingRepository,
                outbox,
                toolGateway: gateway,
                toolCatalogSnapshot: catalog);

            var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            {
                await foreach (var envelope in orchestrator.RunAsync(
                    runId,
                    sessionId,
                    CreateRequest(TestToolRegistry.ToolId),
                    "runtime-1",
                    ct))
                {
                    _ = envelope;
                }
            });

            StringAssert.Contains(exception.Message, "does not support tools");
            Assert.AreEqual(0, provider.CallCount);
            Assert.IsNull(await meetingRepository.GetMeetingSnapshotAsync(sessionId, ct));
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RunAsync_HostDrivenSelection_InvokesSelectedParticipant()
    {
        var result = await ExecuteHostMeetingAsync(conclude: false);

        CollectionAssert.AreEqual(
            ExpectedHostSelectionAgentIds,
            result.InvokedAgentIds);
        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        Assert.AreEqual("Completed", result.MeetingStatus);
        Assert.IsTrue(result.Events.Any(static item =>
            item.MessageType == MessageTypes.TextDelta));
    }

    [TestMethod]
    public async Task RunAsync_HostDriven_WithCatalog_ExposesMeetingHostTools()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"madorin-meeting-host-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var ct = TestContext.CancellationToken;
            await using var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                ct);
            var sessionRepository = new SqliteSessionRepository(connection);
            var meetingRepository = new SqliteMeetingRepository(connection);
            var (sessionId, runId) = await CreateRunningRunAsync(sessionRepository);
            using var outbox = new SqliteEventOutbox(connection);
            using var toolState = new SqliteToolIntentRepository(connection);
            var registry = new BuiltinToolRegistry();
            var validator = new JsonSchemaToolValidator();
            var catalogStore = new ToolCatalogStore(registry, validator);
            var catalog = catalogStore.CaptureSnapshot();
            var authorization = new ToolAuthorizationService(
                toolState,
                RuntimeToolPolicy.CreateRestricted(dataDirectory));
            using var gateway = new ToolGateway(
                catalogStore,
                validator,
                authorization,
                [new BuiltinMeetingToolExecutor(
                    new SqliteMeetingModeToolStore(meetingRepository),
                    dataDirectory)],
                toolState,
                outbox);
            string[]? hostTools = null;
            var provider = new CapturingHostToolProvider(tools => hostTools = tools);
            var orchestrator = new MeetingModeOrchestrator(
                _ => provider,
                new ConversationStore(dataDirectory),
                sessionRepository,
                meetingRepository,
                outbox,
                toolGateway: gateway,
                toolCatalogSnapshot: catalog);

            await foreach (var _ in orchestrator.RunAsync(
                runId,
                sessionId,
                CreateHostRequest(maxRounds: 1),
                "runtime-1",
                ct))
            {
            }

            Assert.IsNotNull(hostTools);
            CollectionAssert.Contains(hostTools, BuiltinToolRegistry.MeetingOutputSummaryToolId);
            CollectionAssert.Contains(hostTools, BuiltinToolRegistry.MeetingConfirmEndToolId);
            CollectionAssert.Contains(hostTools, BuiltinToolRegistry.AskUserToolId);
            CollectionAssert.Contains(hostTools, BuiltinToolRegistry.CheckPendingUserInputToolId);
            CollectionAssert.Contains(hostTools, BuiltinToolRegistry.MeetingListAttachmentsToolId);
            CollectionAssert.DoesNotContain(hostTools, BuiltinToolRegistry.DelegateToAgentToolId);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RunAsync_HostOutputSummary_WritesCanonicalHistoryAndRegistersMessage()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"madorin-meeting-summary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var ct = TestContext.CancellationToken;
            await using var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                ct);
            var sessionRepository = new SqliteSessionRepository(connection);
            var meetingRepository = new SqliteMeetingRepository(connection);
            var (sessionId, runId) = await CreateRunningRunAsync(sessionRepository);
            using var outbox = new SqliteEventOutbox(connection);
            using var toolState = new SqliteToolIntentRepository(connection);
            var store = new ConversationStore(dataDirectory);
            var registry = new BuiltinToolRegistry();
            var validator = new JsonSchemaToolValidator();
            var catalogStore = new ToolCatalogStore(registry, validator);
            var catalog = catalogStore.CaptureSnapshot();
            var authorization = new ToolAuthorizationService(
                toolState,
                RuntimeToolPolicy.CreateRestricted(dataDirectory));
            using var gateway = new ToolGateway(
                catalogStore,
                validator,
                authorization,
                [new BuiltinMeetingToolExecutor(
                    new SqliteMeetingModeToolStore(meetingRepository, store),
                    dataDirectory)],
                toolState,
                outbox);
            var provider = new HostOutputSummaryThenConcludeProvider();
            var orchestrator = new MeetingModeOrchestrator(
                _ => provider,
                store,
                sessionRepository,
                meetingRepository,
                outbox,
                toolGateway: gateway,
                toolCatalogSnapshot: catalog);

            await foreach (var _ in orchestrator.RunAsync(
                runId,
                sessionId,
                CreateHostRequest(maxRounds: 1),
                "runtime-1",
                ct))
            {
            }

            var history = await store.ReadAllAsync(sessionId, ct);
            Assert.IsTrue(history.Any(static record =>
                record.SummaryMetadata is not null
                && record.MessageId.StartsWith("meeting-tool-summary:", StringComparison.Ordinal)));
            var snapshot = await meetingRepository.GetMeetingSnapshotAsync(sessionId, ct);
            Assert.IsNotNull(snapshot);
            Assert.IsFalse(string.IsNullOrWhiteSpace(snapshot.Session.FinalSummaryMessageId));
            Assert.IsTrue(snapshot.Session.EndConfirmed);
            Assert.AreEqual(RunStatus.Completed, await sessionRepository.GetRunStatusAsync(runId, ct));
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RunAsync_HostAskUser_BlocksOnHitlAndContinuesWithAnswer()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"madorin-meeting-ask-user-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var ct = TestContext.CancellationToken;
            await using var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                ct);
            var sessionRepository = new SqliteSessionRepository(connection);
            var meetingRepository = new SqliteMeetingRepository(connection);
            var (sessionId, runId) = await CreateRunningRunAsync(sessionRepository);
            using var outbox = new SqliteEventOutbox(connection);
            using var toolState = new SqliteToolIntentRepository(connection);
            var registry = new BuiltinToolRegistry();
            var validator = new JsonSchemaToolValidator();
            var catalogStore = new ToolCatalogStore(registry, validator);
            var catalog = catalogStore.CaptureSnapshot();
            var authorization = new ToolAuthorizationService(
                toolState,
                RuntimeToolPolicy.CreateRestricted(dataDirectory));
            using var gateway = new ToolGateway(
                catalogStore,
                validator,
                authorization,
                [new BuiltinMeetingToolExecutor(
                    new SqliteMeetingModeToolStore(meetingRepository),
                    dataDirectory)],
                toolState,
                outbox);
            Task<ApprovalResponse> AutoGrantAsync(ApprovalRequest approval, CancellationToken grantCt)
            {
                grantCt.ThrowIfCancellationRequested();
                return Task.FromResult(new ApprovalResponse(
                    approval.CorrelationId,
                    approval.ApprovalRequestId,
                    ToolAuthorizationDecision.Granted,
                    CreateGrant(approval, dataDirectory)));
            }

            var consentCoordinator = new ToolConsentCoordinator(
                gateway,
                authorization,
                catalog,
                approvalHandler: AutoGrantAsync,
                toolStateStore: toolState);
            MeetingHitlRequest? capturedAsk = null;
            var provider = new HostAskUserThenSelectProvider();
            var orchestrator = new MeetingModeOrchestrator(
                _ => provider,
                new ConversationStore(dataDirectory),
                sessionRepository,
                meetingRepository,
                outbox,
                hitlHandler: (request, hitlCt) =>
                {
                    hitlCt.ThrowIfCancellationRequested();
                    capturedAsk = request;
                    return Task.FromResult(new MeetingHitlResponse(
                        request.ApprovalRequestId,
                        MeetingHitlAction.Supplement,
                        Supplement: "Yes, please end after summary."));
                },
                toolGateway: gateway,
                toolCatalogSnapshot: catalog,
                toolConsentCoordinator: consentCoordinator);

            var events = new List<RuntimeEventEnvelope>();
            await foreach (var envelope in orchestrator.RunAsync(
                runId,
                sessionId,
                CreateHostRequest(maxRounds: 1),
                "runtime-1",
                ct))
            {
                events.Add(envelope);
            }

            Assert.IsNotNull(capturedAsk);
            Assert.AreEqual("meeting.hitl.question.ask-user", capturedAsk.Prompt);
            StringAssert.Contains(
                ((TextContentBlock)capturedAsk.Context![0]).Text,
                "Can we end?");
            Assert.AreEqual(RunStatus.Completed, await sessionRepository.GetRunStatusAsync(runId, ct));
            Assert.IsTrue(events.Any(static item => item.MessageType == MessageTypes.RunCompleted));
            Assert.IsTrue(provider.SawUserAnswer);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RunAsync_HostConcludes_DoesNotScheduleParticipant()
    {
        var result = await ExecuteHostMeetingAsync(conclude: true);

        CollectionAssert.AreEqual(ExpectedHostConclusionAgentIds, result.InvokedAgentIds);
        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        Assert.AreEqual("Completed", result.MeetingStatus);
        Assert.AreEqual(1, result.CurrentRound);
        Assert.HasCount(1, result.Events.Where(static item =>
            item.MessageType == MessageTypes.RunCompleted));
    }

    [TestMethod]
    public async Task RunAsync_PauseAtSafePoint_ThenContinuationRun_ResumesFromHost()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"madorin-meeting-pause-resume-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var ct = TestContext.CancellationToken;
            await using var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                ct);
            var sessionRepository = new SqliteSessionRepository(connection);
            var meetingRepository = new SqliteMeetingRepository(connection);
            var (sessionId, runId) = await CreateRunningRunAsync(sessionRepository);
            using var outbox = new SqliteEventOutbox(connection);
            var store = new ConversationStore(dataDirectory);

            // Seed a meeting session in Running, then request cooperative pause.
            await meetingRepository.CreateMeetingSessionAsync(
                sessionId,
                runId,
                """{"type":"meeting"}""",
                "policy-hash",
                [
                    new MeetingParticipantInput("p-a", "agent-a", null, "A", 0),
                    new MeetingParticipantInput("p-b", "agent-b", null, "B", 1)
                ],
                selectionVersion: 1,
                ct);
            await meetingRepository.RequestPauseAsync(
                sessionId,
                "pause-1",
                "user requested pause",
                ct);
            var seeded = await meetingRepository.GetMeetingSnapshotAsync(sessionId, ct);
            Assert.IsNotNull(seeded);
            Assert.AreEqual("Running", seeded.Session.Status);
            Assert.IsTrue(seeded.Session.PauseRequested);

            var pauseProvider = new HostDecisionProvider(conclude: false);
            var pauseOrchestrator = new MeetingModeOrchestrator(
                _ => pauseProvider,
                store,
                sessionRepository,
                meetingRepository,
                outbox);
            var pauseEvents = new List<RuntimeEventEnvelope>();
            await foreach (var envelope in pauseOrchestrator.RunAsync(
                runId,
                sessionId,
                CreateHostRequest(maxRounds: 3),
                "runtime-pause",
                ct))
            {
                pauseEvents.Add(envelope);
            }

            Assert.AreEqual(RunStatus.Interrupted, await sessionRepository.GetRunStatusAsync(runId, ct));
            var pausedSnapshot = await meetingRepository.GetMeetingSnapshotAsync(sessionId, ct);
            Assert.IsNotNull(pausedSnapshot);
            Assert.AreEqual("Paused", pausedSnapshot.Session.Status);
            Assert.IsFalse(pausedSnapshot.Session.PauseRequested);
            Assert.IsTrue(pauseEvents.Any(static item =>
                item.MessageType == MessageTypes.RunCancelled));

            // Continuation run.
            var continuationRunId = await sessionRepository.CreateRunAsync(
                sessionId,
                "meeting-continuation-run",
                TimeSpan.FromDays(1),
                ct);
            await sessionRepository.TransitionRunStatusAsync(
                continuationRunId,
                RunStatus.Accepted,
                RunStatus.Preparing,
                ct);
            await sessionRepository.TransitionRunStatusAsync(
                continuationRunId,
                RunStatus.Preparing,
                RunStatus.Running,
                ct);

            var resumeProvider = new HostDecisionProvider(conclude: true);
            var resumeOrchestrator = new MeetingModeOrchestrator(
                _ => resumeProvider,
                store,
                sessionRepository,
                meetingRepository,
                outbox);
            var resumeEvents = new List<RuntimeEventEnvelope>();
            await foreach (var envelope in resumeOrchestrator.RunAsync(
                continuationRunId,
                sessionId,
                CreateHostRequest(maxRounds: 2),
                "runtime-resume",
                ct))
            {
                resumeEvents.Add(envelope);
            }

            Assert.AreEqual(
                RunStatus.Completed,
                await sessionRepository.GetRunStatusAsync(continuationRunId, ct));
            var resumedSnapshot = await meetingRepository.GetMeetingSnapshotAsync(sessionId, ct);
            Assert.IsNotNull(resumedSnapshot);
            Assert.AreEqual("Completed", resumedSnapshot.Session.Status);
            Assert.AreEqual(continuationRunId, resumedSnapshot.Session.RunId);
            CollectionAssert.Contains(resumeProvider.InvokedAgentIds, "agent-host");
            Assert.IsTrue(resumeEvents.Any(static item =>
                item.MessageType == MessageTypes.RunCompleted));

            var history = await store.ReadAllAsync(sessionId, ct);
            Assert.IsTrue(
                history.Any(static record =>
                    record.Role == "user"
                    && record.Content.ToString().Contains(
                        "continuation after cooperative pause",
                        StringComparison.OrdinalIgnoreCase)),
                "Continuation resume input message was not written to canonical history.");
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    private async Task<HostMeetingResult> ExecuteHostMeetingAsync(bool conclude)
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"madorin-meeting-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            await using var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken);
            var sessionRepository = new SqliteSessionRepository(connection);
            var (sessionId, runId) = await CreateRunningRunAsync(sessionRepository);
            using var outbox = new SqliteEventOutbox(connection);
            var meetingRepository = new SqliteMeetingRepository(connection);
            var provider = new HostDecisionProvider(conclude);
            var orchestrator = new MeetingModeOrchestrator(
                _ => provider,
                new ConversationStore(dataDirectory),
                sessionRepository,
                meetingRepository,
                outbox);
            var events = new List<RuntimeEventEnvelope>();

            await foreach (var envelope in orchestrator.RunAsync(
                runId,
                sessionId,
                CreateHostRequest(conclude ? 3 : 1),
                "runtime-1",
                TestContext.CancellationToken))
            {
                events.Add(envelope);
            }

            var snapshot = await meetingRepository.GetMeetingSnapshotAsync(
                sessionId,
                TestContext.CancellationToken);
            Assert.IsNotNull(snapshot);
            return new HostMeetingResult(
                provider.InvokedAgentIds.ToArray(),
                events,
                await sessionRepository.GetRunStatusAsync(runId),
                snapshot.Session.Status,
                snapshot.Session.CurrentRound);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    private async Task<(string SessionId, string RunId)> CreateRunningRunAsync(
        SqliteSessionRepository repository)
    {
        var sessionId = await repository.CreateSessionAsync(
            RuntimeMode.Meeting,
            "meeting-tool-session",
            TimeSpan.FromDays(1),
            TestContext.CancellationToken);
        var runId = await repository.CreateRunAsync(
            sessionId,
            "meeting-tool-run",
            TimeSpan.FromDays(1),
            TestContext.CancellationToken);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Accepted,
            RunStatus.Preparing,
            TestContext.CancellationToken);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Preparing,
            RunStatus.Running,
            TestContext.CancellationToken);
        return (sessionId, runId);
    }

    private static NewSessionRunRequest CreateRequest(string toolId) =>
        new(
            "meeting-tool-session-key",
            "meeting-tool-run-key",
            RuntimeMode.Meeting,
            new NextTurnSelection(
                1,
                RuntimeMode.Meeting,
                new DefaultSelection("tool-loop", "tool-model"),
                new MeetingModeOptions(
                    [
                        new MeetingParticipant(
                            "p-tool",
                            new AgentRef(
                                "agent-tool",
                                "v1",
                                "Use the allowed tool when needed.",
                                AllowedToolIds: [toolId]),
                            "Tool participant",
                            0),
                        new MeetingParticipant(
                            "p-standby",
                            new AgentRef("agent-standby", "v1", "Wait."),
                            "Standby participant",
                            1,
                            ParticipantStatus.Standby)
                    ],
                    selectorPolicy: new MeetingSelectorPolicyOptions(
                        MeetingSelectorPolicy.RoundRobin,
                        MaxRounds: 1))),
            [new TextContentBlock("Calculate the answer.")]);

    private static NewSessionRunRequest CreateHostRequest(int maxRounds) =>
        new(
            "meeting-host-session-key",
            "meeting-host-run-key",
            RuntimeMode.Meeting,
            new NextTurnSelection(
                1,
                RuntimeMode.Meeting,
                new DefaultSelection("host-loop", "host-model"),
                new MeetingModeOptions(
                    [
                        new MeetingParticipant(
                            "p-a",
                            new AgentRef("agent-a", "v1", "Represent A."),
                            "Participant A",
                            0),
                        new MeetingParticipant(
                            "p-b",
                            new AgentRef("agent-b", "v1", "Represent B."),
                            "Participant B",
                            1)
                    ],
                    hostAgent: new AgentRef(
                        "agent-host",
                        "v1",
                        "Select the next speaker or conclude."),
                    selectorPolicy: new MeetingSelectorPolicyOptions(
                        MeetingSelectorPolicy.HostDriven,
                        MaxRounds: maxRounds))),
            [new TextContentBlock("Discuss the proposal.")]);

    private static ToolGrant CreateGrant(ApprovalRequest approval, string workspaceRoot)
    {
        var grantId = $"grant-{approval.CallId}";
        return new ToolGrant(
            grantId,
            approval.RunId,
            workspaceRoot,
            [workspaceRoot],
            [workspaceRoot],
            AllowOverwrite: false,
            AllowMove: false,
            AllowDelete: false,
            AllowedExecutables: [],
            AllowPowerShell: false,
            new NetworkPolicy(),
            DateTimeOffset.UtcNow.AddMinutes(10),
            AllowDelegation: false,
            DelegatedAgentIds: [],
            AllowedToolIds: [approval.ToolId],
            AllowedCallIds: [approval.CallId],
            MaximumRisk: approval.Risk,
            AllowedEnvironmentVariables: [],
            AgentId: approval.AgentId,
            RootGrantId: grantId);
    }

    private sealed class HostDecisionProvider(bool conclude) : IRuntimeProviderAdapter
    {
        public string ProviderId => "host-loop";

        public ProviderCapabilities Capabilities { get; } = new(
            Streaming: true,
            StructuredOutput: true,
            ToolCalling: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("host-model", "Host Model", ContextWindow: 8192)];

        public List<string> InvokedAgentIds { get; } = [];

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            InvokedAgentIds.Add(request.AgentId);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            // Without a tool gateway the host catalog stays null; with a gateway Host may receive tools.
            if (string.Equals(request.AgentId, "agent-host", StringComparison.Ordinal))
            {
                var decision = conclude
                    ? "{\"conclude\":true,\"participantId\":null}"
                    : "{\"conclude\":false,\"participantId\":\"p-b\"}";
                yield return new TextDeltaProviderEvent(request.InvocationId, decision);
                yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
                yield break;
            }

            yield return new TextDeltaProviderEvent(request.InvocationId, "Participant B response.");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CapturingHostToolProvider(Action<string[]?> onHostTools) : IRuntimeProviderAdapter
    {
        public string ProviderId => "host-loop";

        public ProviderCapabilities Capabilities { get; } = new(
            Streaming: true,
            ToolCalling: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("host-model", "Host Model", ContextWindow: 8192)];

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (string.Equals(request.AgentId, "agent-host", StringComparison.Ordinal))
            {
                onHostTools(request.Tools?.Select(static tool => tool.ToolId).ToArray());
                yield return new TextDeltaProviderEvent(
                    request.InvocationId,
                    """{"conclude":true,"participantId":null}""");
                yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
                yield break;
            }

            yield return new TextDeltaProviderEvent(request.InvocationId, "unused");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class HostOutputSummaryThenConcludeProvider : IRuntimeProviderAdapter
    {
        private int _hostRound;

        public string ProviderId => "host-loop";

        public ProviderCapabilities Capabilities { get; } = new(
            Streaming: true,
            ToolCalling: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("host-model", "Host Model", ContextWindow: 8192)];

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (string.Equals(request.AgentId, "agent-host", StringComparison.Ordinal))
            {
                _hostRound++;
                if (_hostRound == 1)
                {
                    yield return new ToolCallCompleteProviderEvent(
                        request.InvocationId,
                        "sum-1",
                        BuiltinToolRegistry.MeetingOutputSummaryToolId,
                        "output_summary",
                        """{"summary":"# Meeting Summary\n- Topic: ship P0\n- Consensus: proceed"}""");
                    yield return new ToolCallCompleteProviderEvent(
                        request.InvocationId,
                        "end-1",
                        BuiltinToolRegistry.MeetingConfirmEndToolId,
                        "confirm_end",
                        "{}");
                    yield return new InvocationCompletedProviderEvent(
                        request.InvocationId,
                        "tool_calls");
                    yield break;
                }

                yield return new TextDeltaProviderEvent(
                    request.InvocationId,
                    """{"conclude":true,"participantId":null}""");
                yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
                yield break;
            }

            yield return new TextDeltaProviderEvent(request.InvocationId, "unused");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class HostAskUserThenSelectProvider : IRuntimeProviderAdapter
    {
        private int _hostRound;

        public string ProviderId => "host-loop";

        public ProviderCapabilities Capabilities { get; } = new(
            Streaming: true,
            ToolCalling: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("host-model", "Host Model", ContextWindow: 8192)];

        public bool SawUserAnswer { get; private set; }

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (string.Equals(request.AgentId, "agent-host", StringComparison.Ordinal))
            {
                _hostRound++;
                if (_hostRound == 1)
                {
                    yield return new ToolCallCompleteProviderEvent(
                        request.InvocationId,
                        "ask-1",
                        BuiltinToolRegistry.AskUserToolId,
                        "ask_user",
                        """{"question":"Can we end?"}""");
                    yield return new InvocationCompletedProviderEvent(
                        request.InvocationId,
                        "tool_calls");
                    yield break;
                }

                var lastTool = request.Messages
                    .LastOrDefault(static message => message.Role == RuntimeProviderRoles.Tool);
                if (lastTool?.Content.OfType<ToolResultContentBlock>().FirstOrDefault() is { } toolResult
                    && toolResult.Success
                    && toolResult.Content.OfType<TextContentBlock>().FirstOrDefault() is { } text
                    && text.Text.Contains("userAnswer", StringComparison.Ordinal))
                {
                    SawUserAnswer = true;
                }

                yield return new TextDeltaProviderEvent(
                    request.InvocationId,
                    """Next speaker. {"conclude":false,"participantId":"p-b"}""");
                yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
                yield break;
            }

            yield return new TextDeltaProviderEvent(request.InvocationId, "Participant B response.");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed record HostMeetingResult(
        string[] InvokedAgentIds,
        List<RuntimeEventEnvelope> Events,
        RunStatus RunStatus,
        string MeetingStatus,
        int CurrentRound);

    private sealed class ToolLoopProvider(bool supportsToolCalling = true) : IRuntimeProviderAdapter
    {
        public string ProviderId => "tool-loop";

        public ProviderCapabilities Capabilities { get; } = new(
            Streaming: true,
            ToolCalling: supportsToolCalling);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("tool-model", "Tool Model", ContextWindow: 8192)];

        public int CallCount { get; private set; }

        public RuntimeProviderRequest? FirstRequest { get; private set; }

        public RuntimeProviderRequest? SecondRequest { get; private set; }

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            CallCount++;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (CallCount == 1)
            {
                FirstRequest = request;
                Assert.IsNotNull(request.Tools);
                Assert.HasCount(1, request.Tools);
                Assert.AreEqual(TestToolRegistry.ToolId, request.Tools[0].ToolId);
                yield return new ToolCallCompleteProviderEvent(
                    request.InvocationId,
                    "call-1",
                    TestToolRegistry.ToolId,
                    "calculate",
                    "{\"value\":21}");
                yield return new InvocationCompletedProviderEvent(
                    request.InvocationId,
                    "tool_calls");
                yield break;
            }

            SecondRequest = request;
            yield return new TextDeltaProviderEvent(request.InvocationId, "The answer is 42.");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TestToolRegistry : IBuiltinToolRegistry
    {
        public const string ToolId = "mcli_fs_calculate";

        private static readonly ToolDescriptor Descriptor = new(
            ToolId,
            "builtin",
            "Calculate",
            "Doubles an integer.",
            "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"integer\"}},\"required\":[\"value\"],\"additionalProperties\":false}",
            "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"integer\"}},\"required\":[\"value\"],\"additionalProperties\":false}");

        private readonly ToolDescriptor _descriptor;

        public TestToolRegistry(bool requiresApproval = false)
        {
            _descriptor = Descriptor with { RequiresApproval = requiresApproval };
        }

        public string CatalogVersion => "test-v1";

        public IReadOnlyList<ToolDescriptor> GetTools() => [_descriptor];

        public IReadOnlyList<ToolDescriptor> GetTools(string[] toolIds) =>
            toolIds.Contains(ToolId, StringComparer.Ordinal) ? [_descriptor] : [];
    }

    private sealed class TestToolExecutor : IToolExecutor
    {
        public int CallCount { get; private set; }

        public bool CanExecute(string toolId) =>
            string.Equals(toolId, TestToolRegistry.ToolId, StringComparison.Ordinal);

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new ToolResult(
                invocation.CallId,
                invocation.ToolId,
                true,
                "{\"value\":42}"));
        }
    }
}
