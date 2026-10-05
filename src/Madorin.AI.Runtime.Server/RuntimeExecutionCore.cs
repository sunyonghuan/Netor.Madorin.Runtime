using System.Runtime.CompilerServices;
using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Modes.Expert;
using Madorin.AI.Runtime.Orchestration.Abstractions;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services;
using Madorin.AI.Runtime.Services.AgentJobs;
using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Services.Skills;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;
using Madorin.AI.Runtime.Tools.Builtin.ModeShared;
using System.Security.Cryptography;
using Madorin.AI.Runtime.Transport.Abstractions;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Server;

internal sealed class RuntimeExecutionCore(
    string dataDirectory,
    string databaseConnectionString,
    string runtimeInstanceId,
    string workspaceRoot,
    AgentContextComposer agentContextComposer,
    MemoryFileService memoryFiles,
    RuntimeLimits runtimeLimits,
    IToolResultBlobStore resultBlobStore)
{
    private readonly AgentContextComposer _agentContextComposer = agentContextComposer
        ?? throw new ArgumentNullException(nameof(agentContextComposer));
    private readonly string _dataDirectory = Path.GetFullPath(dataDirectory);
    private readonly string _databaseConnectionString = databaseConnectionString;
    private readonly MemoryFileService _memoryFiles = memoryFiles
        ?? throw new ArgumentNullException(nameof(memoryFiles));
    private readonly MemoryInvocationSnapshotStore _memorySnapshots = new();
    private readonly RuntimeLimits _runtimeLimits = runtimeLimits
        ?? throw new ArgumentNullException(nameof(runtimeLimits));
    private readonly IToolResultBlobStore _resultBlobStore = resultBlobStore
        ?? throw new ArgumentNullException(nameof(resultBlobStore));
    private readonly string _runtimeInstanceId = runtimeInstanceId;
    private readonly string _workspaceRoot = Path.GetFullPath(workspaceRoot);

    public async IAsyncEnumerable<RuntimeEventEnvelope> RunAsync(
        string runId,
        string sessionId,
        NewSessionRunRequest request,
        IRuntimeProviderAdapter provider,
        IToolCatalogStore toolCatalogStore,
        ToolCatalogSnapshot toolCatalogSnapshot,
        IDuplexRpcPeer? controlPeer = null,
        Func<CredentialsRefreshRequestedEvent, CancellationToken, Task<bool>>?
            credentialRefreshHandler = null,
        Func<bool>? isRunTimedOut = null,
        Func<string, IRuntimeProviderAdapter>? providerFactory = null,
        IToolConsentHandler? localToolConsentHandler = null,
        SkillCatalogService? skillCatalog = null,
        bool isHostModeSkills = false,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(toolCatalogStore);
        ArgumentNullException.ThrowIfNull(toolCatalogSnapshot);
        if (controlPeer is not null && localToolConsentHandler is not null)
        {
            throw new ArgumentException(
                "A Runtime execution cannot use both a control peer and a local tool consent handler.",
                nameof(localToolConsentHandler));
        }

        // Host-mode progressive discovery: one Run-scoped activation table shared by all agents.
        SkillActivationStore? skillActivationStore = isHostModeSkills
            ? new SkillActivationStore()
            : null;

        var gatewayCatalogSnapshot = skillCatalog is null
            ? toolCatalogSnapshot
            : toolCatalogSnapshot.WithInternalTools(SkillRuntimeTools.GatewayDescriptors);
        var skillCatalogSnapshots = new ConcurrentDictionary<string, SkillCatalogSnapshot>(
            StringComparer.Ordinal);
        SkillCatalogSnapshot? latestSkillSnapshot = null;
        Func<CancellationToken, Task<SkillCatalogSnapshot?>>? skillCatalogSnapshotProvider =
            skillCatalog is null
                ? null
                : async token =>
                {
                    var snapshot = await skillCatalog.GetSnapshotForInvocationAsync(token)
                        .ConfigureAwait(false);
                    latestSkillSnapshot = snapshot;
                    skillCatalogSnapshots.TryAdd(snapshot.Version, snapshot);
                    return snapshot;
                };

        await using var repositoryConnection = await OpenDatabaseConnectionAsync(ct)
            .ConfigureAwait(false);
        var repository = new SqliteSessionRepository(repositoryConnection);
        await using var outboxConnection = await OpenDatabaseConnectionAsync(ct)
            .ConfigureAwait(false);
        using var outbox = new SqliteEventOutbox(
            outboxConnection,
            Path.Combine(_dataDirectory, "replay"),
            SqliteEventOutbox.DefaultMaxReplayBytes);
        await using var toolConnection = await OpenDatabaseConnectionAsync(ct)
            .ConfigureAwait(false);
        using var toolStateStore = new SqliteToolIntentRepository(toolConnection);
        var scopeResolver = new BuiltinToolConsentScopeResolver();
        var authorizationService = new ToolAuthorizationService(
            toolStateStore,
            RuntimeToolPolicy.CreateRestricted(_workspaceRoot),
            TimeProvider.System,
            scopeResolver);
        var reverseExecutor = controlPeer is null
            ? null
            : new ReverseRpcToolExecutor(controlPeer, _resultBlobStore);
        var consentHandler = localToolConsentHandler
            ?? (reverseExecutor is null
                ? null
                : new ReverseRpcToolConsentHandler(
                    reverseExecutor.RequestPermissionAsync,
                    reverseExecutor.RequestApprovalAsync));
        // 并行子 Agent 作业池（无定时）：Run 作用域，Expert 绑定 Worker 后可 create/wait/result 回收。
        await using var agentJobCoordinator = new AgentJobCoordinator();
        var conversationStore = new ConversationStore(_dataDirectory);
        Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse>>? askUserApproval = null;
        var executors = new List<IToolExecutor>
        {
            new BuiltinFsToolExecutor(_memoryFiles.WorkspaceMutationLock),
            new BuiltinProcessToolExecutor(),
            new BuiltinHttpToolExecutor(),
            new BuiltinMemoryToolExecutor(_memoryFiles),
            new BuiltinDelegateAgentToolExecutor(),
            new BuiltinAgentJobToolExecutor(agentJobCoordinator),
        };
        if (skillCatalog is not null)
        {
            executors.Add(new AgentFileSkillScriptToolExecutor(
                _workspaceRoot,
                runId,
                version => skillCatalogSnapshots.TryGetValue(version, out var snapshot)
                    ? snapshot
                    : null));
            executors.Add(new SkillRuntimeToolExecutor(
                _workspaceRoot,
                runId,
                () => latestSkillSnapshot ?? skillCatalog.Current,
                CollectAgents(request.Selection.ModeOptions),
                skillActivationStore,
                isHostModeSkills));
        }
        if (reverseExecutor is not null)
        {
            executors.Add(reverseExecutor);
        }

        using var toolGateway = new ToolGateway(
            toolCatalogStore,
            new JsonSchemaToolValidator(),
            authorizationService,
            executors,
            toolStateStore,
            outbox,
            resultBlobStore: _resultBlobStore,
            runtimeLimits: _runtimeLimits);
        var recoveryService = new RecoveryService(
            repository,
            toolStateStore,
            toolGateway,
            gatewayCatalogSnapshot,
            workRecoveryStore: null);
        await recoveryService.RecoverSentToolIntentsAsync(ct).ConfigureAwait(false);
        // Live catalog must stay aligned with search/browse inject; consent re-entry
        // must not freeze the Run-start snapshot or CallId identity will conflict.
        Func<ToolCatalogSnapshot> liveCatalogProvider = () => skillCatalog is null
            ? toolCatalogStore.CaptureSnapshot()
            : toolCatalogStore.CaptureSnapshot()
                .WithInternalTools(SkillRuntimeTools.GatewayDescriptors);

        // 人审只回答是否同意。可执行文件用本次 scope 补全；
        // 运行期文件系统根在定稿时扩回工作区，避免收成单个文件。
        var toolConsentCoordinator = new ToolConsentCoordinator(
            toolGateway,
            authorizationService,
            gatewayCatalogSnapshot,
            consentHandler,
            scopeResolver,
            _workspaceRoot,
            toolStateStore,
            liveCatalogProvider);
        askUserApproval = toolConsentCoordinator.CanRequestApproval
            ? toolConsentCoordinator.RequestWorkflowApprovalAsync
            : null;

        Func<RuntimeSkillScriptInvocation, CancellationToken, Task<RuntimeSkillScriptResult>>? scriptInvoker =
            skillCatalog is null
                ? null
                : (script, token) => RunSkillScriptThroughGatewayAsync(
                    script,
                    skillCatalogSnapshots,
                    gatewayCatalogSnapshot,
                    toolGateway,
                    toolConsentCoordinator,
                    token);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Accepted,
            RunStatus.Preparing,
            ct).ConfigureAwait(false);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Preparing,
            RunStatus.Running,
            ct).ConfigureAwait(false);

        Func<string, IRuntimeProviderAdapter> expertProviderResolver = requestedProviderId =>
        {
            if (providerFactory is not null)
            {
                return providerFactory(requestedProviderId);
            }

            if (string.Equals(requestedProviderId, provider.ProviderId, StringComparison.Ordinal))
            {
                return provider;
            }

            throw new InvalidOperationException(
                $"Provider {requestedProviderId} is not available in the current Runtime execution.");
        };
        ExpertModeOrchestrator orchestrator = request.Mode switch
        {
            RuntimeMode.Expert => new ExpertModeOrchestrator(
                provider,
                new ConversationStore(_dataDirectory),
                repository,
                outbox,
                _workspaceRoot,
                _agentContextComposer,
                credentialRefreshHandler,
                toolGateway,
                authorizationService,
                gatewayCatalogSnapshot,
                _memorySnapshots,
                _runtimeLimits,
                reverseExecutor is null ? null : reverseExecutor.RequestPermissionAsync,
                reverseExecutor is null ? null : reverseExecutor.RequestApprovalAsync,
                toolStateStore,
                isRunTimedOut,
                toolConsentCoordinator,
                skillCatalogSnapshotProvider,
                scriptInvoker,
                liveCatalogProvider: liveCatalogProvider,
                isHostModeSkills: isHostModeSkills,
                skillActivationStore: skillActivationStore,
                agentJobCoordinator: agentJobCoordinator,
                providerResolver: expertProviderResolver),

            _ => throw new NotSupportedException(
                $"Runtime mode '{request.Mode}' is not supported.")
        };

        await foreach (var envelope in orchestrator.RunAsync(
                runId,
                sessionId,
                request,
                _runtimeInstanceId,
                ct)
            .ConfigureAwait(false))
        {
            yield return envelope;
        }
    }

    private async Task<SqliteConnection> OpenDatabaseConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_databaseConnectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await SqliteSchema.EnsureCreatedAsync(connection, ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<RuntimeSkillScriptResult> RunSkillScriptThroughGatewayAsync(
        RuntimeSkillScriptInvocation request,
        ConcurrentDictionary<string, SkillCatalogSnapshot> skillCatalogSnapshots,
        ToolCatalogSnapshot gatewayCatalogSnapshot,
        ToolGateway toolGateway,
        ToolConsentCoordinator toolConsentCoordinator,
        CancellationToken ct)
    {
        if (!skillCatalogSnapshots.TryGetValue(request.CatalogVersion, out var skillCatalogSnapshot))
        {
            return new RuntimeSkillScriptResult(
                false,
                "{}",
                "The Skill catalog was not captured by this Invocation.");
        }

        if (!skillCatalogSnapshot.TryGetSkill(request.SkillName, out var skill) || skill is null)
        {
            return new RuntimeSkillScriptResult(false, "{}", $"Skill '{request.SkillName}' was not found.");
        }

        var relativeScript = request.Script.Replace('/', Path.DirectorySeparatorChar);
        var scriptPath = Path.GetFullPath(Path.Combine(skill.MaterializedPath, relativeScript));
        var materializedRoot = Path.GetFullPath(skill.MaterializedPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!scriptPath.StartsWith(materializedRoot, StringComparisonForPath())
            || !skill.Scripts.Contains(request.Script, StringComparer.OrdinalIgnoreCase)
            || !File.Exists(scriptPath))
        {
            return new RuntimeSkillScriptResult(false, "{}", "The requested script is outside the fixed Skill snapshot.");
        }

        string interpreter;
        try
        {
            interpreter = await AgentFileSkillScriptRunner.ResolveInterpreterNameAsync(
                Path.GetExtension(scriptPath),
                ct).ConfigureAwait(false);
        }
        catch (SkillScriptException ex)
        {
            return new RuntimeSkillScriptResult(false, "{}", ex.Message);
        }

        var scriptHash = SkillScriptAuthorization.ComputeScriptHash(scriptPath);
        var argumentsJson = CreateSkillScriptArgumentsJson(request, scriptHash, interpreter);
        var invocation = new ToolInvocation(
            Guid.NewGuid().ToString("N"),
            SkillScriptAuthorization.ToolId,
            request.AgentId,
            request.ParentAgentId,
            argumentsJson,
            request.RunId,
            request.SessionId,
            request.InvocationId,
            ToolCatalogVersion: gatewayCatalogSnapshot.EffectiveVersion,
            ParentInvocationId: request.ParentInvocationId,
            WorkStepId: request.WorkStepId,
            PlanVersion: request.PlanVersion);

        var result = await toolGateway.ExecuteAsync(
            invocation,
            permission: null,
            gatewayCatalogSnapshot,
            ct).ConfigureAwait(false);
        if (result.Kind is ToolGatewayResultKind.NeedsPermission or ToolGatewayResultKind.NeedsApproval)
        {
            result = await toolConsentCoordinator.ResolveAndExecuteAsync(invocation, result, ct)
                .ConfigureAwait(false);
        }

        if (result.Result is { } toolResult)
        {
            return toolResult.Success
                ? new RuntimeSkillScriptResult(true, toolResult.OutputJson ?? "{}")
                : new RuntimeSkillScriptResult(false, toolResult.OutputJson ?? "{}", toolResult.Error);
        }

        return new RuntimeSkillScriptResult(
            false,
            "{}",
            result.Error?.Message ?? "Skill script execution did not produce a result.");

        static StringComparison StringComparisonForPath() =>
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
    }

    private static string CreateSkillScriptArgumentsJson(
        RuntimeSkillScriptInvocation request,
        string scriptHash,
        string interpreter)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }))
        {
            writer.WriteStartObject();
            writer.WriteString("skillName", request.SkillName);
            writer.WriteString("script", request.Script);
            writer.WriteString("scriptHash", scriptHash);
            writer.WriteString("interpreter", interpreter);
            writer.WriteString("catalogVersion", request.CatalogVersion);
            writer.WriteStartArray("arguments");
            foreach (var argument in request.Arguments)
            {
                writer.WriteStringValue(argument);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static Dictionary<string, AgentRef> CollectAgents(ModeOptions options)
    {
        var agents = new Dictionary<string, AgentRef>(StringComparer.Ordinal);
        void Add(AgentRef? agent)
        {
            if (agent is not null)
            {
                agents[agent.AgentId] = agent;
            }
        }

        switch (options)
        {
            case ExpertModeOptions expert:
                Add(expert.Agent);
                foreach (var agent in expert.AvailableAgents ?? [])
                {
                    Add(agent);
                }

                break;
        }

        return agents;
    }

    private static string ExtractTranscriptText(JsonElement content)
    {
        try
        {
            var blocks = content.Deserialize(RuntimeJsonContext.Default.ContentBlockArray);
            if (blocks is null || blocks.Length == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var block in blocks)
            {
                if (block is TextContentBlock text && !string.IsNullOrWhiteSpace(text.Text))
                {
                    if (builder.Length > 0)
                    {
                        builder.AppendLine();
                    }

                    builder.Append(text.Text.Trim());
                }
            }

            return builder.ToString();
        }
        catch (JsonException)
        {
            return content.ValueKind == JsonValueKind.String
                ? content.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }
    }
}
