using System.Runtime.CompilerServices;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Modes.Expert;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;

namespace Madorin.AI.Runtime.Modes.Tests;

[TestClass]
public sealed class ExpertToolLoopTests
{
    private static readonly string[] ExpectedSecondRequestRoles =
    [
        RuntimeProviderRoles.System,
        RuntimeProviderRoles.User,
        RuntimeProviderRoles.Assistant,
        RuntimeProviderRoles.Tool
    ];
    private static readonly string[] ExpectedPersistedRoles =
        ["user", "assistant", "tool", "assistant"];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void RuntimeLimits_DefaultToolRoundLimitIsUnlimited()
    {
        Assert.IsNull(new RuntimeLimits().MaxToolRounds);
    }

    [TestMethod]
    public void DiagnosticTest_ToolFilteringForExpertMode()
    {
        var registry = new TestToolRegistry();
        var validator = new JsonSchemaToolValidator();
        var catalogStore = new ToolCatalogStore(registry, validator);
        var catalog = catalogStore.CaptureSnapshot();

        Console.WriteLine($"Before filtering: {catalog.Tools.Count} tools");
        foreach (var tool in catalog.Tools)
        {
            Console.WriteLine($"  - {tool.ToolId}, SupportedModes: {(tool.SupportedModes == null ? "null" : string.Join(",", tool.SupportedModes))}");
        }

        var filtered = catalog.FilterByMode("expert");
        Console.WriteLine($"After filtering: {filtered.Tools.Count} tools");
        foreach (var tool in filtered.Tools)
        {
            Console.WriteLine($"  - {tool.ToolId}, SupportedModes: {(tool.SupportedModes == null ? "null" : string.Join(",", tool.SupportedModes))}");
        }

        Assert.IsTrue(filtered.Tools.Any(t => t.ToolId == TestToolRegistry.ToolId),
            $"Test tool {TestToolRegistry.ToolId} should be visible in expert mode");
    }

    [TestMethod]
    public async Task RunAsync_ToolCall_ExecutesOnceAndContinuesSameProvider()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"madorin-expert-tool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            await using var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken);
            var repository = new SqliteSessionRepository(connection);
            var sessionId = await repository.CreateSessionAsync(
                RuntimeMode.Expert,
                "tool-session",
                TimeSpan.FromDays(1),
                TestContext.CancellationToken);
            var runId = await repository.CreateRunAsync(
                sessionId,
                "tool-run",
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

            using var outbox = new SqliteEventOutbox(connection);
            using var toolState = new SqliteToolIntentRepository(connection);
            var registry = new TestToolRegistry();
            var validator = new JsonSchemaToolValidator();
            var catalogStore = new ToolCatalogStore(registry, validator);
            var catalog = catalogStore.CaptureSnapshot();
            var authorization = new ToolAuthorizationService(
                toolState,
                RuntimeToolPolicy.CreateRestricted(dataDirectory));

            // Create a Grant to allow the test tool
            var grant = new ToolGrant(
                GrantId: "test-grant",
                RunId: runId,
                WorkspaceRoot: dataDirectory,
                AllowedReadRoots: [dataDirectory],
                AllowedWriteRoots: [dataDirectory],
                AllowOverwrite: true,
                AllowMove: true,
                AllowDelete: true,
                AllowedExecutables: [],
                AllowPowerShell: false,
                NetworkPolicy: new NetworkPolicy(DenyAll: false, AllowedHosts: []),
                ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
                AllowDelegation: false,
                DelegatedAgentIds: [],
                AllowedToolIds: [TestToolRegistry.ToolId],
                AllowedCallIds: null,
                MaximumRisk: ToolRiskLevel.Network,
                AllowedEnvironmentVariables: null,
                AgentId: null,
                ParentGrantId: null,
                RootGrantId: null,
                DelegationChain: null,
                ApprovalRequestId: null,
                AllowedSkillScriptKeys: null,
                SkillCatalogVersion: null);
            await authorization.SaveGrantAsync(grant, agentId: null, TestContext.CancellationToken);

            var executor = new TestToolExecutor();
            using var gateway = new ToolGateway(
                catalogStore,
                validator,
                authorization,
                [executor],
                toolState,
                outbox);
            var provider = new ToolLoopProvider();
            var orchestrator = new ExpertModeOrchestrator(
                provider,
                new ConversationStore(dataDirectory),
                repository,
                outbox,
                dataDirectory,
                toolGateway: gateway,
                toolAuthorizationService: authorization,
                toolCatalogSnapshot: catalog);

            var events = new List<RuntimeEventEnvelope>();
            await foreach (var envelope in orchestrator.RunAsync(
                runId,
                sessionId,
                CreateRequest(),
                "runtime-1",
                TestContext.CancellationToken))
            {
                events.Add(envelope);
                Console.WriteLine($"Event: {envelope.MessageType}");
            }

            Console.WriteLine($"Total events: {events.Count}");
            Console.WriteLine($"Event types: {string.Join(", ", events.Select(e => e.MessageType))}");

            var toolCompletedEvents = events.Where(e => e.MessageType == MessageTypes.ToolCallCompleted).ToList();
            Console.WriteLine($"ToolCallCompleted events: {toolCompletedEvents.Count}");

            Assert.AreEqual(2, provider.CallCount);
            Assert.AreEqual(1, executor.CallCount);
            Assert.AreEqual(RunStatus.Completed, await repository.GetRunStatusAsync(runId));
            Assert.IsTrue(events.Any(static item =>
                item.MessageType == MessageTypes.ToolCallCompleted));
            Assert.IsTrue(events.Any(item =>
                item.MessageType == MessageTypes.RunCompleted));
            Assert.IsNotNull(provider.SecondRequest);
            CollectionAssert.AreEqual(
                ExpectedSecondRequestRoles,
                provider.SecondRequest.Messages.Select(static message => message.Role).ToArray());
            Assert.AreEqual(
                provider.SecondRequest.Messages.Length,
                provider.SecondRequest.HistoryMessageCount);
            var result = (ToolResultContentBlock)provider.SecondRequest.Messages[^1].Content[0];
            Assert.IsTrue(result.Success);
            Assert.AreEqual("call-1", result.CallId);
            StringAssert.Contains(((TextContentBlock)result.Content[0]).Text, "42");

            var history = await new ConversationStore(dataDirectory).ReadAllAsync(
                sessionId,
                TestContext.CancellationToken);
            CollectionAssert.AreEqual(
                ExpectedPersistedRoles,
                history.Select(static record => record.Role).ToArray());
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
    public async Task RunAsync_MemoryRead_ReturnsCurrentLinesToSameProvider()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"madorin-expert-memory-tool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            await using var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken);
            var repository = new SqliteSessionRepository(connection);
            var sessionId = await repository.CreateSessionAsync(
                RuntimeMode.Expert,
                "memory-tool-session",
                TimeSpan.FromDays(1),
                TestContext.CancellationToken);
            var runId = await repository.CreateRunAsync(
                sessionId,
                "memory-tool-run",
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

            var userHome = Path.Combine(dataDirectory, "home");
            var workspace = Path.Combine(dataDirectory, "workspace");
            var memoryFiles = new MemoryFileService(userHome, workspace);
            const string expectedMemory = "# Memory\n\n- Preserve exact memory text.\n";
            await memoryFiles.ReplaceAsync(
                MemoryScope.Project,
                expectedMemory,
                TestContext.CancellationToken);

            using var outbox = new SqliteEventOutbox(connection);
            using var toolState = new SqliteToolIntentRepository(connection);
            var registry = new BuiltinToolRegistry();
            var validator = new JsonSchemaToolValidator();
            var catalogStore = new ToolCatalogStore(registry, validator);
            var catalog = catalogStore.CaptureSnapshot();
            var authorization = new ToolAuthorizationService(
                toolState,
                RuntimeToolPolicy.CreateRestricted(workspace));
            var memorySnapshots = new MemoryInvocationSnapshotStore();
            using var gateway = new ToolGateway(
                catalogStore,
                validator,
                authorization,
                [new BuiltinMemoryToolExecutor(memoryFiles)],
                toolState,
                outbox);
            var provider = new MemoryToolLoopProvider(expectedMemory);
            var orchestrator = new ExpertModeOrchestrator(
                provider,
                new ConversationStore(dataDirectory),
                repository,
                outbox,
                dataDirectory,
                agentContextComposer: new AgentContextComposer(memoryFiles),
                toolGateway: gateway,
                toolAuthorizationService: authorization,
                toolCatalogSnapshot: catalog,
                memorySnapshotStore: memorySnapshots);

            var events = new List<RuntimeEventEnvelope>();
            await foreach (var envelope in orchestrator.RunAsync(
                runId,
                sessionId,
                CreateMemoryRequest(),
                "runtime-memory",
                TestContext.CancellationToken))
            {
                events.Add(envelope);
            }

            Assert.AreEqual(2, provider.CallCount);
            Assert.AreEqual(RunStatus.Completed, await repository.GetRunStatusAsync(runId));
            Assert.IsTrue(events.Any(static item =>
                item.MessageType == MessageTypes.ToolCallCompleted));
            Assert.IsTrue(events.Any(static item =>
                item.MessageType == MessageTypes.RunCompleted));
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
    public async Task RunAsync_DefaultToolRoundLimit_AllowsMoreThanEightRounds()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"madorin-expert-tool-many-rounds-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            await using var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken);
            var repository = new SqliteSessionRepository(connection);
            var sessionId = await repository.CreateSessionAsync(
                RuntimeMode.Expert,
                "tool-many-rounds-session",
                TimeSpan.FromDays(1),
                TestContext.CancellationToken);
            var runId = await repository.CreateRunAsync(
                sessionId,
                "tool-many-rounds-run",
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

            using var outbox = new SqliteEventOutbox(connection);
            using var toolState = new SqliteToolIntentRepository(connection);
            var registry = new TestToolRegistry();
            var validator = new JsonSchemaToolValidator();
            var catalogStore = new ToolCatalogStore(registry, validator);
            var catalog = catalogStore.CaptureSnapshot();
            var authorization = new ToolAuthorizationService(
                toolState,
                RuntimeToolPolicy.CreateRestricted(dataDirectory));

            // Create a Grant to allow the test tool
            var grant = new ToolGrant(
                GrantId: "test-grant",
                RunId: runId,
                WorkspaceRoot: dataDirectory,
                AllowedReadRoots: [dataDirectory],
                AllowedWriteRoots: [dataDirectory],
                AllowOverwrite: true,
                AllowMove: true,
                AllowDelete: true,
                AllowedExecutables: [],
                AllowPowerShell: false,
                NetworkPolicy: new NetworkPolicy(DenyAll: false, AllowedHosts: []),
                ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
                AllowDelegation: false,
                DelegatedAgentIds: [],
                AllowedToolIds: [TestToolRegistry.ToolId],
                AllowedCallIds: null,
                MaximumRisk: ToolRiskLevel.Network,
                AllowedEnvironmentVariables: null,
                AgentId: null,
                ParentGrantId: null,
                RootGrantId: null,
                DelegationChain: null,
                ApprovalRequestId: null,
                AllowedSkillScriptKeys: null,
                SkillCatalogVersion: null);
            await authorization.SaveGrantAsync(grant, agentId: null, TestContext.CancellationToken);

            var executor = new TestToolExecutor();
            using var gateway = new ToolGateway(
                catalogStore,
                validator,
                authorization,
                [executor],
                toolState,
                outbox);
            var provider = new MultiRoundToolLoopProvider(totalToolRounds: 9);
            var orchestrator = new ExpertModeOrchestrator(
                provider,
                new ConversationStore(dataDirectory),
                repository,
                outbox,
                dataDirectory,
                toolGateway: gateway,
                toolAuthorizationService: authorization,
                toolCatalogSnapshot: catalog);

            var events = new List<RuntimeEventEnvelope>();
            await foreach (var envelope in orchestrator.RunAsync(
                runId,
                sessionId,
                CreateRequest(),
                "runtime-many-rounds",
                TestContext.CancellationToken))
            {
                events.Add(envelope);
            }

            Assert.AreEqual(10, provider.CallCount);
            Assert.AreEqual(9, executor.CallCount);
            Assert.AreEqual(RunStatus.Completed, await repository.GetRunStatusAsync(runId));
            Assert.IsTrue(events.Any(item => item.MessageType == MessageTypes.RunCompleted));
            Assert.IsFalse(events.Any(item => item.MessageType == MessageTypes.RunFailed));
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
    public async Task RunAsync_DelegateAgentTool_RoutesSubAgentAndReturnsResult()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"madorin-expert-delegate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            await using var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken);
            var repository = new SqliteSessionRepository(connection);
            var sessionId = await repository.CreateSessionAsync(
                RuntimeMode.Expert,
                "delegate-session",
                TimeSpan.FromDays(1),
                TestContext.CancellationToken);
            var runId = await repository.CreateRunAsync(
                sessionId,
                "delegate-run",
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
                [new BuiltinDelegateAgentToolExecutor()],
                toolState,
                outbox);
            var provider = new DelegateAgentToolLoopProvider();
            var orchestrator = new ExpertModeOrchestrator(
                provider,
                new ConversationStore(dataDirectory),
                repository,
                outbox,
                dataDirectory,
                toolGateway: gateway,
                toolAuthorizationService: authorization,
                toolCatalogSnapshot: catalog);

            var events = new List<RuntimeEventEnvelope>();
            await foreach (var envelope in orchestrator.RunAsync(
                runId,
                sessionId,
                CreateDelegateRequest(),
                "runtime-delegate",
                TestContext.CancellationToken))
            {
                events.Add(envelope);
            }

            Assert.AreEqual(3, provider.CallCount);
            Assert.AreEqual(1, provider.SubAgentCallCount);
            Assert.AreEqual(RunStatus.Completed, await repository.GetRunStatusAsync(runId));
            Assert.IsTrue(events.Any(static item =>
                item.MessageType == MessageTypes.ToolCallCompleted));
            Assert.IsTrue(events.Any(static item =>
                item.MessageType == MessageTypes.RunCompleted));
            Assert.IsNotNull(provider.MainAgentSecondRequest);
            var toolResult = provider.MainAgentSecondRequest!.Messages
                .Where(static message => message.Role == RuntimeProviderRoles.Tool)
                .SelectMany(static message => message.Content)
                .OfType<ToolResultContentBlock>()
                .Single(static item => item.CallId == "delegate-call-1");
            Assert.IsTrue(toolResult.Success);
            var resultText = Assert.IsInstanceOfType<TextContentBlock>(
                Assert.ContainsSingle(toolResult.Content)).Text;
            using var resultJson = JsonDocument.Parse(resultText);
            Assert.IsTrue(resultJson.RootElement.GetProperty("delegated").GetBoolean());
            Assert.IsTrue(resultJson.RootElement.GetProperty("success").GetBoolean());
            Assert.AreEqual(
                "developer",
                resultJson.RootElement.GetProperty("agentName").GetString());
            Assert.AreEqual(
                "101",
                resultJson.RootElement.GetProperty("result").GetString());
            StringAssert.Contains(
                resultJson.RootElement.GetProperty("message").GetString(),
                "completed the task");
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
    public async Task RunAsync_DelegateAgentTool_UnknownAgent_ReturnsFailureResult()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"madorin-expert-delegate-missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            await using var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken);
            var repository = new SqliteSessionRepository(connection);
            var sessionId = await repository.CreateSessionAsync(
                RuntimeMode.Expert,
                "delegate-missing-session",
                TimeSpan.FromDays(1),
                TestContext.CancellationToken);
            var runId = await repository.CreateRunAsync(
                sessionId,
                "delegate-missing-run",
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
                [new BuiltinDelegateAgentToolExecutor()],
                toolState,
                outbox);
            var provider = new DelegateAgentMissingTargetProvider();
            var orchestrator = new ExpertModeOrchestrator(
                provider,
                new ConversationStore(dataDirectory),
                repository,
                outbox,
                dataDirectory,
                toolGateway: gateway,
                toolAuthorizationService: authorization,
                toolCatalogSnapshot: catalog);

            await foreach (var _ in orchestrator.RunAsync(
                runId,
                sessionId,
                CreateDelegateRequest(),
                "runtime-delegate-missing",
                TestContext.CancellationToken))
            {
            }

            Assert.AreEqual(2, provider.CallCount);
            Assert.AreEqual(RunStatus.Completed, await repository.GetRunStatusAsync(runId));
            Assert.IsNotNull(provider.SecondRequest);
            var toolResult = provider.SecondRequest!.Messages
                .Where(static message => message.Role == RuntimeProviderRoles.Tool)
                .SelectMany(static message => message.Content)
                .OfType<ToolResultContentBlock>()
                .Single(static item => item.CallId == "delegate-call-missing");
            Assert.IsFalse(toolResult.Success);
            var resultText = Assert.IsInstanceOfType<TextContentBlock>(
                Assert.ContainsSingle(toolResult.Content)).Text;
            using var resultJson = JsonDocument.Parse(resultText);
            Assert.IsFalse(resultJson.RootElement.GetProperty("delegated").GetBoolean());
            Assert.IsFalse(resultJson.RootElement.GetProperty("success").GetBoolean());
            StringAssert.Contains(
                resultJson.RootElement.GetProperty("message").GetString(),
                "not found in AvailableAgents");
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    private static NewSessionRunRequest CreateRequest() =>
        new(
            "tool-session-key",
            "tool-run-key",
            RuntimeMode.Expert,
            new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection("tool-loop", "tool-model"),
                new ExpertModeOptions(
                    new AgentRef("agent-1", "v1", "Use tools when needed."))),
            [new TextContentBlock("Calculate the answer.")]);

    private static NewSessionRunRequest CreateDelegateRequest()
    {
        var primary = new AgentRef("expert", "v1", "You coordinate specialists.");
        var developer = new AgentRef("developer", "v1", "You only return numeric answers.");
        return new(
            "delegate-session-key",
            "delegate-run-key",
            RuntimeMode.Expert,
            new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection("tool-loop", "tool-model"),
                new ExpertModeOptions(primary, [primary, developer])),
            [new TextContentBlock("Delegate 1+100 to the developer.")]);
    }

    private static NewSessionRunRequest CreateMemoryRequest() =>
        new(
            "memory-tool-session-key",
            "memory-tool-run-key",
            RuntimeMode.Expert,
            new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection("memory-tool-loop", "memory-tool-model"),
                new ExpertModeOptions(
                    new AgentRef("memory-agent", "v1", "Use memory when needed."))),
            [new TextContentBlock("Read the project memory.")]);

    private sealed class ToolLoopProvider : IRuntimeProviderAdapter
    {
        public string ProviderId => "tool-loop";

        public ProviderCapabilities Capabilities { get; } = new(
            Streaming: true,
            ToolCalling: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("tool-model", "Tool Model", ContextWindow: 8192)];

        public int CallCount { get; private set; }

        public RuntimeProviderRequest? SecondRequest { get; private set; }

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            CallCount++;
            Console.WriteLine($"ToolLoopProvider.CompleteStreamingAsync called, CallCount={CallCount}");
            Console.WriteLine($"request.Tools count: {request.Tools?.Length ?? 0}");
            if (request.Tools != null)
            {
                foreach (var tool in request.Tools)
                {
                    Console.WriteLine($"  - {tool.ToolId}");
                }
            }
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (CallCount == 1)
            {
                Assert.IsNotNull(request.Tools, "request.Tools should not be null");
                Assert.IsTrue(request.Tools.Any(static tool =>
                    tool.ToolId == TestToolRegistry.ToolId),
                    $"request.Tools should contain {TestToolRegistry.ToolId}");
                yield return new ReasoningDeltaProviderEvent(
                    request.InvocationId,
                    "I should use the calculate tool.");
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
            var projectedRequest = ProviderRequestCompatibilityProjector.Project(request, this);
            Assert.IsFalse(projectedRequest.Messages
                .SelectMany(static message => message.Content)
                .OfType<ReasoningContentBlock>()
                .Any());
            var toolResult = (ToolResultContentBlock)request.Messages[^1].Content[0];
            Assert.AreEqual("call-1", toolResult.CallId);
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

        public string CatalogVersion => "test-v1";

        public IReadOnlyList<ToolDescriptor> GetTools() => [Descriptor];

        public IReadOnlyList<ToolDescriptor> GetTools(string[] toolIds) =>
            toolIds.Contains(ToolId, StringComparer.Ordinal) ? [Descriptor] : [];
    }

    private sealed class MultiRoundToolLoopProvider(int totalToolRounds) : IRuntimeProviderAdapter
    {
        public string ProviderId => "tool-loop";

        public ProviderCapabilities Capabilities { get; } = new(
            Streaming: true,
            ToolCalling: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("tool-model", "Tool Model", ContextWindow: 8192)];

        public int CallCount { get; private set; }

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            CallCount++;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();

            if (CallCount <= totalToolRounds)
            {
                yield return new ToolCallCompleteProviderEvent(
                    request.InvocationId,
                    $"call-{CallCount}",
                    TestToolRegistry.ToolId,
                    "calculate",
                    "{\"value\":21}");
                yield return new InvocationCompletedProviderEvent(
                    request.InvocationId,
                    "tool_calls");
                yield break;
            }

            yield return new TextDeltaProviderEvent(request.InvocationId, "Completed after many rounds.");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class MemoryToolLoopProvider(string expectedMemory)
        : IRuntimeProviderAdapter
    {
        public string ProviderId => "memory-tool-loop";

        public ProviderCapabilities Capabilities { get; } = new(
            Streaming: true,
            ToolCalling: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("memory-tool-model", "Memory Tool Model", ContextWindow: 8192)];

        public int CallCount { get; private set; }

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            CallCount++;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (CallCount == 1)
            {
                Assert.IsNotNull(request.Tools);
                Assert.IsTrue(request.Tools.Any(static tool =>
                    tool.ToolId == BuiltinToolRegistry.MemoryReadToolId));
                Assert.IsTrue(request.Tools.Any(static tool =>
                    tool.ToolId == BuiltinToolRegistry.MemoryWriteToolId));
                Assert.IsFalse(request.Tools.Any(static tool =>
                    tool.ToolId == BuiltinToolRegistry.MemoryAppendToolId));
                yield return new ToolCallCompleteProviderEvent(
                    request.InvocationId,
                    "memory-call-1",
                    BuiltinToolRegistry.MemoryReadToolId,
                    "read_memory",
                    "{\"scope\":\"project\"}");
                yield return new InvocationCompletedProviderEvent(
                    request.InvocationId,
                    "tool_calls");
                yield break;
            }

            var result = request.Messages
                .Where(static message => message.Role == RuntimeProviderRoles.Tool)
                .SelectMany(static message => message.Content)
                .OfType<ToolResultContentBlock>()
                .Single(static item => item.CallId == "memory-call-1");
            Assert.IsTrue(result.Success);
            Assert.AreEqual(BuiltinToolRegistry.MemoryReadToolId, result.ToolId);
            var resultText = Assert.IsInstanceOfType<TextContentBlock>(
                Assert.ContainsSingle(result.Content)).Text;
            using var resultJson = JsonDocument.Parse(resultText);
            var lines = resultJson.RootElement
                .GetProperty("lines")
                .EnumerateArray()
                .Select(static item => item.GetProperty("text").GetString())
                .ToArray();
            Assert.AreEqual(
                expectedMemory,
                string.Join("\n", lines) + "\n");
            Assert.IsFalse(string.IsNullOrWhiteSpace(
                resultJson.RootElement.GetProperty("hash").GetString()));

            yield return new TextDeltaProviderEvent(
                request.InvocationId,
                "Memory result applied.");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
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

    private sealed class DelegateAgentToolLoopProvider : IRuntimeProviderAdapter
    {
        public string ProviderId => "tool-loop";

        public ProviderCapabilities Capabilities { get; } = new(
            Streaming: true,
            ToolCalling: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("tool-model", "Tool Model", ContextWindow: 8192)];

        public int CallCount { get; private set; }

        public int SubAgentCallCount { get; private set; }

        public int MainAgentCallCount { get; private set; }

        public RuntimeProviderRequest? MainAgentSecondRequest { get; private set; }

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            CallCount++;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();

            var isSubAgent = string.Equals(request.AgentId, "developer", StringComparison.Ordinal);
            if (isSubAgent)
            {
                SubAgentCallCount++;
                yield return new TextDeltaProviderEvent(request.InvocationId, "101");
                yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
                yield break;
            }

            MainAgentCallCount++;
            if (MainAgentCallCount == 1)
            {
                Assert.IsNotNull(request.Tools);
                Assert.IsTrue(request.Tools.Any(static tool =>
                    tool.ToolId == BuiltinToolRegistry.DelegateToAgentToolId));
                yield return new ToolCallCompleteProviderEvent(
                    request.InvocationId,
                    "delegate-call-1",
                    BuiltinToolRegistry.DelegateToAgentToolId,
                    "delegate_to_agent",
                    """{"agentName":"developer","task":"计算 1+100，只返回数字"}""");
                yield return new InvocationCompletedProviderEvent(
                    request.InvocationId,
                    "tool_calls");
                yield break;
            }

            MainAgentSecondRequest = request;
            yield return new TextDeltaProviderEvent(
                request.InvocationId,
                "Delegated result received.");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DelegateAgentMissingTargetProvider : IRuntimeProviderAdapter
    {
        public string ProviderId => "tool-loop";

        public ProviderCapabilities Capabilities { get; } = new(
            Streaming: true,
            ToolCalling: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("tool-model", "Tool Model", ContextWindow: 8192)];

        public int CallCount { get; private set; }

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
                yield return new ToolCallCompleteProviderEvent(
                    request.InvocationId,
                    "delegate-call-missing",
                    BuiltinToolRegistry.DelegateToAgentToolId,
                    "delegate_to_agent",
                    """{"agentName":"missing-agent","task":"do something"}""");
                yield return new InvocationCompletedProviderEvent(
                    request.InvocationId,
                    "tool_calls");
                yield break;
            }

            SecondRequest = request;
            yield return new TextDeltaProviderEvent(
                request.InvocationId,
                "Delegation failed as expected.");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    // ============================================================
    // @ Syntax Delegation Tests
    // ============================================================

    [TestMethod]
    public async Task RunAsync_AtSyntaxDelegation_InvokesSubAgent()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"madorin-expert-atsyntax-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            await using var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken);
            var repository = new SqliteSessionRepository(connection);
            var sessionId = await repository.CreateSessionAsync(
                RuntimeMode.Expert,
                "atsyntax-session",
                TimeSpan.FromDays(1),
                TestContext.CancellationToken);
            var runId = await repository.CreateRunAsync(
                sessionId,
                "atsyntax-run",
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

            using var outbox = new SqliteEventOutbox(connection);
            using var toolState = new SqliteToolIntentRepository(connection);
            var registry = new TestToolRegistry();
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
                [new TestToolExecutor()],
                toolState,
                outbox);
            var provider = new AtSyntaxDelegationProvider();
            var orchestrator = new ExpertModeOrchestrator(
                provider,
                new ConversationStore(dataDirectory),
                repository,
                outbox,
                dataDirectory,
                toolGateway: gateway,
                toolAuthorizationService: authorization,
                toolCatalogSnapshot: catalog);

            var events = new List<RuntimeEventEnvelope>();
            await foreach (var envelope in orchestrator.RunAsync(
                runId,
                sessionId,
                CreateAtSyntaxDelegationRequest(),
                "runtime-atsyntax",
                TestContext.CancellationToken))
            {
                events.Add(envelope);
            }

            Assert.AreEqual(1, provider.SubAgentCallCount, "Sub-agent should be called once");
            Assert.AreEqual("developer", provider.InvokedAgentId);
            Assert.AreEqual("say hello", provider.InvokedTask);
            Assert.AreEqual(RunStatus.Completed, await repository.GetRunStatusAsync(runId));
            Assert.IsTrue(events.Any(item => item.MessageType == MessageTypes.RunCompleted));
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
    public async Task RunAsync_AtSyntaxDelegation_UnknownAgent_ThrowsException()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"madorin-expert-atsyntax-unknown-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            await using var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken);
            var repository = new SqliteSessionRepository(connection);
            var sessionId = await repository.CreateSessionAsync(
                RuntimeMode.Expert,
                "atsyntax-unknown-session",
                TimeSpan.FromDays(1),
                TestContext.CancellationToken);
            var runId = await repository.CreateRunAsync(
                sessionId,
                "atsyntax-unknown-run",
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

            using var outbox = new SqliteEventOutbox(connection);
            using var toolState = new SqliteToolIntentRepository(connection);
            var registry = new TestToolRegistry();
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
                [new TestToolExecutor()],
                toolState,
                outbox);
            var provider = new AtSyntaxDelegationProvider();
            var orchestrator = new ExpertModeOrchestrator(
                provider,
                new ConversationStore(dataDirectory),
                repository,
                outbox,
                dataDirectory,
                toolGateway: gateway,
                toolAuthorizationService: authorization,
                toolCatalogSnapshot: catalog);

            InvalidOperationException? exception = null;
            try
            {
                await foreach (var _ in orchestrator.RunAsync(
                    runId,
                    sessionId,
                    CreateAtSyntaxUnknownAgentRequest(),
                    "runtime-atsyntax-unknown",
                    TestContext.CancellationToken))
                {
                }
            }
            catch (InvalidOperationException ex)
            {
                exception = ex;
            }

            Assert.IsNotNull(exception, "Should throw InvalidOperationException");
            StringAssert.Contains(exception.Message, "unknown_agent");
            StringAssert.Contains(exception.Message, "not found");
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    // Helper methods for @ syntax tests
    private static NewSessionRunRequest CreateAtSyntaxDelegationRequest()
    {
        var primary = new AgentRef("expert", "v1", "You coordinate specialists.");
        var developer = new AgentRef("developer", "v1", "You are a developer assistant.");
        return new(
            "atsyntax-session-key",
            "atsyntax-run-key",
            RuntimeMode.Expert,
            new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection("tool-loop", "tool-model"),
                new ExpertModeOptions(primary, [primary, developer])),
            [new TextContentBlock("@developer say hello")]);
    }

    private static NewSessionRunRequest CreateAtSyntaxUnknownAgentRequest()
    {
        var primary = new AgentRef("expert", "v1", "You coordinate specialists.");
        var developer = new AgentRef("developer", "v1", "You are a developer assistant.");
        return new(
            "atsyntax-unknown-session-key",
            "atsyntax-unknown-run-key",
            RuntimeMode.Expert,
            new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection("tool-loop", "tool-model"),
                new ExpertModeOptions(primary, [primary, developer])),
            [new TextContentBlock("@unknown_agent do something")]);
    }

    private sealed class AtSyntaxDelegationProvider : IRuntimeProviderAdapter
    {
        public string ProviderId => "tool-loop";

        public ProviderCapabilities Capabilities { get; } = new(
            Streaming: true,
            ToolCalling: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("tool-model", "Tool Model", ContextWindow: 8192)];

        public int SubAgentCallCount { get; private set; }

        public string? InvokedAgentId { get; private set; }

        public string? InvokedTask { get; private set; }

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();

            // Detect sub-agent invocation by agent ID
            if (string.Equals(request.AgentId, "developer", StringComparison.Ordinal))
            {
                SubAgentCallCount++;
                InvokedAgentId = request.AgentId;

                // Extract task from system prompt injection
                var userMsg = request.Messages.LastOrDefault(m => m.Role == "user");
                if (userMsg != null)
                {
                    var textContent = userMsg.Content.OfType<TextContentBlock>().FirstOrDefault();
                    InvokedTask = textContent?.Text ?? string.Empty;
                }

                yield return new TextDeltaProviderEvent(request.InvocationId, "Hello from developer!");
                yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
                yield break;
            }

            // Main expert should not be called when delegation is active
            yield return new TextDeltaProviderEvent(request.InvocationId, "Unexpected call to expert.");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
