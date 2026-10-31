using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Expert;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Core;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Orchestration.Abstractions;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services.AgentJobs;
using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Services.Prompt;
using Madorin.AI.Runtime.Services.Skills;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Modes.Expert;

public sealed class ExpertModeOrchestrator(
    IRuntimeProviderAdapter provider,
    ConversationStore store,
    SqliteSessionRepository sessionRepo,
    SqliteEventOutbox outbox,
    string workspaceRoot,
    AgentContextComposer? agentContextComposer = null,
    Func<CredentialsRefreshRequestedEvent, CancellationToken, Task<bool>>?
        credentialRefreshHandler = null,
    ToolGateway? toolGateway = null,
    IToolAuthorizationService? toolAuthorizationService = null,
    ToolCatalogSnapshot? toolCatalogSnapshot = null,
    MemoryInvocationSnapshotStore? memorySnapshotStore = null,
    RuntimeLimits? runtimeLimits = null,
    IToolResultBlobStore? toolResultBlobStore = null,
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
    SkillActivationStore? skillActivationStore = null,
    IAgentJobCoordinator? agentJobCoordinator = null,
    Func<string, IRuntimeProviderAdapter>? providerResolver = null) : IModeOrchestrator
{
    private const string ProjectionVersion = "expert-full-tail-v1";
    /// <summary>与 BuiltinToolRegistry.DelegateToAgentToolId 保持一致，避免 Modes.Expert 反向依赖 Tools.Builtin。</summary>
    private const string DelegateToAgentToolId = "mcli_delegate_agent";
    /// <summary>与 BuiltinToolRegistry.ExpertCreatePlanToolId 保持一致。</summary>
    private const string ExpertCreatePlanToolId = "mcli_expert_create_plan";
    /// <summary>与 BuiltinToolRegistry.ExpertExecuteStepToolId 保持一致。</summary>
    private const string ExpertExecuteStepToolId = "mcli_expert_execute_step";
    /// <summary>与 BuiltinToolRegistry.ExpertGetPlanToolId 保持一致。</summary>
    private const string ExpertGetPlanToolId = "mcli_expert_get_plan";
    /// <summary>与 BuiltinToolRegistry.ExpertUpdateStepStatusToolId 保持一致。</summary>
    private const string ExpertUpdateStepStatusToolId = "mcli_expert_update_step_status";
    /// <summary>与 BuiltinToolRegistry.ExpertAbandonPlanToolId 保持一致。</summary>
    private const string ExpertAbandonPlanToolId = "mcli_expert_abandon_plan";
    private readonly SqliteEventOutbox _outbox = outbox
        ?? throw new ArgumentNullException(nameof(outbox));
    private readonly IRuntimeProviderAdapter _provider = provider
        ?? throw new ArgumentNullException(nameof(provider));
    private readonly SqliteSessionRepository _sessionRepo = sessionRepo
        ?? throw new ArgumentNullException(nameof(sessionRepo));
    private readonly ConversationStore _store = store
        ?? throw new ArgumentNullException(nameof(store));
    private readonly string _workspaceRoot = workspaceRoot
        ?? throw new ArgumentNullException(nameof(workspaceRoot));
    private readonly AgentContextComposer? _agentContextComposer = agentContextComposer;
    private readonly Func<CredentialsRefreshRequestedEvent, CancellationToken, Task<bool>>?
        _credentialRefreshHandler = credentialRefreshHandler;
    private readonly Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse>>?
        _approvalHandler = approvalHandler;
    private readonly MemoryInvocationSnapshotStore? _memorySnapshotStore = memorySnapshotStore;
    private readonly RuntimeLimits _runtimeLimits = runtimeLimits ?? new RuntimeLimits(
        MaxToolResultBytesPerInvocation: 4 * 1024 * 1024);
    private readonly IToolResultBlobStore? _toolResultBlobStore = toolResultBlobStore;
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
    private readonly IAgentJobCoordinator? _agentJobCoordinator = agentJobCoordinator;
    private readonly Func<string, IRuntimeProviderAdapter> _providerResolver = providerResolver ?? (requestedProviderId =>
    {
        if (string.Equals(requestedProviderId, provider.ProviderId, StringComparison.Ordinal))
        {
            return provider;
        }

        throw new InvalidOperationException(
            $"Provider {requestedProviderId} is not available through the selected adapter.");
    });
    private string[]? _hostAllowlistSkillIds;
    private string? _hostSkillDiscoveryMode;
    private IReadOnlyDictionary<string, string>? _hostCatalogPathsByName;
    private IReadOnlyDictionary<string, string>? _hostCatalogDescriptionsByName;
    private IReadOnlyList<SkillScopeSourceRoot>? _hostScopeSourceRoots;
    private ExpertPlanManager? _planManager;
    private string? _conversationLanguage;
    private readonly List<ExpertToolEvidence> _primaryToolEvidence = [];
    private readonly object _primaryEvidenceGate = new();

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

        // Validate RequestControl parameters early to fail fast with clear errors
        expert.RequestControl?.Validate();

        _conversationLanguage = request.Language;
        var providerId = expert.Agent.ProviderId ?? request.Selection.DefaultSelection.ProviderId;
        var modelId = expert.Agent.ModelId ?? request.Selection.DefaultSelection.ModelId;
        if (!string.Equals(_provider.ProviderId, providerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Provider '{providerId}' is not available through the selected adapter.");
        }

        // 并行作业池：绑定本 Run 的子 Agent 执行器，供 mcli_agent_job_* 工具 create/wait/result 回收。
        _agentJobCoordinator?.BindWorkerWithRequest(
            (jobId, request, jobRunId, jobSessionId, parentAgentId, jobCt) =>
                ExecuteAgentJobAsync(
                    expert,
                    jobId,
                    request,
                    jobRunId,
                    jobSessionId,
                    parentAgentId,
                    jobCt));

        var invocationId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;

        // Initialize plan manager for this session
        _planManager = new ExpertPlanManager(_workspaceRoot, _outbox);
        var restoredPlan = await _planManager.RestoreActivePlanAsync(sessionId, ct)
            .ConfigureAwait(false);

        // Inject available sub-agents list and plan progress into system prompt
        var baseSystemPrompt = expert.Agent.SystemPrompt;
        var withSubAgents = AppendAvailableSubAgentsList(
            baseSystemPrompt,
            expert.Agent,
            expert.AvailableAgents);
        var enhancedSystemPrompt = PrepareSystemPromptWithPlan(withSubAgents);

        var agentContext = _agentContextComposer is null
            ? AgentContextComposer.WithoutMemory(enhancedSystemPrompt)
            : await _agentContextComposer.ComposeAsync(enhancedSystemPrompt, ct)
                .ConfigureAwait(false);
        agentContext = agentContext with
        {
            Instructions = ConversationEnvironmentPrompt.Append(
                agentContext.Instructions,
                _workspaceRoot,
                request.Language)
        };
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
            CompactionCacheKeyFactory.ComputeAgentPromptHash(expert.Agent.SystemPrompt),
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
        var preTurnRecordCount = (await _store.ReadAllAsync(sessionId, ct).ConfigureAwait(false)).Count;

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
            var (subAgentContent, success, _) = await InvokeSubAgentAsync(
                targetAgent.AgentId,
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
                    targetAgent.AgentId,
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
                targetAgent.AgentId,
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

        var source = await _store.ReadCompactionSourceAsync(sessionId, ct).ConfigureAwait(false);
        var history = source.Records;
        var contextPolicy = expert.ContextPolicy ?? new ExpertContextPolicy();
        var model = _provider.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, modelId, StringComparison.Ordinal)
            || string.Equals(candidate.ProviderModelId, modelId, StringComparison.Ordinal));
        if (model?.ContextWindow is not > 0 && _provider is IRuntimeProviderModelCatalogSource catalogSource)
        {
            model = (await catalogSource.FetchModelsAsync(ct).ConfigureAwait(false))
                .FirstOrDefault(candidate => candidate.Id == modelId || candidate.ProviderModelId == modelId) ?? model;
        }
        var budget = ContextTokenBudget.Resolve(model, contextPolicy.MaxTokens, expert.RequestControl?.MaxTokens);
        var tokenLimit = budget.InputLimit;
        var useSummaryStrategy = IsSummaryStrategy(contextPolicy.Strategy);
        var compactionProviderId = string.IsNullOrWhiteSpace(contextPolicy.SummaryProviderId)
            ? providerId
            : contextPolicy.SummaryProviderId;
        var compactionModelId = string.IsNullOrWhiteSpace(contextPolicy.SummaryModelId)
            ? modelId
            : contextPolicy.SummaryModelId;
        var compactionProvider = _provider;
        Exception? compactionProviderError = null;
        if (useSummaryStrategy)
        {
            try
            {
                compactionProvider = _providerResolver(compactionProviderId);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                compactionProviderError = ex;
            }
        }
        var systemMessage = new RuntimeProviderMessage(
            RuntimeProviderRoles.System,
            [new TextContentBlock(agentContext.Instructions)]);
        var catalogSnapshot = CaptureCatalogSnapshot()?.FilterByMode("expert");
        ToolDescriptor[]? tools = _toolGateway is not null && catalogSnapshot is not null
            ? [.. catalogSnapshot.Tools]
            : null;
        var usedFallbackEstimate = false;
        async ValueTask<ProviderTokenEstimate> EstimateProjectionAsync(
            RuntimeProviderMessage[] candidateMessages,
            CancellationToken token)
        {
            var estimateRequest = new RuntimeProviderRequest(
                    invocationId,
                    expert.Agent.AgentId,
                    providerId,
                    modelId,
                    [systemMessage, .. candidateMessages],
                    Tools: tools,
                    CancellationToken: token,
                    Skills: CreateSkillsContext(snapshot, runId, sessionId));
            var estimate = await _provider.EstimateTokensAsync(estimateRequest, token).ConfigureAwait(false);
            usedFallbackEstimate = estimate.InputTokens is null;
            return estimate.InputTokens is null ? ProviderTokenEstimator.Estimate(estimateRequest) : estimate;
        }
        var contextPolicyHash = CompactionCacheKeyFactory.ComputeContextPolicyHash(contextPolicy);
        var agentPromptHash = CompactionCacheKeyFactory.ComputeAgentPromptHash(expert.Agent.SystemPrompt);
        var cachedProjection = SummaryProjectionResolver.Resolve(source,
            await _store.ReadCompactCacheAsync(sessionId, ct).ConfigureAwait(false),
            agentPromptHash, request.Selection.ToolCatalogVersion);
        var projectionCacheHit = cachedProjection.IsApplied;
        var projectionCacheMissReason = cachedProjection.CacheMissReason;
        ContextProjectionBuilder.Result? projection = null;
        if (cachedProjection.IsApplied)
        {
            // 重新应用 TailMessageCount，只保留摘要 + 最近的消息
            var rawTailSize = contextPolicy.TailMessageCount ?? 8;
            var uncoveredRecords = history.Where(r => r.Sequence > cachedProjection.Cache!.CoveredThroughSequence).ToArray();

            // 从未覆盖的历史中，保留最近的 rawTailSize 条（保持工具调用的原子性）
            RuntimeProviderMessage[] tailMessages;
            if (rawTailSize == 0 || uncoveredRecords.Length <= rawTailSize)
            {
                tailMessages = uncoveredRecords.Select(SummaryProjectionResolver.ToMessage).ToArray();
            }
            else
            {
                tailMessages = ContextProjectionBuilder.BuildTailWindow(uncoveredRecords, rawTailSize);
            }

            // 构建最终消息：[摘要] + [保留的尾部]
            var finalMessages = new RuntimeProviderMessage[1 + tailMessages.Length];
            finalMessages[0] = cachedProjection.Messages[0];  // 摘要消息
            Array.Copy(tailMessages, 0, finalMessages, 1, tailMessages.Length);

            var estimate = await EstimateProjectionAsync(finalMessages, ct).ConfigureAwait(false);
            var droppedCount = history.Count(record => record.Sequence <= cachedProjection.Cache!.CoveredThroughSequence)
                + Math.Max(0, uncoveredRecords.Length - tailMessages.Length);
            projection = new ContextProjectionBuilder.Result(
                finalMessages,
                "Summary",
                droppedCount,
                estimate.InputTokens ?? ProviderTokenEstimator.Estimate(new RuntimeProviderRequest(
                    invocationId, expert.Agent.AgentId, providerId, modelId,
                    [systemMessage, .. finalMessages], Tools: tools)).InputTokens ?? 0,
                usedFallbackEstimate ? "runtime.utf8-bytes/4" : $"provider.{estimate.Accuracy.ToString().ToLowerInvariant()}");
        }

        projection ??= await ContextProjectionBuilder.BuildAsync(
                history,
                tokenLimit,
                EstimateProjectionAsync,
                ct)
            .ConfigureAwait(false);
        if (usedFallbackEstimate)
        {
            projection = projection with { EstimateSource = "runtime.utf8-bytes/4" };
        }

        // 自动压缩触发条件：
        // 1. 有缓存且仍超标 → 需要重新压缩
        // 2. 无缓存 + 策略是 Summary + (历史被调整 OR 历史消息数超过阈值)
        var historyMessageCount = history.Count - preTurnRecordCount;
        var compactionThreshold = (contextPolicy.TailMessageCount ?? 8) * 2;
        var shouldCompact = (projectionCacheHit && projection.EstimatedTokens > tokenLimit)
            || (!projectionCacheHit && useSummaryStrategy && (projection.IsAdjusted || historyMessageCount >= compactionThreshold));

        if (shouldCompact)
        {
            // 仅在确需压缩时校验摘要模型，避免无效摘要选型阻断普通对话。
            if (!useSummaryStrategy && !string.Equals(compactionProviderId, providerId, StringComparison.Ordinal))
            {
                compactionProvider = _providerResolver(compactionProviderId);
            }
            if (compactionProviderError is not null)
            {
                throw new InvalidOperationException(
                    $"Summary Provider '{compactionProviderId}' is unavailable.",
                    compactionProviderError);
            }
            ValidateCompactionModel(compactionProvider, compactionModelId);
            var prefixCount = SummaryProjectionResolver.GetCompressiblePrefixCount(source,
                Math.Max(history.Count - preTurnRecordCount, Math.Max(1, contextPolicy.TailMessageCount ?? 8)));
            if (prefixCount == 0 || (cachedProjection.Cache is { } existing
                && history[prefixCount - 1].Sequence == existing.CoveredThroughSequence))
            {
                throw new InvalidDataException("context_budget_exhausted: No additional historical prefix can be compacted safely.");
            }
            var summaryInput = SummaryProjectionResolver.CreateSummaryInput(source, cachedProjection, prefixCount);
            var summaryMessage = await StrictConversationSummarizer.GenerateAsync(
                    compactionProvider!,
                    compactionProviderId,
                    compactionModelId,
                    summaryInput,
                    ct: ct)
                .ConfigureAwait(false);
            RuntimeProviderMessage[] candidateMessages = [summaryMessage,
                .. history.Skip(prefixCount).Select(SummaryProjectionResolver.ToMessage)];
            var after = await EstimateProjectionAsync(candidateMessages, ct).ConfigureAwait(false);
            var afterEstimateSource = usedFallbackEstimate ? "runtime.utf8-bytes/4"
                : $"provider.{after.Accuracy.ToString().ToLowerInvariant()}";
            var before = await EstimateProjectionAsync(cachedProjection.Messages, ct).ConfigureAwait(false);
            if (after.InputTokens is not { } afterTokens || afterTokens > tokenLimit
                || before.InputTokens is not { } beforeTokens || afterTokens >= beforeTokens)
            {
                throw new InvalidDataException("summary_not_reduced: The summary did not reduce input enough to fit the active context budget.");
            }
            var summaryProjection = new ContextProjectionBuilder.Result(candidateMessages, "Summary", prefixCount,
                afterTokens, afterEstimateSource);
            var historyEstimateRequest = new RuntimeProviderRequest(invocationId, expert.Agent.AgentId,
                providerId, modelId, cachedProjection.Messages, CancellationToken: ct);
            var beforeHistoryEstimate = await _provider.EstimateTokensAsync(historyEstimateRequest, ct).ConfigureAwait(false);
            var beforeHistoryTokens = beforeHistoryEstimate.InputTokens
                ?? ProviderTokenEstimator.Estimate(historyEstimateRequest).InputTokens ?? 0;
            historyEstimateRequest = historyEstimateRequest with { Messages = candidateMessages };
            var afterHistoryEstimate = await _provider.EstimateTokensAsync(historyEstimateRequest, ct).ConfigureAwait(false);
            var historyProjection = summaryProjection with
            {
                EstimatedTokens = afterHistoryEstimate.InputTokens
                    ?? ProviderTokenEstimator.Estimate(historyEstimateRequest).InputTokens ?? 0,
                EstimateSource = afterHistoryEstimate.InputTokens is null ? "runtime.utf8-bytes/4"
                    : $"provider.{afterHistoryEstimate.Accuracy.ToString().ToLowerInvariant()}"
            };
            await WriteProjectionCacheAsync(
                        sessionId,
                        source,
                        historyProjection,
                        beforeHistoryTokens,
                        compactionProvider!,
                        compactionProviderId,
                        compactionModelId,
                        contextPolicyHash,
                        agentPromptHash,
                        request.Selection.ToolCatalogVersion,
                        projectionCacheMissReason,
                        ct)
                    .ConfigureAwait(false);
            projection = summaryProjection;
            projectionCacheHit = false;
        }
        // 只检查估算的 tokens 是否超标
        // TailWindow 裁剪历史是正常行为，不应报错
        if (projection.EstimatedTokens > tokenLimit)
        {
            throw new InvalidDataException("context_budget_exhausted: Required system, tools, or current-turn content exceeds the input budget.");
        }
        snapshot = snapshot with
        {
            ContextProjection = new ContextProjectionSnapshot(
                projection.Strategy,
                [],
                projection.DroppedMessageCount,
                projection.Messages.Length,
                projection.EstimatedTokens,
                projection.EstimateSource,
                tokenLimit,
                CompactionProviderId: compactionProviderId,
                CompactionModelId: compactionModelId,
                CacheHit: projectionCacheHit,
                CacheMissReason: projectionCacheMissReason ?? (projection.IsAdjusted ? "projection-rebuilt" : null))
        };
        var updatedSnapshotJson = JsonSerializer.Serialize(
            snapshot,
            RuntimeJsonContext.Default.InvocationSnapshot);
        await _sessionRepo.SaveInvocationSnapshotAsync(
            runId,
            sessionId,
            snapshot,
            updatedSnapshotJson,
            ct).ConfigureAwait(false);
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
            MaxTokens: budget.OutputLimit,
            CancellationToken: ct,
            Skills: CreateSkillsContext(snapshot, runId, sessionId),
            RequestControl: (expert.RequestControl ?? new ExpertRequestControl()) with { MaxTokens = budget.OutputLimit })
        {
            HistoryMessageCount = Math.Max(0, providerMessages.Count - 1),
            OpenBlobRead = _toolResultBlobStore is null ? null : _toolResultBlobStore.OpenReadAsync,
            MaximumBufferedBlobBytes = (int)Math.Min(64L * 1024 * 1024, _runtimeLimits.MaxBlobBytes)
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

        if (restoredPlan is not null)
        {
            var planRestoredPayload = JsonSerializer.SerializeToElement(
                new { PlanId = restoredPlan.PlanId, Goal = restoredPlan.Goal, StepCount = restoredPlan.Steps.Length },
                RuntimeJsonContext.Default.JsonElement);
            yield return await CreateEnvelopeAsync(
                runtimeInstanceId,
                runId,
                runSequence++,
                "plan.restored",
                planRestoredPayload,
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
            catalogSnapshot = CaptureCatalogSnapshot()?.FilterByMode("expert");
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
                var liveCatalog = CaptureCatalogSnapshot()?.FilterByMode("expert") ?? catalogSnapshot;
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
                        CaptureCatalogSnapshot()?.FilterByMode("expert") ?? catalogSnapshot,
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
                // 元数据工具受理后，由 Expert 编排器同步路由子 Agent，并把真实结果写回 tool result。
                if (IsDelegateToAgentTool(toolCall.ToolId)
                    && gatewayResult.Kind is ToolGatewayResultKind.Success
                    && gatewayResult.Result is { Success: true })
                {
                    toolResult = await RouteDelegateAgentToolAsync(
                        toolCall,
                        expert,
                        runId,
                        sessionId,
                        invocationId,
                        ct).ConfigureAwait(false);
                }
                // 计划工具受理后，由 Expert 编排器处理计划创建和步骤执行。
                else if (IsExpertPlanTool(toolCall.ToolId)
                    && gatewayResult.Kind is ToolGatewayResultKind.Success
                    && gatewayResult.Result is { Success: true })
                {
                    toolResult = await RouteExpertPlanToolAsync(
                        toolCall,
                        expert,
                        runId,
                        sessionId,
                        invocationId,
                        ct).ConfigureAwait(false);
                }

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

                if (!IsExpertPlanTool(toolCall.ToolId) && !IsDelegateToAgentTool(toolCall.ToolId))
                {
                    var proof = new ExpertToolEvidence(
                        expert.Agent.AgentId, toolCall.CallId, toolCall.ToolId,
                        toolResult.Success, DateTimeOffset.UtcNow);
                    lock (_primaryEvidenceGate)
                    {
                        _primaryToolEvidence.Add(proof);
                    }
                    if (_planManager is not null)
                    {
                        await _planManager.RecordToolEvidenceAsync([proof], ct).ConfigureAwait(false);
                    }
                }
                else if (IsDelegateToAgentTool(toolCall.ToolId) && !toolResult.Success
                    && _planManager is not null)
                {
                    var proof = new ExpertToolEvidence(expert.Agent.AgentId,
                        toolCall.CallId, toolCall.ToolId, false, DateTimeOffset.UtcNow);
                    lock (_primaryEvidenceGate) { _primaryToolEvidence.Add(proof); }
                    await _planManager.RecordToolEvidenceAsync([proof], ct).ConfigureAwait(false);
                }

                var resultBytes = GetContentByteCount(toolResult);
                totalToolResultBytes += resultBytes;
                if (totalToolResultBytes > maxToolResultBytes)
                {
                    failure = CreateRuntimeError(
                        RuntimeErrorCodes.LimitsIncompatible,
                        "Tool results exceeded the configured Invocation size limit.");
                    break;
                }

                // 完整正文已在 Blob。发给模型前收成可见预览，避免不支持文件的 Provider 把结果丢掉。
                toolResult = await ToolResultPreview.MaterializeAsync(
                    toolResult,
                    ProviderSupportsFileBlobs(_provider),
                    _toolResultBlobStore,
                    _runtimeLimits.MaxInlineContentBytes,
                    ct).ConfigureAwait(false);

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
            var continuedEstimate = await _provider.EstimateTokensAsync(providerRequest, ct).ConfigureAwait(false);
            var continuedTokens = continuedEstimate.InputTokens ?? ProviderTokenEstimator.Estimate(providerRequest).InputTokens;
            if (continuedTokens is null || continuedTokens > tokenLimit)
            {
                failure = CreateRuntimeError("context_budget_exhausted",
                    "The current invocation and its tool results exceed the input budget. Start another turn after compaction.");
                break;
            }
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
            liveCatalogProvider: () => CaptureCatalogSnapshot()?.FilterByMode("expert") ?? catalogSnapshot);
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

    private static bool IsDelegateToAgentTool(string toolId) =>
        string.Equals(toolId, DelegateToAgentToolId, StringComparison.Ordinal);

    private static bool IsExpertPlanTool(string toolId) =>
        string.Equals(toolId, ExpertCreatePlanToolId, StringComparison.Ordinal)
        || string.Equals(toolId, ExpertExecuteStepToolId, StringComparison.Ordinal)
        || string.Equals(toolId, ExpertGetPlanToolId, StringComparison.Ordinal)
        || string.Equals(toolId, ExpertUpdateStepStatusToolId, StringComparison.Ordinal)
        || string.Equals(toolId, ExpertAbandonPlanToolId, StringComparison.Ordinal);

    /// <summary>并行 Job Worker：解析 AvailableAgents 并复用 InvokeSubAgentAsync。</summary>
    private async Task<AgentJobWorkerResult> ExecuteAgentJobAsync(
        ExpertModeOptions expert,
        string jobId,
        AgentJobCreateRequest request,
        string runId,
        string sessionId,
        string parentAgentId,
        CancellationToken ct)
    {
        _ = jobId;
        if (string.Equals(request.AgentName, expert.Agent.AgentId, StringComparison.OrdinalIgnoreCase))
        {
            return new AgentJobWorkerResult(
                false,
                null,
                $"Cannot run parallel job as the primary agent '{request.AgentName}'.");
        }

        var targetAgent = FindExistingAgent(
            expert.AvailableAgents,
            request.AgentName,
            request.Capabilities,
            expert.Agent.AgentId);
        if (targetAgent is null && request.CreateIfMissing && request.Temporary)
        {
            targetAgent = TemporaryAgentFactory.Create(
                expert.Agent,
                request.Role ?? request.AgentName,
                request.Task,
                request.Capabilities,
                request.AllowedToolIds);
        }

        if (targetAgent is null)
        {
            var availableAgentIds = string.Join(", ", expert.AvailableAgents?.Select(static a => a.AgentId) ?? []);
            return new AgentJobWorkerResult(
                false,
                null,
                $"Sub-agent '{request.AgentName}' not found in AvailableAgents and temporary creation was not enabled. Available: [{availableAgentIds}]");
        }

        var (content, success, evidence) = await InvokeSubAgentAsync(
            targetAgent.AgentId,
            targetAgent,
            request.Task,
            runId,
            sessionId,
            parentInvocationId: Guid.NewGuid().ToString("N"),
            parentAgentId,
            ct).ConfigureAwait(false);

        if (!success)
        {
            return new AgentJobWorkerResult(
                false,
                null,
                $"Sub-agent '{targetAgent.AgentId}' failed to complete the task.");
        }

        return new AgentJobWorkerResult(true, FlattenContentText(content), null, evidence);
    }

    /// <summary>
    /// 在 mcli_delegate_agent 参数校验通过后，同步调用目标子 Agent，并把执行结果写回 tool result。
    /// </summary>
    private async Task<ToolResultContentBlock> RouteDelegateAgentToolAsync(
        ToolCallContentBlock toolCall,
        ExpertModeOptions expert,
        string runId,
        string sessionId,
        string parentInvocationId,
        CancellationToken ct)
    {
        if (!TryParseDelegateAgentArguments(
                toolCall.Arguments.GetRawText(),
                out var agentName,
                out var task,
                out var options,
                out var parseError))
        {
            return CreateDelegateToolResult(
                toolCall,
                success: false,
                delegated: false,
                agentName: agentName,
                task: task,
                message: parseError ?? "Invalid delegate agent arguments.",
                resultText: null);
        }

        if (string.Equals(agentName, expert.Agent.AgentId, StringComparison.OrdinalIgnoreCase))
        {
            return CreateDelegateToolResult(
                toolCall,
                success: false,
                delegated: false,
                agentName: agentName,
                task: task,
                message: $"Cannot delegate to the primary agent '{agentName}'.",
                resultText: null);
        }

        var targetAgent = FindExistingAgent(
            expert.AvailableAgents,
            agentName,
            options.Capabilities,
            expert.Agent.AgentId);
        if (targetAgent is null && options.CreateIfMissing && options.Temporary)
        {
            targetAgent = TemporaryAgentFactory.Create(
                expert.Agent,
                options.Role ?? agentName,
                task,
                options.Capabilities,
                options.AllowedToolIds);
        }

        if (targetAgent is null)
        {
            var availableAgentIds = string.Join(", ", expert.AvailableAgents?.Select(static a => a.AgentId) ?? []);
            return CreateDelegateToolResult(
                toolCall,
                success: false,
                delegated: false,
                agentName: agentName,
                task: task,
                message: $"Sub-agent '{agentName}' not found in AvailableAgents and temporary creation was not enabled. Available: [{availableAgentIds}]",
                resultText: null);
        }

        var resolvedAgentName = targetAgent.AgentId;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (options.TimeoutMs is { } timeoutMs)
        {
            timeout.CancelAfter(timeoutMs);
        }
        ContentBlock[] subAgentContent;
        bool success;
        ExpertToolEvidence[] evidence;
        try
        {
            (subAgentContent, success, evidence) = await InvokeSubAgentAsync(
                resolvedAgentName, targetAgent, task, runId, sessionId,
                parentInvocationId, expert.Agent.AgentId, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return CreateDelegateToolResult(toolCall, success: false, delegated: true,
                agentName: resolvedAgentName, task: task,
                message: $"Sub-agent '{resolvedAgentName}' timed out.", resultText: null);
        }

        if (!success)
        {
            return CreateDelegateToolResult(
                toolCall,
                success: false,
                delegated: true,
                agentName: resolvedAgentName,
                task: task,
                message: $"Sub-agent '{resolvedAgentName}' failed to complete the task.",
                resultText: null);
        }

        await AppendContentAsync(
            sessionId,
            parentInvocationId,
            resolvedAgentName,
            "assistant",
            subAgentContent,
            DateTimeOffset.UtcNow,
            ct).ConfigureAwait(false);

        var resultText = FlattenContentText(subAgentContent);
        lock (_primaryEvidenceGate)
        {
            _primaryToolEvidence.AddRange(evidence);
        }
        if (_planManager is not null)
        {
            await _planManager.RecordToolEvidenceAsync(evidence, ct).ConfigureAwait(false);
        }
        return CreateDelegateToolResult(
            toolCall,
            success: true,
            delegated: true,
            agentName: resolvedAgentName,
            task: task,
            message: $"Sub-agent '{resolvedAgentName}' completed the task.",
            resultText: resultText);
    }

    private sealed record DelegateAgentOptions(
        bool CreateIfMissing,
        bool Temporary,
        string? Role,
        string[]? Capabilities,
        string[]? AllowedToolIds,
        int? TimeoutMs = null);

    private static AgentRef? FindExistingAgent(
        AgentRef[]? agents,
        string agentName,
        string[]? capabilities,
        string? excludedAgentId = null)
    {
        if (agents is not { Length: > 0 })
        {
            return null;
        }

        agents = agents.Where(agent => !string.Equals(
            agent.AgentId, excludedAgentId, StringComparison.OrdinalIgnoreCase)).ToArray();
        var exact = agents.FirstOrDefault(agent =>
            string.Equals(agent.AgentId, agentName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(agent.DisplayName, agentName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        if (capabilities is not { Length: > 0 })
        {
            return null;
        }

        var requested = capabilities.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return agents.FirstOrDefault(agent =>
            agent.Capabilities is { Length: > 0 }
            && requested.IsSubsetOf(agent.Capabilities));
    }

    private static bool TryParseDelegateAgentArguments(
        string argumentsJson,
        out string agentName,
        out string task,
        out DelegateAgentOptions options,
        out string? error)
    {
        agentName = string.Empty;
        task = string.Empty;
        options = new DelegateAgentOptions(false, false, null, null, null);
        error = null;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Tool arguments must be a JSON object.";
                return false;
            }

            if (!root.TryGetProperty("agentName", out var agentNameElement)
                || agentNameElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(agentNameElement.GetString()))
            {
                error = "Missing or invalid 'agentName' parameter.";
                return false;
            }

            if (!root.TryGetProperty("task", out var taskElement)
                || taskElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(taskElement.GetString()))
            {
                error = "Missing or invalid 'task' parameter.";
                return false;
            }

            agentName = agentNameElement.GetString()!.Trim();
            task = taskElement.GetString()!.Trim();
            var createIfMissing = root.TryGetProperty("createIfMissing", out var createElement)
                && createElement.ValueKind == JsonValueKind.True;
            var temporary = root.TryGetProperty("temporary", out var temporaryElement)
                && temporaryElement.ValueKind == JsonValueKind.True;
            var role = root.TryGetProperty("role", out var roleElement)
                && roleElement.ValueKind == JsonValueKind.String
                ? roleElement.GetString()
                : null;
            var capabilities = ReadStringArray(root, "capabilities");
            var allowedToolIds = ReadStringArray(root, "allowedToolIds");
            int? timeoutMs = null;
            if (root.TryGetProperty("timeoutMs", out var timeoutElement))
            {
                if (timeoutElement.ValueKind != JsonValueKind.Number
                    || !timeoutElement.TryGetInt32(out var parsedTimeout) || parsedTimeout < 1_000)
                {
                    error = "'timeoutMs' must be an integer of at least 1000 milliseconds.";
                    return false;
                }
                timeoutMs = parsedTimeout;
            }
            options = new DelegateAgentOptions(createIfMissing, temporary, role, capabilities, allowedToolIds, timeoutMs);
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string[]? ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return value.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()!)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static ToolResultContentBlock CreateDelegateToolResult(
        ToolCallContentBlock toolCall,
        bool success,
        bool delegated,
        string agentName,
        string task,
        string message,
        string? resultText)
    {
        var outputJson = BuildDelegateResultJson(
            delegated,
            success,
            agentName,
            task,
            message,
            resultText);
        return new ToolResultContentBlock(
            toolCall.CallId,
            toolCall.ToolId,
            success,
            [new TextContentBlock(outputJson)]);
    }

    /// <summary>
    /// 路由 Expert 计划工具调用到 ExpertPlanManager。
    /// </summary>
    private async Task<ToolResultContentBlock> RouteExpertPlanToolAsync(
        ToolCallContentBlock toolCall,
        ExpertModeOptions expert,
        string runId,
        string sessionId,
        string invocationId,
        CancellationToken ct)
    {
        try
        {
            if (string.Equals(toolCall.ToolId, ExpertCreatePlanToolId, StringComparison.Ordinal))
            {
                return await HandleCreatePlanAsync(toolCall, runId, sessionId, invocationId, ct)
                    .ConfigureAwait(false);
            }

            if (string.Equals(toolCall.ToolId, ExpertExecuteStepToolId, StringComparison.Ordinal))
            {
                return await HandleExecuteStepAsync(toolCall, expert, runId, sessionId, invocationId, ct)
                    .ConfigureAwait(false);
            }

            if (string.Equals(toolCall.ToolId, ExpertGetPlanToolId, StringComparison.Ordinal))
            {
                return await HandleGetPlanAsync(toolCall, runId, sessionId, invocationId, ct)
                    .ConfigureAwait(false);
            }

            if (string.Equals(toolCall.ToolId, ExpertUpdateStepStatusToolId, StringComparison.Ordinal))
            {
                return await HandleUpdateStepStatusAsync(toolCall, runId, sessionId, invocationId, ct)
                    .ConfigureAwait(false);
            }

            if (string.Equals(toolCall.ToolId, ExpertAbandonPlanToolId, StringComparison.Ordinal))
            {
                return await HandleAbandonPlanAsync(toolCall, runId, sessionId, invocationId, ct)
                    .ConfigureAwait(false);
            }

            return CreatePlanToolErrorResult(
                toolCall,
                $"Unknown plan tool: {toolCall.ToolId}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await FailInterruptedStepAsync(toolCall, runId, "Step execution was cancelled; reconcile partial results before retrying.").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await FailInterruptedStepAsync(toolCall, runId, ex.Message).ConfigureAwait(false);
            return CreatePlanToolErrorResult(
                toolCall,
                $"Plan tool execution failed: {ex.Message}");
        }
    }

    private async Task FailInterruptedStepAsync(ToolCallContentBlock toolCall, string runId, string error)
    {
        if (toolCall.ToolId != ExpertExecuteStepToolId
            || !TryParseExecuteStepArguments(toolCall.Arguments.GetRawText(), out var index, out _)
            || _planManager?.ActivePlan is not { } plan
            || index < 0 || index >= plan.Steps.Length
            || plan.Steps[index].Status != ExpertStepStatus.Running)
        {
            return;
        }
        await _planManager.UpdateStepStatusAsync(plan.Steps[index].StepId, runId,
            ExpertStepStatus.Failed, notes: error, ct: CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<ToolResultContentBlock> HandleCreatePlanAsync(
        ToolCallContentBlock toolCall,
        string runId,
        string sessionId,
        string invocationId,
        CancellationToken ct)
    {
        if (!TryParseCreatePlanArguments(
                toolCall.Arguments.GetRawText(),
                out var goal,
                out var stepDrafts,
                out var parseError))
        {
            return CreatePlanToolErrorResult(toolCall, parseError ?? "Invalid create_plan arguments.");
        }

        var request = new CreateExpertPlanRequest(goal, stepDrafts);
        var plan = await _planManager!.CreatePlanAsync(sessionId, runId, request, ct).ConfigureAwait(false);
        var outputJson = SerializePlanResult(plan);
        return new ToolResultContentBlock(
            toolCall.CallId,
            toolCall.ToolId,
            Success: true,
            [new TextContentBlock(outputJson)]);
    }

    private async Task<ToolResultContentBlock> HandleExecuteStepAsync(
        ToolCallContentBlock toolCall,
        ExpertModeOptions expert,
        string runId,
        string sessionId,
        string invocationId,
        CancellationToken ct)
    {
        if (!TryParseExecuteStepArguments(
                toolCall.Arguments.GetRawText(),
                out var stepIndex,
                out var parseError))
        {
            return CreatePlanToolErrorResult(toolCall, parseError ?? "Invalid execute_step arguments.");
        }

        var activePlan = _planManager!.ActivePlan;
        if (activePlan is null)
        {
            return CreatePlanToolErrorResult(toolCall, "No active plan.");
        }

        if (stepIndex < 0 || stepIndex >= activePlan.Steps.Length)
        {
            return CreatePlanToolErrorResult(toolCall, $"Step index {stepIndex} is out of range.");
        }

        var step = activePlan.Steps[stepIndex];
        if (step.Status is ExpertStepStatus.Completed or ExpertStepStatus.Failed or ExpertStepStatus.Skipped)
        {
            return CreatePlanToolErrorResult(
                toolCall,
                $"Step {stepIndex} is already terminal ({step.Status}) and cannot be executed again.");
        }

        var stepId = step.StepId;
        var executionPolicy = step.ExecutionPolicy ?? "PrimaryAgent";
        if (!string.Equals(executionPolicy, "PrimaryAgent", StringComparison.OrdinalIgnoreCase))
        {
            if (step.Status == ExpertStepStatus.Running)
            {
                return CreatePlanToolErrorResult(toolCall, "Assigned step is already running; reconcile its outcome before retrying.");
            }
            await _planManager.UpdateStepStatusAsync(
                stepId, runId, ExpertStepStatus.Running, ct: ct).ConfigureAwait(false);
        }
        string? executionResult = null;
        ExpertToolEvidence[] stepEvidence = [];
        if (string.Equals(executionPolicy, "PreferParallelTemporaryAgents", StringComparison.OrdinalIgnoreCase))
        {
            if (_agentJobCoordinator is null || step.ParallelTasks is not { Length: >= 2 and <= 5 })
            {
                var failed = await _planManager.UpdateStepStatusAsync(
                    stepId, runId, ExpertStepStatus.Failed, progress: 0,
                    notes: "PreferParallelTemporaryAgents requires 2-5 explicit parallelTasks and an Agent Job coordinator.", ct).ConfigureAwait(false);
                return CreatePlanToolResult(toolCall, false, SerializeStepExecutionResult(failed, stepId));
            }

            var requests = step.ParallelTasks.Select((task, index) => new AgentJobCreateRequest(
                step.AgentName ?? $"plan-worker-{index + 1}",
                task,
                CreateIfMissing: true,
                Temporary: true,
                Role: "parallel plan worker",
                AllowedToolIds: (expert.Agent.AllowedToolIds ?? TemporaryAgentFactory.DefaultReadOnlyToolIds).ToArray())).ToArray();
            var jobs = _agentJobCoordinator.CreateMany(requests, runId, sessionId, expert.Agent.AgentId);
            AgentJobWaitResult wait;
            try
            {
                wait = await _agentJobCoordinator.WaitAsync(
                    jobs.Select(static job => job.JobId).ToArray(), 300_000, ct).ConfigureAwait(false);
            }
            finally
            {
                foreach (var job in jobs)
                {
                    _agentJobCoordinator.TryCancel(job.JobId);
                }
            }
            var results = _agentJobCoordinator.CollectMany(jobs.Select(static job => job.JobId).ToArray());
            if (!wait.AllTerminal || !wait.AllSucceeded || results.Any(static result => !result.Ready || !result.Success || string.IsNullOrWhiteSpace(result.Result)))
            {
                var failed = await _planManager.UpdateStepStatusAsync(
                    stepId, runId, ExpertStepStatus.Failed, progress: 0,
                    notes: string.Join("; ", results.Select(static result => result.Error ?? result.Message)), ct).ConfigureAwait(false);
                return CreatePlanToolResult(toolCall, false, SerializeStepExecutionResult(failed, stepId));
            }

            stepEvidence = wait.Jobs.SelectMany(static job => job.Evidence ?? []).ToArray();
            executionResult = string.Join(Environment.NewLine, results.Select(static result => result.Result));
        }
        else if (!string.Equals(executionPolicy, "PrimaryAgent", StringComparison.OrdinalIgnoreCase))
        {
            var target = FindExistingAgent(expert.AvailableAgents, step.AgentName ?? string.Empty, null, expert.Agent.AgentId);
            if (target is null && string.Equals(executionPolicy, "TemporaryAgentIfUnmatched", StringComparison.OrdinalIgnoreCase))
            {
                target = TemporaryAgentFactory.Create(
                    expert.Agent,
                    step.AgentName ?? "temporary-worker",
                    step.Description ?? step.Title,
                    null,
                    expert.Agent.AllowedToolIds);
            }

            if (target is null)
            {
                var failed = await _planManager.UpdateStepStatusAsync(
                    stepId, runId, ExpertStepStatus.Failed, progress: 0,
                    notes: $"No agent matched execution policy '{executionPolicy}'.", ct).ConfigureAwait(false);
                return CreatePlanToolResult(toolCall, false, SerializeStepExecutionResult(failed, stepId));
            }

            var (content, success, evidence) = await InvokeSubAgentAsync(
                target.AgentId, target, step.Description ?? step.Title, runId, sessionId,
                invocationId, expert.Agent.AgentId, ct).ConfigureAwait(false);
            stepEvidence = evidence;
            executionResult = FlattenContentText(content);
            if (!success || string.IsNullOrWhiteSpace(executionResult))
            {
                var failed = await _planManager.UpdateStepStatusAsync(
                    stepId, runId, ExpertStepStatus.Failed, progress: 0,
                    notes: "Assigned Agent did not complete the step.", ct).ConfigureAwait(false);
                return CreatePlanToolResult(toolCall, false, SerializeStepExecutionResult(failed, stepId));
            }
        }

        if (string.Equals(executionPolicy, "PrimaryAgent", StringComparison.OrdinalIgnoreCase))
        {
            if (step.Status != ExpertStepStatus.Running || step.StartedAt is null)
            {
                return CreatePlanToolErrorResult(toolCall,
                    "PrimaryAgent requires a running step with execution evidence.");
            }
            stepEvidence = step.Evidence ?? [];
            using var executeArgs = JsonDocument.Parse(toolCall.Arguments.GetRawText());
            executionResult = executeArgs.RootElement.TryGetProperty("result", out var resultElement)
                && resultElement.ValueKind == JsonValueKind.String
                ? resultElement.GetString() : null;
        }

        var evidenceError = ExpertExecutionEvidence.Validate(
            step.CompletionMode, step.ExpectedOutput, executionResult, stepEvidence);
        if (evidenceError is not null)
        {
            if (!string.Equals(executionPolicy, "PrimaryAgent", StringComparison.OrdinalIgnoreCase)
                || stepEvidence.Any(static item => !item.Success))
            {
                await _planManager.UpdateStepStatusWithEvidenceAsync(
                    stepId, runId, ExpertStepStatus.Failed, 0,
                    evidenceError, stepEvidence, ct).ConfigureAwait(false);
            }
            return CreatePlanToolErrorResult(toolCall, evidenceError);
        }

        var plan = await _planManager.UpdateStepStatusWithEvidenceAsync(
            stepId, runId, ExpertStepStatus.Completed, 100,
            executionResult, stepEvidence, ct).ConfigureAwait(false);

        var outputJson = SerializeStepExecutionResult(plan, stepId);
        return new ToolResultContentBlock(
            toolCall.CallId,
            toolCall.ToolId,
            Success: true,
            [new TextContentBlock(outputJson)]);
    }

    private static ToolResultContentBlock CreatePlanToolResult(
        ToolCallContentBlock toolCall,
        bool success,
        string outputJson) =>
        new(
            toolCall.CallId,
            toolCall.ToolId,
            success,
            [new TextContentBlock(outputJson)]);

    private static string BuildDelegateResultJson(
        bool delegated,
        bool success,
        string agentName,
        string task,
        string message,
        string? resultText)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("delegated", delegated);
            writer.WriteBoolean("success", success);
            if (!string.IsNullOrEmpty(agentName))
            {
                writer.WriteString("agentName", agentName);
            }

            if (!string.IsNullOrEmpty(task))
            {
                writer.WriteString("task", task);
            }

            writer.WriteString("message", message);
            if (resultText is not null)
            {
                writer.WriteString("result", resultText);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool TryParseCreatePlanArguments(
        string argumentsJson,
        out string goal,
        out ExpertPlanStepDraft[] stepDrafts,
        out string? error)
    {
        goal = string.Empty;
        stepDrafts = [];
        error = null;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;

            if (!root.TryGetProperty("goal", out var goalElement)
                || goalElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(goalElement.GetString()))
            {
                error = "Missing or invalid 'goal' parameter.";
                return false;
            }

            if (!root.TryGetProperty("steps", out var stepsElement)
                || stepsElement.ValueKind != JsonValueKind.Array)
            {
                error = "Missing or invalid 'steps' parameter (must be an array).";
                return false;
            }

            var stepList = new List<ExpertPlanStepDraft>();
            var stepIndex = 0;
            foreach (var stepElement in stepsElement.EnumerateArray())
            {
                if (stepElement.ValueKind != JsonValueKind.Object)
                {
                    error = $"Step {stepIndex} must be an object.";
                    return false;
                }

                if (!stepElement.TryGetProperty("title", out var titleElement)
                    || titleElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(titleElement.GetString()))
                {
                    error = $"Step {stepIndex} missing or invalid 'title'.";
                    return false;
                }

                var stepId = $"step-{stepIndex + 1}";
                var title = titleElement.GetString()!.Trim();
                var description = stepElement.TryGetProperty("description", out var descElement)
                    && descElement.ValueKind == JsonValueKind.String
                    ? descElement.GetString()?.Trim()
                    : null;
                var agentName = stepElement.TryGetProperty("agentName", out var agentElement)
                    && agentElement.ValueKind == JsonValueKind.String
                    ? agentElement.GetString()?.Trim()
                    : null;
                var executionPolicy = stepElement.TryGetProperty("executionPolicy", out var policyElement)
                    && policyElement.ValueKind == JsonValueKind.String
                    ? policyElement.GetString()?.Trim()
                    : null;
                var parallelTasks = stepElement.TryGetProperty("parallelTasks", out var parallelElement)
                    && parallelElement.ValueKind == JsonValueKind.Array
                    ? parallelElement.EnumerateArray()
                        .Where(static item => item.ValueKind == JsonValueKind.String)
                        .Select(static item => item.GetString()!)
                        .Where(static item => !string.IsNullOrWhiteSpace(item))
                        .Take(6)
                        .ToArray()
                    : null;

                var completionMode = stepElement.TryGetProperty("completionMode", out var modeElement)
                    && modeElement.ValueKind == JsonValueKind.String
                    ? modeElement.GetString()! : "tools";
                var expectedOutput = stepElement.TryGetProperty("expectedOutput", out var expectedElement)
                    && expectedElement.ValueKind == JsonValueKind.String
                    ? expectedElement.GetString() : null;
                if (completionMode is not ("tools" or "analysis")
                    || (completionMode == "analysis" && string.IsNullOrWhiteSpace(expectedOutput)))
                {
                    error = $"Step {stepIndex} requires tools or analysis with non-empty expectedOutput.";
                    return false;
                }

                stepList.Add(new ExpertPlanStepDraft(stepId, title, description, agentName,
                    executionPolicy, parallelTasks, completionMode, expectedOutput));
                stepIndex++;
            }

            if (stepList.Count < 2 || stepList.Count > 15)
            {
                error = "Plan must have 2-15 steps.";
                return false;
            }

            goal = goalElement.GetString()!.Trim();
            stepDrafts = [.. stepList];
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryParseExecuteStepArguments(
        string argumentsJson,
        out int stepIndex,
        out string? error)
    {
        stepIndex = -1;
        error = null;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("stepIndex", out var stepIndexElement)
                || stepIndexElement.ValueKind != JsonValueKind.Number
                || !stepIndexElement.TryGetInt32(out stepIndex)
                || stepIndex < 0)
            {
                error = "Missing or invalid non-negative 'stepIndex' parameter.";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static ToolResultContentBlock CreatePlanToolErrorResult(
        ToolCallContentBlock toolCall,
        string errorMessage)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", false);
            writer.WriteString("error", errorMessage);
            writer.WriteEndObject();
        }

        var outputJson = Encoding.UTF8.GetString(stream.ToArray());
        return new ToolResultContentBlock(
            toolCall.CallId,
            toolCall.ToolId,
            Success: false,
            [new TextContentBlock(outputJson)]);
    }

    private static string SerializePlanResult(ExpertPlan plan)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", true);
            writer.WriteString("planId", plan.PlanId);
            writer.WriteString("goal", plan.Goal);
            writer.WriteNumber("totalSteps", plan.Steps.Length);
            writer.WriteStartArray("steps");
            for (int i = 0; i < plan.Steps.Length; i++)
            {
                var step = plan.Steps[i];
                writer.WriteStartObject();
                writer.WriteNumber("index", i);
                writer.WriteString("stepId", step.StepId);
                writer.WriteString("title", step.Title);
                if (step.Description is not null)
                {
                    writer.WriteString("description", step.Description);
                }
                writer.WriteString("status", step.Status.ToString().ToLowerInvariant());
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string SerializeStepExecutionResult(ExpertPlan plan, string executedStepId)
    {
        var executedStep = plan.Steps.FirstOrDefault(s => s.StepId == executedStepId);
        var completedCount = plan.Steps.Count(s => s.Status == ExpertStepStatus.Completed);
        var totalCount = plan.Steps.Length;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", true);
            writer.WriteString("stepId", executedStepId);
            writer.WriteString("status", executedStep?.Status.ToString().ToLowerInvariant() ?? "unknown");
            writer.WriteNumber("completedSteps", completedCount);
            writer.WriteNumber("totalSteps", totalCount);
            writer.WriteBoolean("planCompleted", plan.Status == ExpertPlanStatus.Completed);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private async Task<ToolResultContentBlock> HandleGetPlanAsync(
        ToolCallContentBlock toolCall,
        string runId,
        string sessionId,
        string invocationId,
        CancellationToken ct)
    {
        if (_planManager?.ActivePlan is null)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteBoolean("success", true);
                writer.WriteBoolean("hasPlan", false);
                writer.WriteString("message", "No active plan found.");
                writer.WriteEndObject();
            }

            var outputJson = Encoding.UTF8.GetString(stream.ToArray());
            return new ToolResultContentBlock(
                toolCall.CallId,
                toolCall.ToolId,
                Success: true,
                [new TextContentBlock(outputJson)]);
        }

        var plan = _planManager.ActivePlan;
        var outputJsonWithPlan = SerializeGetPlanResult(plan);

        return new ToolResultContentBlock(
            toolCall.CallId,
            toolCall.ToolId,
            Success: true,
            [new TextContentBlock(outputJsonWithPlan)]);
    }

    private async Task<ToolResultContentBlock> HandleUpdateStepStatusAsync(
        ToolCallContentBlock toolCall,
        string runId,
        string sessionId,
        string invocationId,
        CancellationToken ct)
    {
        if (_planManager?.ActivePlan is null)
        {
            return CreatePlanToolErrorResult(toolCall, "No active plan to update.");
        }

        if (!TryParseUpdateStepStatusArguments(
                toolCall.Arguments.GetRawText(),
                out var stepIndex,
                out var newStatus,
                out var notes,
                out var parseError))
        {
            return CreatePlanToolErrorResult(toolCall, parseError ?? "Invalid update_step_status arguments.");
        }

        try
        {
            var targetStep = _planManager.ActivePlan.Steps[stepIndex];
            var oldStatus = targetStep.Status;
            if (newStatus == ExpertStepStatus.Running
                && (targetStep.ExecutionPolicy is null
                    || string.Equals(targetStep.ExecutionPolicy, "PrimaryAgent", StringComparison.OrdinalIgnoreCase))
                && _planManager.ActivePlan.Steps.Any(step => step.StepId != targetStep.StepId
                    && step.Status == ExpertStepStatus.Running
                    && (step.ExecutionPolicy is null
                        || string.Equals(step.ExecutionPolicy, "PrimaryAgent", StringComparison.OrdinalIgnoreCase))))
            {
                return CreatePlanToolErrorResult(toolCall,
                    "Only one PrimaryAgent step may be running; finish or reset the current step before starting another.");
            }
            if (newStatus is ExpertStepStatus.Completed)
            {
                return CreatePlanToolErrorResult(
                    toolCall,
                    "A step can only become completed through mcli_expert_execute_step after its execution succeeds.");
            }

            var updatedPlan = await _planManager.UpdateStepStatusByIndexAsync(
                stepIndex,
                runId,
                newStatus,
                notes,
                ct).ConfigureAwait(false);

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteBoolean("success", true);
                writer.WriteNumber("stepIndex", stepIndex);
                writer.WriteString("oldStatus", oldStatus.ToString().ToLowerInvariant());
                writer.WriteString("newStatus", newStatus.ToString().ToLowerInvariant());
                writer.WriteString("message", $"Step {stepIndex} updated from {oldStatus} to {newStatus}.");
                writer.WriteEndObject();
            }

            var outputJson = Encoding.UTF8.GetString(stream.ToArray());
            return new ToolResultContentBlock(
                toolCall.CallId,
                toolCall.ToolId,
                Success: true,
                [new TextContentBlock(outputJson)]);
        }
        catch (Exception ex)
        {
            return CreatePlanToolErrorResult(toolCall, $"Failed to update step status: {ex.Message}");
        }
    }

    private async Task<ToolResultContentBlock> HandleAbandonPlanAsync(
        ToolCallContentBlock toolCall,
        string runId,
        string sessionId,
        string invocationId,
        CancellationToken ct)
    {
        if (_planManager?.ActivePlan is null)
        {
            return CreatePlanToolErrorResult(toolCall, "No active plan to abandon.");
        }

        var planId = _planManager.ActivePlan.PlanId;
        await _planManager.AbandonPlanAsync(reason: null, ct).ConfigureAwait(false);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", true);
            writer.WriteString("planId", planId);
            writer.WriteString("message", $"Plan '{planId}' has been abandoned.");
            writer.WriteEndObject();
        }

        var outputJson = Encoding.UTF8.GetString(stream.ToArray());
        return new ToolResultContentBlock(
            toolCall.CallId,
            toolCall.ToolId,
            Success: true,
            [new TextContentBlock(outputJson)]);
    }

    private static string SerializeGetPlanResult(ExpertPlan plan)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", true);
            writer.WriteBoolean("hasPlan", true);
            writer.WriteString("planId", plan.PlanId);
            writer.WriteString("title", plan.Goal);
            writer.WriteString("status", plan.Status.ToString().ToLowerInvariant());

            writer.WriteStartArray("steps");
            for (int i = 0; i < plan.Steps.Length; i++)
            {
                var step = plan.Steps[i];
                writer.WriteStartObject();
                writer.WriteNumber("index", i);
                writer.WriteString("stepId", step.StepId);
                writer.WriteString("title", step.Title);
                if (step.Description is not null)
                {
                    writer.WriteString("description", step.Description);
                }
                writer.WriteString("status", step.Status.ToString().ToLowerInvariant());
                writer.WriteNumber("progress", step.Progress);
                if (step.AgentName is not null)
                {
                    writer.WriteString("agentName", step.AgentName);
                }
                if (step.Result is not null)
                {
                    writer.WriteString("result", step.Result);
                }
                if (step.ErrorMessage is not null)
                {
                    writer.WriteString("error", step.ErrorMessage);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteString("message", $"Active plan: {plan.Goal} ({plan.Steps.Count(s => s.Status == ExpertStepStatus.Completed)}/{plan.Steps.Length} completed)");
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool TryParseUpdateStepStatusArguments(
        string argumentsJson,
        out int stepIndex,
        out ExpertStepStatus newStatus,
        out string? notes,
        out string? error)
    {
        stepIndex = -1;
        newStatus = ExpertStepStatus.Pending;
        notes = null;
        error = null;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;

            if (!root.TryGetProperty("stepIndex", out var stepIndexElement)
                || stepIndexElement.ValueKind != JsonValueKind.Number)
            {
                error = "Missing or invalid 'stepIndex' parameter.";
                return false;
            }

            stepIndex = stepIndexElement.GetInt32();
            if (stepIndex < 0)
            {
                error = "stepIndex must be non-negative.";
                return false;
            }

            if (!root.TryGetProperty("newStatus", out var statusElement)
                || statusElement.ValueKind != JsonValueKind.String)
            {
                error = "Missing or invalid 'newStatus' parameter.";
                return false;
            }

            var statusString = statusElement.GetString();
            if (!Enum.TryParse<ExpertStepStatus>(statusString, ignoreCase: true, out newStatus))
            {
                error = $"Invalid status value: {statusString}. Valid values: pending, running, completed, failed, skipped.";
                return false;
            }

            if (root.TryGetProperty("notes", out var notesElement)
                && notesElement.ValueKind == JsonValueKind.String)
            {
                notes = notesElement.GetString();
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string FlattenContentText(ContentBlock[] content)
    {
        if (content.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var block in content)
        {
            if (block is TextContentBlock text
                && !string.IsNullOrEmpty(text.Text))
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.Append(text.Text);
            }
        }

        return builder.ToString();
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

    private static bool ProviderSupportsFileBlobs(IRuntimeProviderAdapter provider) =>
        provider.Capabilities.Files;

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

    private static bool IsSummaryStrategy(string strategy) =>
        string.Equals(strategy, "Summary", StringComparison.OrdinalIgnoreCase)
        || string.Equals(strategy, "SummaryCompressed", StringComparison.OrdinalIgnoreCase);

    private static void ValidateCompactionModel(
        IRuntimeProviderAdapter provider,
        string modelId)
    {
        if (provider.Models.Count == 0 || provider.Models.Any(model =>
                string.Equals(model.Id, modelId, StringComparison.Ordinal)
                || string.Equals(model.ProviderModelId, modelId, StringComparison.Ordinal)))
        {
            return;
        }

        throw new KeyNotFoundException(
            $"Model {modelId} is not available from Provider {provider.ProviderId}.");
    }

    private async Task WriteProjectionCacheAsync(string sessionId,
        ConversationCompactionSource source,
        ContextProjectionBuilder.Result projection,
        int beforeEstimatedTokens,
        IRuntimeProviderAdapter provider,
        string providerId,
        string modelId,
        string contextPolicyHash,
        string agentPromptHash,
        string toolCatalogVersion,
        string? cacheMissReason,
        CancellationToken ct)
    {
        var cache = new ConversationCompactCacheV1(
            ConversationCompactCacheV1.CurrentSchema,
            sessionId,
            source.HistorySha256,
            "summary",
            providerId,
            modelId,
            null,
            source.Records.Count,
            projection.DroppedMessageCount,
            beforeEstimatedTokens,
            projection.EstimatedTokens,
            projection.EstimateSource,
            [CreateCompactMessage(projection.Messages[0])],
            DateTimeOffset.UtcNow,
            RuntimeMode: RuntimeMode.Expert.ToString(),
            ContextPolicyHash: contextPolicyHash,
            AgentPromptHash: agentPromptHash,
            ToolCatalogVersion: toolCatalogVersion,
            SummaryModelVersion: ResolveModelVersion(provider, modelId),
            CacheMissReason: cacheMissReason,
            CoveredThroughSequence: source.Records[projection.DroppedMessageCount - 1].Sequence,
            PrefixHistorySha256: source.PrefixHistoryHashes![source.Records[projection.DroppedMessageCount - 1].Sequence],
            ProjectionRevision: Guid.NewGuid().ToString("N"),
            SummaryValidated: true);
        await _store.WriteCompactCacheAsync(sessionId, cache, ct)
            .ConfigureAwait(false);
    }

    private static ConversationCompactMessageV1 CreateCompactMessage(RuntimeProviderMessage message) => new(
        message.Role,
        JsonSerializer.SerializeToElement(
            message.Content,
            RuntimeJsonContext.Default.ContentBlockArray));

    private static string ResolveModelVersion(
        IRuntimeProviderAdapter provider,
        string modelId)
    {
        var found = provider.Models.FirstOrDefault(model =>
            string.Equals(model.Id, modelId, StringComparison.Ordinal)
            || string.Equals(model.ProviderModelId, modelId, StringComparison.Ordinal));
        return found?.ProviderModelId ?? found?.Id ?? modelId;
    }

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
        // Expert 不得暴露 Work/Meeting 专用工具（SupportedModes 过滤）。
        return snapshot is null ? null : [.. snapshot.FilterByMode("expert").Tools];
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
        agentListBuilder.AppendLine("### Synchronous single-agent handoff");
        agentListBuilder.AppendLine(
            "Use `mcli_delegate_agent` when you need one specialist and must block for its `result` immediately.");
        agentListBuilder.AppendLine();
        agentListBuilder.AppendLine("### Parallel background jobs (no scheduling)");
        agentListBuilder.AppendLine(
            "Use these tools to run multiple specialists in parallel without blocking the create call:");
        agentListBuilder.AppendLine("- `mcli_agent_job_create` / `mcli_agent_job_create_many`: enqueue jobs, get `jobId`(s)");
        agentListBuilder.AppendLine("- `mcli_agent_job_status`: query state");
        agentListBuilder.AppendLine(
            "- `mcli_agent_job_result`: recover finished result(s) (`jobId` or `jobIds`); non-terminal returns ready=false");
        agentListBuilder.AppendLine(
            "- `mcli_agent_job_wait`: wait until terminal (or timeout) then recover results — preferred after create_many");
        agentListBuilder.AppendLine("- `mcli_agent_job_cancel`: request cancel");
        agentListBuilder.AppendLine(
            "There is no cron/timer API; long multi-step orchestration belongs in Work mode.");
        agentListBuilder.AppendLine();
        agentListBuilder.AppendLine(
            "Users may also start a turn with `@agentName <task>`; that path is handled before your turn.");
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
                    $"- **{agent.AgentId}** (`@{agent.AgentId}`) - {description}");
            }
        }

        return baseInstructions + agentListBuilder.ToString();
    }

    /// <summary>
    /// Injects active plan progress into system prompt to guide execution.
    /// </summary>
    private string PrepareSystemPromptWithPlan(string baseSystemPrompt)
    {
        if (_planManager?.ActivePlan is null)
        {
            return baseSystemPrompt;
        }

        return AppendPlanProgressReminder(baseSystemPrompt, _planManager.ActivePlan);
    }

    /// <summary>
    /// Appends plan progress section to system prompt.
    /// </summary>
    private static string AppendPlanProgressReminder(string baseSystemPrompt, ExpertPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("## 📋 Active Execution Plan");
        sb.AppendLine();
        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"**Goal:** {plan.Goal}");
        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"**Status:** {plan.Status}");
        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"**Progress:** {CalculateOverallProgress(plan.Steps)}%");
        sb.AppendLine();
        sb.AppendLine("### Steps:");
        sb.AppendLine();

        foreach (var step in plan.Steps)
        {
            var icon = step.Status switch
            {
                ExpertStepStatus.Completed => "✅",
                ExpertStepStatus.Running => "▶️",
                ExpertStepStatus.Failed => "❌",
                ExpertStepStatus.Skipped => "⏭️",
                _ => "⬜"
            };

            var progressBar = step.Progress > 0
                ? $" [{new string('█', step.Progress / 10)}{new string('░', 10 - step.Progress / 10)}] {step.Progress}%"
                : "";

            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                $"{icon} **Step {step.StepId}**: {step.Title} ({step.Status}){progressBar}");

            if (!string.IsNullOrEmpty(step.Result))
            {
                sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"   └─ {step.Result}");
            }
            if (!string.IsNullOrEmpty(step.ErrorMessage))
            {
                sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"   └─ ⚠️ {step.ErrorMessage}");
            }
        }

        sb.AppendLine();

        // Key guidance: indicate next action
        var nextStep = plan.Steps.FirstOrDefault(s => s.Status == ExpertStepStatus.Pending);
        if (nextStep is not null)
        {
            sb.AppendLine("### 🎯 Next Action:");
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                $"Execute **Step {nextStep.StepId}** using `mcli_expert_execute_step`:");
            sb.AppendLine("```");
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                $"mcli_expert_execute_step(stepIndex: {Array.IndexOf(plan.Steps, nextStep)})");
            sb.AppendLine("```");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(nextStep.AgentName))
            {
                sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                    $"This step should be delegated to agent: **{nextStep.AgentName}**");
                sb.AppendLine();
            }
        }
        else if (plan.Steps.All(s => s.Status == ExpertStepStatus.Completed))
        {
            sb.AppendLine("### ✨ All Steps Completed");
            sb.AppendLine("Provide a comprehensive final summary to the user with:");
            sb.AppendLine("- Key outcomes from each step");
            sb.AppendLine("- Overall results and achievements");
            sb.AppendLine("- Any recommendations or next steps");
            sb.AppendLine();
        }
        else if (plan.Steps.Any(s => s.Status == ExpertStepStatus.Running))
        {
            var runningStep = plan.Steps.First(s => s.Status == ExpertStepStatus.Running);
            sb.AppendLine("### ⚡ Step In Progress:");
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                $"Step {runningStep.StepId} is currently running.");
            sb.AppendLine("Continue working on this step until completion.");
            sb.AppendLine();
        }
        else if (plan.Steps.Any(s => s.Status == ExpertStepStatus.Failed))
        {
            sb.AppendLine("### ⚠️ Plan Has Failed Steps");
            sb.AppendLine("Review the errors above and decide whether to:");
            sb.AppendLine("- Retry the failed step by executing it again");
            sb.AppendLine("- Skip to the next pending step");
            sb.AppendLine("- Report the failure to the user");
            sb.AppendLine();
        }

        return baseSystemPrompt + sb.ToString();
    }

    /// <summary>
    /// Calculates overall progress percentage from step statuses.
    /// </summary>
    private static int CalculateOverallProgress(ExpertPlanStep[] steps)
    {
        if (steps.Length == 0)
        {
            return 0;
        }

        var totalWeight = steps.Length * 100;
        var completedWeight = steps.Sum(s => s.Status == ExpertStepStatus.Completed ? 100 : s.Progress);
        return (int)Math.Round(completedWeight * 100.0 / totalWeight);
    }

    /// <summary>
    /// Invokes a sub-agent with the delegated task, supporting full tool calling capabilities.
    /// </summary>
    private async Task<(ContentBlock[] content, bool success, ExpertToolEvidence[] evidence)> InvokeSubAgentAsync(
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
        var subAgentProvider = string.Equals(subAgentProviderId, _provider.ProviderId, StringComparison.Ordinal)
            ? _provider
            : _providerResolver(subAgentProviderId);
        var subAgentModelId = subAgent.ModelId
            ?? (subAgentProvider.Models.Count > 0 ? subAgentProvider.Models[0].Id : "default");

        // Compose sub-agent's system prompt with memory if available
        var subAgentContext = _agentContextComposer is null
            ? AgentContextComposer.WithoutMemory(subAgent.SystemPrompt)
            : await _agentContextComposer.ComposeAsync(subAgent.SystemPrompt, ct).ConfigureAwait(false);
        var subAgentInstructions = ConversationEnvironmentPrompt.Append(
            subAgentContext.Instructions,
            _workspaceRoot,
            _conversationLanguage);

        // Create initial messages for sub-agent: system + user task
        var systemMessage = new RuntimeProviderMessage(
            RuntimeProviderRoles.System,
            [new TextContentBlock(subAgentInstructions)]);

        var userTaskMessage = new RuntimeProviderMessage(
            RuntimeProviderRoles.User,
            [new TextContentBlock(task)]);

        var subAgentMessages = new List<RuntimeProviderMessage> { systemMessage, userTaskMessage };

        ToolCatalogSnapshot? CaptureSubAgentCatalog()
        {
            var snapshot = CaptureCatalogSnapshot()?.FilterByMode("expert");
            return snapshot is null || subAgent.AllowedToolIds is null
                ? snapshot
                : new ToolCatalogSnapshot(
                    snapshot.HostCatalogVersion,
                    snapshot.EffectiveVersion,
                    snapshot.AllTools.Where(tool =>
                        subAgent.AllowedToolIds.Contains(tool.ToolId, StringComparer.Ordinal)));
        }

        // Capture tool catalog for sub-agent
        var catalogSnapshot = CaptureSubAgentCatalog();
        ToolDescriptor[]? tools = _toolGateway is not null && catalogSnapshot is not null
            ? [.. catalogSnapshot.Tools]
            : null;

        var assistantContent = new List<ContentBlock>();
        var evidence = new List<ExpertToolEvidence>();
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
                Skills: null,
                RequestControl: null)
            {
                OpenBlobRead = _toolResultBlobStore is null ? null : _toolResultBlobStore.OpenReadAsync,
                MaximumBufferedBlobBytes = (int)Math.Min(64L * 1024 * 1024, _runtimeLimits.MaxBlobBytes)
            };

            // Call provider
            var completed = false;
            RuntimeError? failure = null;

            await foreach (var providerEvent in ProviderInvocationBoundary.CompleteStreamingAsync(
                subAgentProvider,
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

            if (failure is not null || !completed)
            {
                return ([], false, evidence.ToArray());
            }

            // Check for tool calls
            var toolCalls = assistantContent.OfType<ToolCallContentBlock>().ToArray();
            if (toolCalls.Length == 0)
            {
                var hasOutput = assistantContent.Any(block => block switch
                {
                    TextContentBlock text => !string.IsNullOrWhiteSpace(text.Text),
                    _ => false
                });
                return hasOutput && evidence.All(static item => item.Success)
                    ? (assistantContent.ToArray(), true, evidence.ToArray())
                    : ([], false, evidence.ToArray());
            }

            if (toolCalls.Length > maxToolCallsPerRound)
            {
                return ([], false, evidence.ToArray());
            }

            // Add assistant message to conversation
            subAgentMessages.Add(new RuntimeProviderMessage(
                RuntimeProviderRoles.Assistant,
                [.. assistantContent]));

            // Execute tool calls with ParentAgentId
            foreach (var toolCall in toolCalls)
            {
                var liveCatalog = CaptureSubAgentCatalog() ?? catalogSnapshot;
                if (liveCatalog is null || _toolGateway is null)
                {
                    return ([], false, evidence.ToArray());
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
                    ToolCatalogVersion: liveCatalog.EffectiveVersion,
                    ParentInvocationId: parentInvocationId);

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
                    throw;
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
                        return ([], false, evidence.ToArray());
                    }
                }

                var toolResult = CreateToolResultContent(toolCall, gatewayResult);
                evidence.Add(new ExpertToolEvidence(
                    subAgentName, toolCall.CallId, toolCall.ToolId,
                    toolResult.Success, DateTimeOffset.UtcNow));
                subAgentMessages.Add(new RuntimeProviderMessage(
                    RuntimeProviderRoles.Tool,
                    [toolResult]));
            }
        }

        // Exceeded max tool rounds
        return ([], false, evidence.ToArray());
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
