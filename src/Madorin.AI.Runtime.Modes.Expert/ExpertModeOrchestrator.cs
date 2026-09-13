using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Core;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Orchestration.Abstractions;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Services.Skills;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Modes.Expert;

public sealed class ExpertModeOrchestrator(
    IRuntimeProviderAdapter provider,
    ConversationStore store,
    SqliteSessionRepository sessionRepo,
    SqliteEventOutbox outbox,
    AgentContextComposer? agentContextComposer = null,
    Func<CredentialsRefreshRequestedEvent, CancellationToken, Task<bool>>?
        credentialRefreshHandler = null,
    ToolGateway? toolGateway = null,
    IToolAuthorizationService? toolAuthorizationService = null,
    ToolCatalogSnapshot? toolCatalogSnapshot = null,
    MemoryInvocationSnapshotStore? memorySnapshotStore = null,
    RuntimeLimits? runtimeLimits = null,
    Func<ToolPermissionRequest, CancellationToken, Task<ToolPermissionResponse>>?
        toolPermissionHandler = null,
    Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse>>?
        approvalHandler = null,
    IToolStateStore? toolStateStore = null,
    Func<bool>? isRunTimedOut = null,
    ToolConsentCoordinator? toolConsentCoordinator = null,
    Func<CancellationToken, Task<SkillCatalogSnapshot?>>? skillCatalogSnapshotProvider = null,
    Func<RuntimeSkillScriptInvocation, CancellationToken, Task<RuntimeSkillScriptResult>>? scriptInvoker = null,
    Func<ToolCatalogSnapshot>? liveCatalogProvider = null,
    bool isHostModeSkills = false,
    SkillActivationStore? skillActivationStore = null) : IModeOrchestrator
{
    private const string ProjectionVersion = "expert-full-tail-v1";
    private readonly SqliteEventOutbox _outbox = outbox
        ?? throw new ArgumentNullException(nameof(outbox));
    private readonly IRuntimeProviderAdapter _provider = provider
        ?? throw new ArgumentNullException(nameof(provider));
    private readonly SqliteSessionRepository _sessionRepo = sessionRepo
        ?? throw new ArgumentNullException(nameof(sessionRepo));
    private readonly ConversationStore _store = store
        ?? throw new ArgumentNullException(nameof(store));
    private readonly AgentContextComposer? _agentContextComposer = agentContextComposer;
    private readonly Func<CredentialsRefreshRequestedEvent, CancellationToken, Task<bool>>?
        _credentialRefreshHandler = credentialRefreshHandler;
    private readonly Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse>>?
        _approvalHandler = approvalHandler;
    private readonly MemoryInvocationSnapshotStore? _memorySnapshotStore = memorySnapshotStore;
    private readonly RuntimeLimits _runtimeLimits = runtimeLimits ?? new RuntimeLimits(
        MaxToolResultBytesPerInvocation: 4 * 1024 * 1024);
    private readonly ToolCatalogSnapshot? _toolCatalogSnapshot = toolCatalogSnapshot;
    private readonly ToolGateway? _toolGateway = toolGateway;
    private readonly IToolAuthorizationService? _toolAuthorizationService = toolAuthorizationService;
    private readonly IToolStateStore? _toolStateStore = toolStateStore;
    private readonly Func<ToolPermissionRequest, CancellationToken, Task<ToolPermissionResponse>>?
        _toolPermissionHandler = toolPermissionHandler;
    private readonly Func<bool>? _isRunTimedOut = isRunTimedOut;
    private readonly ToolConsentCoordinator? _toolConsentCoordinator = toolConsentCoordinator;
    private readonly Func<CancellationToken, Task<SkillCatalogSnapshot?>>? _skillCatalogSnapshotProvider =
        skillCatalogSnapshotProvider;
    private readonly Func<RuntimeSkillScriptInvocation, CancellationToken, Task<RuntimeSkillScriptResult>>?
        _scriptInvoker = scriptInvoker;
    private readonly Func<ToolCatalogSnapshot>? _liveCatalogProvider = liveCatalogProvider;
    private readonly bool _isHostModeSkills = isHostModeSkills;
    private readonly SkillActivationStore? _skillActivationStore = skillActivationStore;
    private string[]? _hostAllowlistSkillIds;
    private string? _hostSkillDiscoveryMode;
    private IReadOnlyDictionary<string, string>? _hostCatalogPathsByName;
    private IReadOnlyDictionary<string, string>? _hostCatalogDescriptionsByName;
    private IReadOnlyList<SkillScopeSourceRoot>? _hostScopeSourceRoots;

    public RuntimeMode Mode => RuntimeMode.Expert;

    public async IAsyncEnumerable<RuntimeEventEnvelope> RunAsync(
        string runId,
        string sessionId,
        NewSessionRunRequest request,
        string runtimeInstanceId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
        ValidateToolCallLimit(_runtimeLimits.MaxToolCallsPerRound);
        var expert = ValidateAndGetOptions(request);
        var providerId = expert.Agent.ProviderId ?? request.Selection.DefaultSelection.ProviderId;
        var modelId = expert.Agent.ModelId ?? request.Selection.DefaultSelection.ModelId;
        if (!string.Equals(_provider.ProviderId, providerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Provider '{providerId}' is not available through the selected adapter.");
        }

        var invocationId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;

        // Inject available sub-agents list into system prompt when provided
        var baseSystemPrompt = expert.Agent.SystemPrompt;
        var enhancedSystemPrompt = AppendAvailableSubAgentsList(
            baseSystemPrompt,
            expert.Agent,
            expert.AvailableAgents);

        var agentContext = _agentContextComposer is null
            ? AgentContextComposer.WithoutMemory(enhancedSystemPrompt)
            : await _agentContextComposer.ComposeAsync(enhancedSystemPrompt, ct)
                .ConfigureAwait(false);
        using var memorySnapshotLease = CaptureMemorySnapshot(invocationId, agentContext);
        var fullSkillCatalog = await CaptureSkillCatalogSnapshotAsync(ct).ConfigureAwait(false);
        var skillCatalogSnapshot = fullSkillCatalog?.FilterBySkillIds(expert.Agent.SkillIds);
        BindHostSkillsState(
            expert.Agent.SkillIds,
            skillCatalogSnapshot,
            expert.Agent.SkillDiscoveryMode);
        var catalogVersion = CaptureCatalogSnapshot()?.EffectiveVersion
            ?? request.Selection.ToolCatalogVersion;
        var snapshot = new InvocationSnapshot(
            invocationId,
            expert.Agent.AgentId,
            providerId,
            modelId,
            ComputeHash(expert.Agent.SystemPrompt),
            agentContext.GlobalMemoryHash,
            agentContext.ProjectMemoryHash,
            catalogVersion,
            ProjectionVersion,
            startedAt,
            SkillCatalogVersion: skillCatalogSnapshot?.Version ?? fullSkillCatalog?.Version,
            SkillNames: skillCatalogSnapshot?.Skills.Select(static skill => skill.Name).ToArray(),
            SkillContentHashes: skillCatalogSnapshot?.Skills.Select(static skill => skill.ContentHash).ToArray(),
            SkillSourceIds: skillCatalogSnapshot?.Skills.Select(static skill => skill.SourceId).ToArray(),
            SkillMaterializedPaths: skillCatalogSnapshot?.Skills.Select(static skill => skill.MaterializedPath).ToArray());
        if (_isHostModeSkills)
        {
            // Host mode: cut full-allowlist Advertise; Loadable starts empty (V1 InitialSkills=∅).
            snapshot = HostModeSkillsSnapshot.ProjectAdvertiseEmpty(
                snapshot,
                skillCatalogSnapshot ?? fullSkillCatalog);
        }

        // Check for @agentName delegation syntax in user input
        // Extract text content from ContentBlock[] for delegation parsing
        var userInputText = string.Concat(
            request.InitialInput.OfType<TextContentBlock>().Select(block => block.Text));
        var (isDelegation, delegatedAgentName, delegatedTask) = TryParseDelegationSyntax(userInputText);

        if (isDelegation && expert.AvailableAgents is { Length: > 0 })
        {
            // Find target sub-agent
            var targetAgent = Array.Find(expert.AvailableAgents, a =>
                string.Equals(a.AgentId, delegatedAgentName, StringComparison.OrdinalIgnoreCase));

            if (targetAgent is null)
            {
                var availableAgentIds = string.Join(", ", expert.AvailableAgents.Select(a => a.AgentId));
                throw new InvalidOperationException(
                    $"Sub-agent '@{delegatedAgentName}' not found in AvailableAgents. Available: [{availableAgentIds}]");
            }

            // Append original user input first
            await AppendContentAsync(
                sessionId,
                invocationId,
                expert.Agent.AgentId,
                "user",
                request.InitialInput,
                startedAt,
                ct).ConfigureAwait(false);

            // Invoke sub-agent with the delegated task
            long subAgentRunSequence = 1;
            var (subAgentContent, success) = await InvokeSubAgentAsync(
                delegatedAgentName!,
                targetAgent,
                delegatedTask!,
                runId,
                sessionId,
                invocationId,
                expert.Agent.AgentId,
                ct).ConfigureAwait(false);

            if (!success)
            {
                // Sub-agent invocation failed
                var errorContent = new ContentBlock[]
                {
                    new TextContentBlock($"Failed to invoke sub-agent '@{delegatedAgentName}'.")
                };
                await AppendContentAsync(
                    sessionId,
                    invocationId,
                    delegatedAgentName!,
                    "assistant",
                    errorContent,
                    DateTimeOffset.UtcNow,
                    ct).ConfigureAwait(false);

                var failurePayload = JsonSerializer.SerializeToElement(
                    new InvocationFailedEvent(
                        invocationId,
                        CreateRuntimeError("SubAgentFailed", $"Sub-agent '@{delegatedAgentName}' failed to complete the task.")),
                    RuntimeJsonContext.Default.InvocationFailedEvent);
                yield return await CreateEnvelopeAsync(
                    runtimeInstanceId,
                    runId,
                    subAgentRunSequence++,
                    MessageTypes.InvocationFailed,
                    failurePayload,
                    ct).ConfigureAwait(false);
                yield break;
            }

            // Sub-agent succeeded, append its output to conversation
            await AppendContentAsync(
                sessionId,
                invocationId,
                delegatedAgentName!,
                "assistant",
                subAgentContent,
                DateTimeOffset.UtcNow,
                ct).ConfigureAwait(false);

            // Emit completion event for sub-agent
            var subAgentCompletedPayload = JsonSerializer.SerializeToElement(
                new InvocationCompletedEvent(invocationId),
                RuntimeJsonContext.Default.InvocationCompletedEvent);
            yield return await CreateEnvelopeAsync(
                runtimeInstanceId,
                runId,
                subAgentRunSequence++,
                MessageTypes.InvocationCompleted,
                subAgentCompletedPayload,
                ct).ConfigureAwait(false);

            // Continue with main agent flow (main agent can see sub-agent's result)
            // Fall through to normal execution
        }
        else
        {
            // No delegation, append user input normally
            await AppendContentAsync(
                sessionId,
                invocationId,
                expert.Agent.AgentId,
                "user",
                request.InitialInput,
                startedAt,
                ct).ConfigureAwait(false);
        }
        var snapshotJson = JsonSerializer.Serialize(
            snapshot,
            RuntimeJsonContext.Default.InvocationSnapshot);
        await _sessionRepo.SaveInvocationSnapshotAsync(
            runId,
            sessionId,
            snapshot,
            snapshotJson,
            ct).ConfigureAwait(false);
        var history = await _store.ReadAllAsync(sessionId, ct).ConfigureAwait(false);
        var model = _provider.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, modelId, StringComparison.Ordinal));
        var tokenLimit = model?.ContextWindow is > 0
            ? (int)(model.ContextWindow.Value * 0.9)
            : int.MaxValue;
        var systemMessage = new RuntimeProviderMessage(
            RuntimeProviderRoles.System,
            [new TextContentBlock(agentContext.Instructions)]);
        var catalogSnapshot = CaptureCatalogSnapshot();
        ToolDescriptor[]? tools = _toolGateway is not null && catalogSnapshot is not null
            ? [.. catalogSnapshot.Tools]
            : null;
        ValueTask<ProviderTokenEstimate> EstimateProjectionAsync(
            RuntimeProviderMessage[] candidateMessages,
            CancellationToken token) =>
            _provider.EstimateTokensAsync(
                new RuntimeProviderRequest(
                    invocationId,
                    expert.Agent.AgentId,
                    providerId,
                    modelId,
                    [systemMessage, .. candidateMessages],
                    Tools: tools,
                    CancellationToken: token,
                    Skills: CreateSkillsContext(snapshot, runId, sessionId)),
                token);
        var projection = await ContextProjectionBuilder.BuildAsync(
                history,
                tokenLimit,
                EstimateProjectionAsync,
                ct)
            .ConfigureAwait(false);
        var messages = new RuntimeProviderMessage[projection.Messages.Length + 1];
        messages[0] = systemMessage;
        projection.Messages.CopyTo(messages, 1);
        var providerMessages = messages.ToList();
        var providerRequest = new RuntimeProviderRequest(
            invocationId,
            expert.Agent.AgentId,
            providerId,
            modelId,
            [.. providerMessages],
            Tools: tools,
            CancellationToken: ct,
            Skills: CreateSkillsContext(snapshot, runId, sessionId))
        {
            HistoryMessageCount = Math.Max(0, providerMessages.Count - 1)
        };

        long runSequence = 1;
        var startedPayload = JsonSerializer.SerializeToElement(
            new InvocationStartedEvent(invocationId, snapshot),
            RuntimeJsonContext.Default.InvocationStartedEvent);
        yield return await CreateEnvelopeAsync(
            runtimeInstanceId,
            runId,
            runSequence++,
            MessageTypes.InvocationStarted,
            startedPayload,
            ct).ConfigureAwait(false);
        if (projection.IsAdjusted)
        {
            var adjustedPayload = JsonSerializer.SerializeToElement(
                new ContextProjectionAdjustedEvent(
                    invocationId,
                    projection.DroppedMessageCount,
                    projection.Messages.Length,
                    projection.EstimatedTokens,
                    projection.EstimateSource,
                    projection.Strategy),
                RuntimeJsonContext.Default.ContextProjectionAdjustedEvent);
            yield return await CreateEnvelopeAsync(
                runtimeInstanceId,
                runId,
                runSequence++,
                MessageTypes.ContextProjectionAdjusted,
                adjustedPayload,
                ct).ConfigureAwait(false);
        }

        var assistantContent = new List<ContentBlock>();
        var terminalContent = new List<ContentBlock>();
        RuntimeError? failure = null;
        var cancelled = false;
        var maxToolRounds = _runtimeLimits.MaxToolRounds;
        var maxToolCallsPerRound = _runtimeLimits.MaxToolCallsPerRound;
        var maxToolResultBytes = _runtimeLimits.MaxToolResultBytesPerInvocation
            ?? 4L * 1024 * 1024;
        long totalToolResultBytes = 0;
        for (var toolRound = 0; ; toolRound++)
        {
            assistantContent = [];
            var completed = false;
            for (var providerAttempt = 0; ; providerAttempt++)
            {
                completed = false;
                failure = null;
                var hasObservedProviderOutput = false;
                var attemptRequest = providerRequest.CreateAttempt(providerAttempt);
                var providerEnumerator = ProviderInvocationBoundary.CompleteStreamingAsync(
                        _provider,
                        attemptRequest,
                        ct)
                    .GetAsyncEnumerator(ct);
                try
                {
                    while (true)
                    {
                        RuntimeProviderEvent? providerEvent = null;
                        try
                        {
                            if (!await providerEnumerator.MoveNextAsync().ConfigureAwait(false))
                            {
                                if (!completed && failure is null)
                                {
                                    failure = CreateRuntimeError(
                                        "ProviderStreamEnded",
                                        "The Provider stream ended without a terminal event.");
                                }

                                break;
                            }

                            providerEvent = providerEnumerator.Current;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            cancelled = true;
                            break;
                        }
                        catch (RuntimeProviderException ex)
                        {
                            failure = ex.Error;
                            break;
                        }
                        catch (Exception ex)
                        {
                            failure = CreateRuntimeError("ProviderExecutionFailed", ex.Message);
                            break;
                        }

                        switch (providerEvent)
                        {
                            case InvocationCompletedProviderEvent:
                                completed = true;
                                break;
                            case InvocationFailedProviderEvent failed:
                                failure = failed.Error;
                                break;
                            default:
                                hasObservedProviderOutput |= providerEvent is
                                    TextDeltaProviderEvent
                                    or ReasoningDeltaProviderEvent
                                    or ToolCallDeltaProviderEvent
                                    or ToolCallCompleteProviderEvent
                                    or UsageUpdatedProviderEvent;
                                AccumulateAssistantContent(assistantContent, providerEvent);
                                var mapped = MapProviderEvent(providerEvent);
                                var providerSequence = runSequence++;
                                if (IsDroppableDelta(mapped.MessageType))
                                {
                                    var envelope = await CreateDeltaEnvelopeAsync(
                                        runtimeInstanceId,
                                        runId,
                                        providerSequence,
                                        mapped.MessageType,
                                        mapped.Payload,
                                        ct).ConfigureAwait(false);
                                    if (envelope is not null)
                                    {
                                        yield return envelope;
                                    }
                                    else
                                    {
                                        var droppedPayload = JsonSerializer.SerializeToElement(
                                            new DeltaDroppedEvent(
                                                runId,
                                                providerSequence,
                                                providerSequence),
                                            RuntimeJsonContext.Default.DeltaDroppedEvent);
                                        yield return await CreateEnvelopeAsync(
                                            runtimeInstanceId,
                                            runId,
                                            runSequence++,
                                            MessageTypes.DeltaDropped,
                                            droppedPayload,
                                            ct).ConfigureAwait(false);
                                    }
                                }
                                else
                                {
                                    yield return await CreateEnvelopeAsync(
                                        runtimeInstanceId,
                                        runId,
                                        providerSequence,
                                        mapped.MessageType,
                                        mapped.Payload,
                                        ct).ConfigureAwait(false);
                                }

                                break;
                        }

                        if (completed || failure is not null)
                        {
                            break;
                        }
                    }
                }
                finally
                {
                    await providerEnumerator.DisposeAsync().ConfigureAwait(false);
                }

                if (completed || failure is null)
                {
                    break;
                }

                if (providerAttempt != 0
                    || !IsAuthenticationFailure(failure)
                    || !ProviderRetryPolicy.CanRetry(providerRequest, hasObservedProviderOutput)
                    || _credentialRefreshHandler is null)
                {
                    break;
                }

                await _sessionRepo.TransitionRunStatusAsync(
                    runId,
                    RunStatus.Running,
                    RunStatus.WaitingForCredentials,
                    ct).ConfigureAwait(false);
                yield return await CreateStatusEnvelopeAsync(
                    runtimeInstanceId,
                    runId,
                    runSequence++,
                    RunStatus.WaitingForCredentials,
                    ct).ConfigureAwait(false);

                var refreshRequest = new CredentialsRefreshRequestedEvent(
                    runId,
                    providerId,
                    "Unauthorized");
                var refreshed = await _credentialRefreshHandler(refreshRequest, ct)
                    .ConfigureAwait(false);
                if (!refreshed)
                {
                    failure = CreateRuntimeError(
                        "CredentialRefreshTimeout",
                        "Provider credentials were not refreshed before the waiting period expired.");
                    break;
                }

                await _sessionRepo.TransitionRunStatusAsync(
                    runId,
                    RunStatus.WaitingForCredentials,
                    RunStatus.Running,
                    ct).ConfigureAwait(false);
                yield return await CreateStatusEnvelopeAsync(
                    runtimeInstanceId,
                    runId,
                    runSequence++,
                    RunStatus.Running,
                    ct).ConfigureAwait(false);
            }

            terminalContent.AddRange(assistantContent);
            if (cancelled || failure is not null)
            {
                break;
            }

            var toolCalls = assistantContent.OfType<ToolCallContentBlock>().ToArray();
            if (toolCalls.Length == 0)
            {
                break;
            }

            if (maxToolRounds is { } toolRoundLimit && toolRound >= toolRoundLimit)
            {
                failure = CreateRuntimeError(
                    RuntimeErrorCodes.LimitsIncompatible,
                    $"The Invocation exceeded the {toolRoundLimit}-round tool limit.");
                break;
            }

            if (maxToolCallsPerRound is { } toolCallLimit
                && toolCalls.Length > toolCallLimit)
            {
                failure = CreateRuntimeError(
                    RuntimeErrorCodes.LimitsIncompatible,
                    $"The Provider requested {toolCalls.Length} tools in one round; the limit is {toolCallLimit}.");
                break;
            }

            if (toolCalls.Select(static call => call.CallId).Distinct(StringComparer.Ordinal).Count()
                != toolCalls.Length)
            {
                failure = CreateRuntimeError(
                    RuntimeErrorCodes.ToolCallConflict,
                    "The Provider returned duplicate tool call ids in one round.");
                break;
            }

            // 每轮工具调用前刷新 live catalog（search 命中注入后必须可见）
            catalogSnapshot = CaptureCatalogSnapshot();
            if (_toolGateway is null || catalogSnapshot is null)
            {
                failure = CreateRuntimeError(
                    RuntimeErrorCodes.ToolExecutorUnavailable,
                    "The selected Runtime does not have a tool gateway.");
                break;
            }

            await PersistPartialAssistantAsync(
                sessionId,
                invocationId,
                expert.Agent.AgentId,
                assistantContent).ConfigureAwait(false);
            providerMessages.Add(new RuntimeProviderMessage(
                RuntimeProviderRoles.Assistant,
                [.. assistantContent]));
            await _sessionRepo.TransitionRunStatusAsync(
                runId,
                RunStatus.Running,
                RunStatus.WaitingForTool,
                ct).ConfigureAwait(false);
            yield return await CreateStatusEnvelopeAsync(
                runtimeInstanceId,
                runId,
                runSequence++,
                RunStatus.WaitingForTool,
                ct).ConfigureAwait(false);

            foreach (var toolCall in toolCalls)
            {
                // 单次调用再采一次，覆盖 search 注入与并发 Patch
                var liveCatalog = CaptureCatalogSnapshot() ?? catalogSnapshot;
                var invocation = new ToolInvocation(
                    toolCall.CallId,
                    toolCall.ToolId,
                    expert.Agent.AgentId,
                    ParentAgentId: null,
                    toolCall.Arguments.GetRawText(),
                    runId,
                    sessionId,
                    invocationId,
                    ToolCatalogVersion: liveCatalog.EffectiveVersion);
                ToolGatewayResult gatewayResult;
                try
                {
                    gatewayResult = await _toolGateway.ExecuteAsync(
                        invocation,
                        permission: null,
                        liveCatalog,
                        ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                if (gatewayResult.Kind is ToolGatewayResultKind.NeedsPermission
                    or ToolGatewayResultKind.NeedsApproval)
                {
                    runSequence = await _outbox.GetNextRunSequenceAsync(runId, ct)
                        .ConfigureAwait(false);
                    await _sessionRepo.TransitionRunStatusAsync(
                        runId,
                        RunStatus.WaitingForTool,
                        RunStatus.WaitingForApproval,
                        ct).ConfigureAwait(false);
                    yield return await CreateStatusEnvelopeAsync(
                        runtimeInstanceId,
                        runId,
                        runSequence++,
                        RunStatus.WaitingForApproval,
                        ct).ConfigureAwait(false);
                    gatewayResult = await ResolveConsentAndExecuteAsync(
                        invocation,
                        gatewayResult,
                        CaptureCatalogSnapshot() ?? catalogSnapshot,
                        ct).ConfigureAwait(false);
                    runSequence = await _outbox.GetNextRunSequenceAsync(runId, ct)
                        .ConfigureAwait(false);
                    await _sessionRepo.TransitionRunStatusAsync(
                        runId,
                        RunStatus.WaitingForApproval,
                        RunStatus.WaitingForTool,
                        ct).ConfigureAwait(false);
                    yield return await CreateStatusEnvelopeAsync(
                        runtimeInstanceId,
                        runId,
                        runSequence++,
                        RunStatus.WaitingForTool,
                        ct).ConfigureAwait(false);
                }

                runSequence = await _outbox.GetNextRunSequenceAsync(runId, ct)
                    .ConfigureAwait(false);

                var toolResult = CreateToolResultContent(toolCall, gatewayResult);
                if (_isHostModeSkills
                    && gatewayResult.Kind is ToolGatewayResultKind.Success
                    && gatewayResult.Result is { Success: true, OutputJson: { } outputJson })
                {
                    HostModeSkillsSnapshot.TryApplyToolResult(
                        _skillActivationStore,
                        _hostAllowlistSkillIds,
                        toolCall.ToolId,
                        toolCall.Name,
                        outputJson,
                        _hostSkillDiscoveryMode);
                }

                var resultBytes = GetContentByteCount(toolResult);
                totalToolResultBytes += resultBytes;
                if (HasOversizedInlineContent(
                        toolResult,
                        _runtimeLimits.MaxInlineContentBytes)
                    || totalToolResultBytes > maxToolResultBytes)
                {
                    failure = CreateRuntimeError(
                        RuntimeErrorCodes.LimitsIncompatible,
                        "Tool results exceeded the configured inline or Invocation size limit.");
                    break;
                }

                await AppendContentAsync(
                    sessionId,
                    invocationId,
                    expert.Agent.AgentId,
                    RuntimeProviderRoles.Tool,
                    [toolResult],
                    DateTimeOffset.UtcNow,
                    ct).ConfigureAwait(false);
                providerMessages.Add(new RuntimeProviderMessage(
                    RuntimeProviderRoles.Tool,
                    [toolResult]));
            }

            if (cancelled || failure is not null)
            {
                break;
            }

            await _sessionRepo.TransitionRunStatusAsync(
                runId,
                RunStatus.WaitingForTool,
                RunStatus.Running,
                ct).ConfigureAwait(false);
            yield return await CreateStatusEnvelopeAsync(
                runtimeInstanceId,
                runId,
                runSequence++,
                RunStatus.Running,
                ct).ConfigureAwait(false);
            // search/browse 注入后，下一轮必须带上新工具短名；宿主 Skills Loadable 也需按激活表重建
            tools = CaptureAvailableTools();
            providerRequest = providerRequest with
            {
                Messages = [.. providerMessages],
                Tools = tools,
                Skills = CreateSkillsContext(snapshot, runId, sessionId),
                HistoryMessageCount = providerMessages.Count,
                InternalRequestId = null,
                AttemptNumber = 0,
                IsIdempotent = false,
                HasIrreversibleToolSideEffects = true
            };
        }

        if (cancelled || failure is not null)
        {
            await PersistPartialAssistantAsync(
                sessionId,
                invocationId,
                expert.Agent.AgentId,
                assistantContent).ConfigureAwait(false);
            if (cancelled)
            {
                var timedOut = _isRunTimedOut?.Invoke() == true;
                if (timedOut)
                {
                    failure = CreateRuntimeError(
                        RuntimeErrorCodes.RunTimedOut,
                        "The Run exceeded its configured timeout.");
                    var timeoutInvocationPayload = JsonSerializer.SerializeToElement(
                        new InvocationFailedEvent(invocationId, failure),
                        RuntimeJsonContext.Default.InvocationFailedEvent);
                    yield return await CreateEnvelopeAsync(
                        runtimeInstanceId,
                        runId,
                        runSequence++,
                        MessageTypes.InvocationFailed,
                        timeoutInvocationPayload,
                        CancellationToken.None).ConfigureAwait(false);
                    var timeoutRunFailedPayload = JsonSerializer.SerializeToElement(
                        new RunFailedEvent(runId, sessionId, failure),
                        RuntimeJsonContext.Default.RunFailedEvent);
                    var timeoutTerminal = await CommitTerminalEnvelopeAsync(
                        runtimeInstanceId,
                        runId,
                        timeoutRunFailedPayload,
                        RunStatus.Failed,
                        CreateTerminalTextSnapshot(terminalContent),
                        failure,
                        runSequence,
                        CancellationToken.None).ConfigureAwait(false);
                    if (timeoutTerminal is not null)
                    {
                        yield return timeoutTerminal;
                    }
                    yield break;
                }

                failure ??= CreateRuntimeError(
                    "RunCancelled",
                    "The Run was cancelled before the Provider stream completed.");
                var cancelledInvocationPayload = JsonSerializer.SerializeToElement(
                    new InvocationFailedEvent(invocationId, failure),
                    RuntimeJsonContext.Default.InvocationFailedEvent);
                yield return await CreateEnvelopeAsync(
                    runtimeInstanceId,
                    runId,
                    runSequence++,
                    MessageTypes.InvocationFailed,
                    cancelledInvocationPayload,
                    CancellationToken.None).ConfigureAwait(false);
                var runCancelledPayload = JsonSerializer.SerializeToElement(
                    new RunCancelledEvent(runId, sessionId),
                    RuntimeJsonContext.Default.RunCancelledEvent);
                var cancelledTerminal = await CommitTerminalEnvelopeAsync(
                    runtimeInstanceId,
                    runId,
                    runCancelledPayload,
                    RunStatus.Cancelled,
                    CreateTerminalTextSnapshot(terminalContent),
                    error: null,
                    runSequence: runSequence,
                    ct: CancellationToken.None).ConfigureAwait(false);
                if (cancelledTerminal is not null)
                {
                    yield return cancelledTerminal;
                }
                yield break;
            }

            var invocationFailedPayload = JsonSerializer.SerializeToElement(
                new InvocationFailedEvent(invocationId, failure!),
                RuntimeJsonContext.Default.InvocationFailedEvent);
            yield return await CreateEnvelopeAsync(
                runtimeInstanceId,
                runId,
                runSequence++,
                MessageTypes.InvocationFailed,
                invocationFailedPayload,
                CancellationToken.None).ConfigureAwait(false);
            var runFailedPayload = JsonSerializer.SerializeToElement(
                new RunFailedEvent(runId, sessionId, failure!),
                RuntimeJsonContext.Default.RunFailedEvent);
            var failedTerminal = await CommitTerminalEnvelopeAsync(
                runtimeInstanceId,
                runId,
                runFailedPayload,
                RunStatus.Failed,
                CreateTerminalTextSnapshot(terminalContent),
                failure,
                runSequence,
                CancellationToken.None).ConfigureAwait(false);
            if (failedTerminal is not null)
            {
                yield return failedTerminal;
            }
            yield break;
        }

        await _sessionRepo.TransitionRunStatusAsync(
            runId,
            RunStatus.Running,
            RunStatus.Persisting,
            CancellationToken.None).ConfigureAwait(false);
        await PersistPartialAssistantAsync(
            sessionId,
            invocationId,
            expert.Agent.AgentId,
            assistantContent).ConfigureAwait(false);
        var completedPayload = JsonSerializer.SerializeToElement(
            new InvocationCompletedEvent(invocationId),
            RuntimeJsonContext.Default.InvocationCompletedEvent);
        yield return await CreateEnvelopeAsync(
            runtimeInstanceId,
            runId,
            runSequence++,
            MessageTypes.InvocationCompleted,
            completedPayload,
            CancellationToken.None).ConfigureAwait(false);
        var runCompletedPayload = JsonSerializer.SerializeToElement(
            new RunCompletedEvent(runId, sessionId),
            RuntimeJsonContext.Default.RunCompletedEvent);
        var completedTerminal = await CommitTerminalEnvelopeAsync(
            runtimeInstanceId,
            runId,
            runCompletedPayload,
            RunStatus.Completed,
            CreateTerminalTextSnapshot(terminalContent),
            error: null,
            runSequence: runSequence,
            ct: CancellationToken.None).ConfigureAwait(false);
        if (completedTerminal is not null)
        {
            yield return completedTerminal;
        }
    }

    private async Task<ToolGatewayResult> ResolveConsentAndExecuteAsync(
        ToolInvocation invocation,
        ToolGatewayResult pendingResult,
        ToolCatalogSnapshot catalogSnapshot,
        CancellationToken ct)
    {
        if (_toolConsentCoordinator is not null)
        {
            return await _toolConsentCoordinator.ResolveAndExecuteAsync(
                invocation,
                pendingResult,
                ct).ConfigureAwait(false);
        }

        if (_toolGateway is null || _toolAuthorizationService is null)
        {
            return FailedToolGatewayResult(
                RuntimeErrorCodes.ToolExecutorUnavailable,
                "Tool consent cannot be resolved by this Runtime.");
        }

        var coordinator = new ToolConsentCoordinator(
            _toolGateway,
            _toolAuthorizationService,
            catalogSnapshot,
            _toolPermissionHandler,
            _approvalHandler,
            _toolStateStore,
            liveCatalogProvider: () => CaptureCatalogSnapshot() ?? catalogSnapshot);
        return await coordinator.ResolveAndExecuteAsync(
            invocation,
            pendingResult,
            ct).ConfigureAwait(false);
    }

    private MemorySnapshotLease? CaptureMemorySnapshot(
        string invocationId,
        AgentContextSnapshot context)
    {
        if (_memorySnapshotStore is null)
        {
            return null;
        }

        _memorySnapshotStore.Capture(invocationId, context);
        return new MemorySnapshotLease(_memorySnapshotStore, invocationId);
    }

    private static ToolResultContentBlock CreateToolResultContent(
        ToolCallContentBlock toolCall,
        ToolGatewayResult gatewayResult)
    {
        var success = gatewayResult.Kind is ToolGatewayResultKind.Success
            && gatewayResult.Result is { Success: true };
        ContentBlock[] content;
        if (success)
        {
            var blocks = new List<ContentBlock>(2);
            if (gatewayResult.Result!.OutputJson is { } outputJson)
            {
                blocks.Add(new TextContentBlock(outputJson));
            }

            if (gatewayResult.Result.ResultBlob is { } resultBlob)
            {
                blocks.Add(new BlobRefContentBlock(resultBlob));
            }

            content = [.. blocks];
        }
        else
        {
            content =
            [
                new TextContentBlock(
                    gatewayResult.Error?.Message
                    ?? gatewayResult.Result?.Error
                    ?? "Tool execution failed.")
            ];
        }

        return new ToolResultContentBlock(
            toolCall.CallId,
            toolCall.ToolId,
            success,
            content);
    }

    private static long GetContentByteCount(ToolResultContentBlock content) =>
        content.Content.Aggregate(
            0L,
            static (total, block) => checked(
                total + (block switch
                {
                    TextContentBlock text => Encoding.UTF8.GetByteCount(text.Text),
                    BlobRefContentBlock blob => blob.Blob.Length,
                    _ => 0
                })));

    private static bool HasOversizedInlineContent(
        ToolResultContentBlock content,
        int maxInlineContentBytes) =>
        content.Content
            .OfType<TextContentBlock>()
            .Any(text => Encoding.UTF8.GetByteCount(text.Text) > maxInlineContentBytes);

    private static ToolGatewayResult FailedToolGatewayResult(string code, string message) =>
        new(
            ToolGatewayResultKind.Failed,
            Error: new RuntimeError(
                code,
                "tool",
                message,
                IsRetryable: false,
                ProviderDetails: null,
                Guid.NewGuid().ToString("N")));

    private static ExpertModeOptions ValidateAndGetOptions(NewSessionRunRequest request)
    {
        if (request.Mode is not RuntimeMode.Expert
            || request.Selection.Mode is not RuntimeMode.Expert
            || request.Selection.ModeOptions is not ExpertModeOptions expert)
        {
            throw new InvalidOperationException("Expert mode requires one Expert Agent definition.");
        }

        if (request.InitialInput.Length == 0)
        {
            throw new ArgumentException("Expert mode requires initial input.", nameof(request));
        }

        return expert;
    }

    private async Task AppendContentAsync(
        string sessionId,
        string invocationId,
        string agentId,
        string role,
        ContentBlock[] content,
        DateTimeOffset timestamp,
        CancellationToken ct)
    {
        var serializedContent = JsonSerializer.SerializeToElement(
            content,
            RuntimeJsonContext.Default.ContentBlockArray);
        await _store.AppendMessageAsync(
            sessionId,
            Mode.ToString(),
            new ConversationMessageDraft(
                invocationId,
                agentId,
                role,
                serializedContent,
                timestamp),
            ct).ConfigureAwait(false);
    }

    private Task PersistPartialAssistantAsync(
        string sessionId,
        string invocationId,
        string agentId,
        List<ContentBlock> content)
    {
        return content.Count == 0
            ? Task.CompletedTask
            : AppendContentAsync(
                sessionId,
                invocationId,
                agentId,
                "assistant",
                content.ToArray(),
                DateTimeOffset.UtcNow,
                CancellationToken.None);
    }

    private static void AccumulateAssistantContent(
        List<ContentBlock> content,
        RuntimeProviderEvent providerEvent)
    {
        switch (providerEvent)
        {
            case TextDeltaProviderEvent text:
                if (content.LastOrDefault() is TextContentBlock previousText)
                {
                    content[^1] = previousText with { Text = previousText.Text + text.Delta };
                }
                else
                {
                    content.Add(new TextContentBlock(text.Delta));
                }

                break;
            case ReasoningDeltaProviderEvent reasoning:
                if (content.LastOrDefault() is ReasoningContentBlock previousReasoning)
                {
                    var extensions = MergeProviderExtensions(
                        previousReasoning.ProviderExtensions,
                        reasoning.ProviderExtensions);
                    content[^1] = previousReasoning with
                    {
                        Content = previousReasoning.Content + reasoning.Delta,
                        ProviderExtensions = extensions
                    };
                }
                else
                {
                    content.Add(new ReasoningContentBlock(
                        reasoning.Delta,
                        reasoning.ProviderExtensions));
                }

                break;
            case ToolCallCompleteProviderEvent toolCall:
                using (var arguments = JsonDocument.Parse(toolCall.ArgumentsJson))
                {
                    content.Add(new ToolCallContentBlock(
                        toolCall.CallId,
                        toolCall.ToolId,
                        toolCall.Name,
                        arguments.RootElement.Clone()));
                }

                break;
        }
    }

    private static ProviderExtensionData[]? MergeProviderExtensions(
        ProviderExtensionData[]? existing,
        ProviderExtensionData[]? additions)
    {
        if (additions is null or { Length: 0 })
        {
            return existing;
        }

        if (existing is null or { Length: 0 })
        {
            ProviderExtensionData.ValidateCollection(additions);
            return additions;
        }

        ProviderExtensionData[] merged = [.. existing, .. additions];
        ProviderExtensionData.ValidateCollection(merged);
        return merged;
    }

    private static (string MessageType, JsonElement Payload) MapProviderEvent(
        RuntimeProviderEvent providerEvent)
    {
        if (providerEvent is TextDeltaProviderEvent text)
        {
            return (
                MessageTypes.TextDelta,
                JsonSerializer.SerializeToElement(
                    new TextDeltaEvent(text.InvocationId, text.Delta),
                    RuntimeJsonContext.Default.TextDeltaEvent));
        }

        var messageType = providerEvent switch
        {
            ReasoningDeltaProviderEvent => MessageTypes.ReasoningDelta,
            ToolCallDeltaProviderEvent => MessageTypes.ToolCallDelta,
            ToolCallCompleteProviderEvent => MessageTypes.ToolCallCompleted,
            UsageUpdatedProviderEvent => MessageTypes.UsageUpdated,
            ProjectionAdjustedProviderEvent => MessageTypes.ProviderProjectionAdjusted,
            _ => throw new InvalidOperationException(
                $"Provider event '{providerEvent.GetType().Name}' cannot be streamed.")
        };
        return (
            messageType,
            JsonSerializer.SerializeToElement(
                providerEvent,
                RuntimeProviderJsonContext.Default.RuntimeProviderEvent));
    }

    private async Task<RuntimeEventEnvelope> CreateEnvelopeAsync(
        string runtimeInstanceId,
        string runId,
        long runSequence,
        string messageType,
        JsonElement payload,
        CancellationToken ct)
    {
        var gsn = await _outbox.AppendAsync(
            runId,
            runSequence,
            messageType,
            payload.GetRawText(),
            ct).ConfigureAwait(false);
        return new RuntimeEventEnvelope(
            runtimeInstanceId,
            gsn,
            runId,
            runSequence,
            messageType,
            DateTimeOffset.UtcNow,
            payload);
    }

    private async Task<RuntimeEventEnvelope?> CreateDeltaEnvelopeAsync(
        string runtimeInstanceId,
        string runId,
        long runSequence,
        string messageType,
        JsonElement payload,
        CancellationToken ct)
    {
        var gsn = await _outbox.AppendDeltaAsync(
            runId,
            runSequence,
            messageType,
            payload.GetRawText(),
            ct).ConfigureAwait(false);
        return gsn is null
            ? null
            : new RuntimeEventEnvelope(
                runtimeInstanceId,
                gsn.Value,
                runId,
                runSequence,
                messageType,
                DateTimeOffset.UtcNow,
                payload);
    }

    private Task<RuntimeEventEnvelope> CreateStatusEnvelopeAsync(
        string runtimeInstanceId,
        string runId,
        long runSequence,
        RunStatus status,
        CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToElement(
            new RunStatusChangedEvent(runId, status.ToString())
            {
                Mode = RuntimeMode.Expert,
                UpdatedAt = DateTimeOffset.UtcNow,
                Phase = status switch
                {
                    RunStatus.Accepted or RunStatus.Preparing => "preparing",
                    RunStatus.Running => "running",
                    RunStatus.WaitingForTool => "waiting-for-tool",
                    RunStatus.WaitingForApproval => "waiting-for-approval",
                    RunStatus.WaitingForCredentials => "waiting-for-credentials",
                    RunStatus.Persisting => "persisting",
                    _ => "terminal"
                }
            },
            RuntimeJsonContext.Default.RunStatusChangedEvent);
        return CreateEnvelopeAsync(
            runtimeInstanceId,
            runId,
            runSequence,
            MessageTypes.RunStatusChanged,
            payload,
            ct);
    }

    private async Task<RuntimeEventEnvelope?> CommitTerminalEnvelopeAsync(
        string runtimeInstanceId,
        string runId,
        JsonElement payload,
        RunStatus terminalStatus,
        string? terminalText,
        RuntimeError? error,
        long runSequence,
        CancellationToken ct)
    {
        var messageType = terminalStatus switch
        {
            RunStatus.Completed => MessageTypes.RunCompleted,
            RunStatus.Cancelled => MessageTypes.RunCancelled,
            RunStatus.Failed => MessageTypes.RunFailed,
            _ => throw new ArgumentOutOfRangeException(nameof(terminalStatus))
        };
        var gsn = await _sessionRepo.TryCommitTerminalRunAsync(
            runId,
            terminalStatus,
            terminalText,
            error is null
                ? null
                : JsonSerializer.Serialize(error, RuntimeJsonContext.Default.RuntimeError),
            messageType,
            payload.GetRawText(),
            ct).ConfigureAwait(false);
        if (gsn is null)
        {
            return null;
        }

        return new RuntimeEventEnvelope(
            runtimeInstanceId,
            gsn.Value,
            runId,
            runSequence,
            messageType,
            DateTimeOffset.UtcNow,
            payload);
    }

    private static string? CreateTerminalTextSnapshot(IReadOnlyList<ContentBlock> content)
    {
        var text = string.Concat(
            content.OfType<TextContentBlock>().Select(static block => block.Text));
        return text.Length == 0 ? null : text;
    }

    private static bool IsAuthenticationFailure(RuntimeError error) =>
        string.Equals(error.Code, RuntimeErrorCodes.AuthenticationFailed, StringComparison.Ordinal)
        || string.Equals(error.Category, "authentication", StringComparison.Ordinal);

    private static bool IsDroppableDelta(string messageType) =>
        string.Equals(messageType, MessageTypes.TextDelta, StringComparison.Ordinal)
        || string.Equals(messageType, MessageTypes.ReasoningDelta, StringComparison.Ordinal);

    private static RuntimeError CreateRuntimeError(string code, string message) =>
        new(
            code,
            "provider",
            message,
            IsRetryable: false,
            ProviderDetails: null,
            Guid.NewGuid().ToString("N"));

    private static void ValidateToolCallLimit(int? maxToolCallsPerRound)
    {
        if (maxToolCallsPerRound is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxToolCallsPerRound),
                "MaxToolCallsPerRound must be a positive integer or null.");
        }
    }

    private SkillsInvocationContext? CreateSkillsContext(
        InvocationSnapshot snapshot,
        string runId,
        string sessionId)
    {
        if (string.IsNullOrWhiteSpace(snapshot.SkillCatalogVersion))
        {
            return null;
        }

        if (_isHostModeSkills)
        {
            var hostContext = new SkillsInvocationContext(
                snapshot.SkillCatalogVersion,
                snapshot.SkillNames ?? [],
                snapshot.SkillContentHashes ?? [],
                snapshot.SkillSourceIds ?? [],
                snapshot.SkillMaterializedPaths,
                _scriptInvoker,
                runId,
                sessionId,
                snapshot.AgentId,
                snapshot.InvocationId,
                ParentAgentId: null,
                ParentInvocationId: snapshot.ParentInvocationId,
                WorkStepId: snapshot.WorkStepId,
                PlanVersion: snapshot.WorkPlanVersion,
                IsHostMode: true,
                ActivationStore: _skillActivationStore,
                AllowlistSkillIds: _hostAllowlistSkillIds,
                CatalogMaterializedPathsByName: _hostCatalogPathsByName,
                CatalogDescriptionsByName: _hostCatalogDescriptionsByName,
                ScopeSourceRoots: _hostScopeSourceRoots,
                SkillDiscoveryMode: _hostSkillDiscoveryMode);
            var loadablePaths = MafSkillsAgentFactory.ResolveHostLoadableMaterializedPaths(hostContext);
            return hostContext with { SkillMaterializedPaths = loadablePaths };
        }

        return new SkillsInvocationContext(
            snapshot.SkillCatalogVersion,
            snapshot.SkillNames ?? [],
            snapshot.SkillContentHashes ?? [],
            snapshot.SkillSourceIds ?? [],
            snapshot.SkillMaterializedPaths,
            _scriptInvoker,
            runId,
            sessionId,
            snapshot.AgentId,
            snapshot.InvocationId,
            ParentAgentId: null,
            ParentInvocationId: snapshot.ParentInvocationId,
            WorkStepId: snapshot.WorkStepId,
            PlanVersion: snapshot.WorkPlanVersion);
    }

    private void BindHostSkillsState(
        string[]? allowlistSkillIds,
        SkillCatalogSnapshot? allowlistedCatalog,
        string? skillDiscoveryMode)
    {
        if (!_isHostModeSkills)
        {
            _hostAllowlistSkillIds = null;
            _hostSkillDiscoveryMode = null;
            _hostCatalogPathsByName = null;
            _hostCatalogDescriptionsByName = null;
            _hostScopeSourceRoots = null;
            return;
        }

        _hostAllowlistSkillIds = allowlistSkillIds;
        _hostSkillDiscoveryMode = SkillDiscoveryModes.Normalize(skillDiscoveryMode);
        var maps = HostModeSkillsSnapshot.BuildMaps(allowlistedCatalog);
        _hostCatalogPathsByName = maps.PathsByName;
        _hostCatalogDescriptionsByName = maps.DescriptionsByName;
        _hostScopeSourceRoots = maps.ScopeSourceRoots;
    }

    private Task<SkillCatalogSnapshot?> CaptureSkillCatalogSnapshotAsync(CancellationToken ct) =>
        _skillCatalogSnapshotProvider?.Invoke(ct)
        ?? Task.FromResult<SkillCatalogSnapshot?>(null);

    private static string ComputeHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private ToolCatalogSnapshot? CaptureCatalogSnapshot()
    {
        if (_liveCatalogProvider is not null)
        {
            return _liveCatalogProvider();
        }

        return _toolCatalogSnapshot;
    }

    private ToolDescriptor[]? CaptureAvailableTools()
    {
        if (_toolGateway is null)
        {
            return null;
        }

        var snapshot = CaptureCatalogSnapshot();
        return snapshot is null ? null : [.. snapshot.Tools];
    }

    /// <summary>
    /// Parses user input to detect @agentName delegation syntax.
    /// Example: "@developer read file.txt" -> (true, "developer", "read file.txt")
    /// </summary>
    private static (bool isDelegation, string? agentName, string? task) TryParseDelegationSyntax(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return (false, null, null);
        }

        var trimmedInput = input.Trim();
        if (!trimmedInput.StartsWith('@'))
        {
            return (false, null, null);
        }

        // Match pattern: @agentName <rest of input>
        // AgentName can contain letters, digits, underscores, hyphens
        var match = System.Text.RegularExpressions.Regex.Match(
            trimmedInput,
            @"^@([\w\-]+)\s+(.+)$",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        if (match.Success)
        {
            var agentName = match.Groups[1].Value;
            var task = match.Groups[2].Value;
            return (true, agentName, task);
        }

        return (false, null, null);
    }

    /// <summary>
    /// Appends available sub-agents list to the system prompt when AvailableAgents is provided.
    /// Similar to Work mode's AppendAvailableAgentsList and Meeting mode's AppendAvailableParticipantsList.
    /// </summary>
    private static string AppendAvailableSubAgentsList(
        string baseInstructions,
        AgentRef primaryAgent,
        AgentRef[]? availableAgents)
    {
        if (availableAgents is not { Length: > 1 })
        {
            return baseInstructions;
        }

        var agentListBuilder = new StringBuilder();
        agentListBuilder.AppendLine();
        agentListBuilder.AppendLine();
        agentListBuilder.AppendLine("## Sub-Agent Delegation in Expert Mode");
        agentListBuilder.AppendLine();
        agentListBuilder.AppendLine("When you need to delegate a task to a specialized agent, use the following syntax:");
        agentListBuilder.AppendLine();
        agentListBuilder.AppendLine("```");
        agentListBuilder.AppendLine("@agentName <task description>");
        agentListBuilder.AppendLine("```");
        agentListBuilder.AppendLine();
        agentListBuilder.AppendLine("Available sub-agents:");
        agentListBuilder.AppendLine();

        foreach (var agent in availableAgents)
        {
            // Exclude the primary agent itself
            if (!string.Equals(agent.AgentId, primaryAgent.AgentId, StringComparison.Ordinal))
            {
                var description = agent.SystemPrompt?.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? string.Empty;
                agentListBuilder.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                    $"- **@{agent.AgentId}** - {description}");
            }
        }

        agentListBuilder.AppendLine();
        agentListBuilder.AppendLine("The orchestrator will automatically invoke the specified sub-agent and include its result in the conversation.");

        return baseInstructions + agentListBuilder.ToString();
    }

    /// <summary>
    /// Invokes a sub-agent with the delegated task, supporting full tool calling capabilities.
    /// </summary>
    private async Task<(ContentBlock[] content, bool success)> InvokeSubAgentAsync(
        string subAgentName,
        AgentRef subAgent,
        string task,
        string runId,
        string sessionId,
        string parentInvocationId,
        string parentAgentId,
        CancellationToken ct)
    {
        var subInvocationId = Guid.NewGuid().ToString("N");
        var subAgentProviderId = subAgent.ProviderId ?? _provider.ProviderId;
        var subAgentModelId = subAgent.ModelId
            ?? (_provider.Models.Count > 0 ? _provider.Models[0].Id : "default");

        // Compose sub-agent's system prompt with memory if available
        var subAgentContext = _agentContextComposer is null
            ? AgentContextComposer.WithoutMemory(subAgent.SystemPrompt)
            : await _agentContextComposer.ComposeAsync(subAgent.SystemPrompt, ct).ConfigureAwait(false);

        // Create initial messages for sub-agent: system + user task
        var systemMessage = new RuntimeProviderMessage(
            RuntimeProviderRoles.System,
            [new TextContentBlock(subAgentContext.Instructions)]);

        var userTaskMessage = new RuntimeProviderMessage(
            RuntimeProviderRoles.User,
            [new TextContentBlock(task)]);

        var subAgentMessages = new List<RuntimeProviderMessage> { systemMessage, userTaskMessage };

        // Capture tool catalog for sub-agent
        var catalogSnapshot = CaptureCatalogSnapshot();
        ToolDescriptor[]? tools = _toolGateway is not null && catalogSnapshot is not null
            ? [.. catalogSnapshot.Tools]
            : null;

        var assistantContent = new List<ContentBlock>();
        var maxToolRounds = _runtimeLimits.MaxToolRounds ?? 10;
        var maxToolCallsPerRound = _runtimeLimits.MaxToolCallsPerRound ?? 64;

        // Sub-agent tool calling loop
        for (var toolRound = 0; toolRound < maxToolRounds; toolRound++)
        {
            assistantContent.Clear();

            // Create provider request for sub-agent
            var subAgentRequest = new RuntimeProviderRequest(
                subInvocationId,
                subAgentName,
                subAgentProviderId,
                subAgentModelId,
                [.. subAgentMessages],
                Tools: tools,
                CancellationToken: ct,
                Skills: null);

            // Call provider
            var completed = false;
            RuntimeError? failure = null;

            await foreach (var providerEvent in ProviderInvocationBoundary.CompleteStreamingAsync(
                _provider,
                subAgentRequest,
                ct).ConfigureAwait(false))
            {
                switch (providerEvent)
                {
                    case InvocationCompletedProviderEvent:
                        completed = true;
                        break;
                    case InvocationFailedProviderEvent failed:
                        failure = failed.Error;
                        break;
                    default:
                        AccumulateAssistantContent(assistantContent, providerEvent);
                        break;
                }

                if (completed || failure is not null)
                {
                    break;
                }
            }

            if (failure is not null)
            {
                return ([], false);
            }

            // Check for tool calls
            var toolCalls = assistantContent.OfType<ToolCallContentBlock>().ToArray();
            if (toolCalls.Length == 0)
            {
                return (assistantContent.ToArray(), true);
            }

            if (toolCalls.Length > maxToolCallsPerRound)
            {
                return ([], false);
            }

            // Add assistant message to conversation
            subAgentMessages.Add(new RuntimeProviderMessage(
                RuntimeProviderRoles.Assistant,
                [.. assistantContent]));

            // Execute tool calls with ParentAgentId
            foreach (var toolCall in toolCalls)
            {
                var liveCatalog = CaptureCatalogSnapshot() ?? catalogSnapshot;
                if (liveCatalog is null || _toolGateway is null)
                {
                    return ([], false);
                }

                var invocation = new ToolInvocation(
                    toolCall.CallId,
                    toolCall.ToolId,
                    subAgentName,
                    parentAgentId,
                    toolCall.Arguments.GetRawText(),
                    runId,
                    sessionId,
                    subInvocationId,
                    ToolCatalogVersion: liveCatalog.EffectiveVersion);

                ToolGatewayResult gatewayResult;
                try
                {
                    gatewayResult = await _toolGateway.ExecuteAsync(
                        invocation,
                        permission: null,
                        liveCatalog,
                        ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return ([], false);
                }

                // Handle permission/approval if needed
                if (gatewayResult.Kind is ToolGatewayResultKind.NeedsPermission
                    or ToolGatewayResultKind.NeedsApproval)
                {
                    if (_toolConsentCoordinator is not null)
                    {
                        gatewayResult = await _toolConsentCoordinator.ResolveAndExecuteAsync(
                            invocation,
                            gatewayResult,
                            ct).ConfigureAwait(false);
                    }
                    else
                    {
                        return ([], false);
                    }
                }

                var toolResult = CreateToolResultContent(toolCall, gatewayResult);
                subAgentMessages.Add(new RuntimeProviderMessage(
                    RuntimeProviderRoles.Tool,
                    [toolResult]));
            }
        }

        // Exceeded max tool rounds
        return ([], false);
    }

    private sealed class MemorySnapshotLease(
        MemoryInvocationSnapshotStore store,
        string invocationId) : IDisposable
    {
        private MemoryInvocationSnapshotStore? _store = store;

        public void Dispose()
        {
            Interlocked.Exchange(ref _store, null)?.Release(invocationId);
        }
    }
}
