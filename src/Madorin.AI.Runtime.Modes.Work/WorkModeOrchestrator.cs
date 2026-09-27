using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Contracts.Work;
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
using Madorin.AI.Runtime.Tools.Builtin.Work;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Modes.Work;

/// <summary>Runs work-mode planning, validated step materialization, and sequential step execution.</summary>
public sealed class WorkModeOrchestrator : IModeOrchestrator
{
    private const string PlanProjectionVersion = "work-plan-v1";
    private const string StepProjectionVersion = "work-step-v2";
    private const string WorkSetPlanToolId = "mcli_work_set_plan";

    private readonly Func<string, IRuntimeProviderAdapter> _providerFactory;
    private WorkPlanDraft? _capturedSetPlanDraft;
    private WorkStepDispatchBridge? _dispatchBridge;
    private DispatchRunContext? _dispatchContext;
    private readonly ConversationStore _store;
    private readonly SqliteSessionRepository _sessionRepo;
    private readonly SqliteWorkRepository _workRepo;
    private readonly SqliteEventOutbox _outbox;
    private readonly AgentContextComposer? _agentContextComposer;
    private readonly bool _emitAccepted;
    private readonly long _startRunSequence;
    private readonly IToolStateStore? _toolStateStore;
    private readonly ToolGateway? _toolGateway;
    private readonly ToolCatalogSnapshot? _toolCatalogSnapshot;
    private readonly Func<ToolCatalogSnapshot>? _liveCatalogProvider;
    private readonly Func<CancellationToken, Task<SkillCatalogSnapshot?>>? _skillCatalogSnapshotProvider;
    private readonly ToolConsentCoordinator? _toolConsentCoordinator;
    private readonly Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse>>?
        _workflowApprovalHandler;
    private readonly Func<CredentialsRefreshRequestedEvent, CancellationToken, Task<bool>>?
        _credentialRefreshHandler;
    private readonly Func<RuntimeSkillScriptInvocation, CancellationToken, Task<RuntimeSkillScriptResult>>?
        _scriptInvoker;
    private readonly RuntimeLimits _runtimeLimits;
    private readonly bool _isHostModeSkills;
    private readonly SkillActivationStore? _skillActivationStore;

    public WorkModeOrchestrator(
        Func<string, IRuntimeProviderAdapter> providerFactory,
        ConversationStore store,
        SqliteSessionRepository sessionRepo,
        SqliteEventOutbox outbox,
        AgentContextComposer? agentContextComposer = null,
        bool emitAccepted = true,
        long startRunSequence = 0,
        IToolStateStore? toolStateStore = null,
        ToolGateway? toolGateway = null,
        ToolCatalogSnapshot? toolCatalogSnapshot = null,
        RuntimeLimits? runtimeLimits = null,
        ToolConsentCoordinator? toolConsentCoordinator = null,
        SqliteWorkRepository? workRepo = null,
        SqliteConnection? connection = null,
        Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse>>?
            workflowApprovalHandler = null,
        Func<CredentialsRefreshRequestedEvent, CancellationToken, Task<bool>>?
            credentialRefreshHandler = null,
        Func<CancellationToken, Task<SkillCatalogSnapshot?>>? skillCatalogSnapshotProvider = null,
        Func<RuntimeSkillScriptInvocation, CancellationToken, Task<RuntimeSkillScriptResult>>? scriptInvoker = null,
        Func<ToolCatalogSnapshot>? liveCatalogProvider = null,
        bool isHostModeSkills = false,
        SkillActivationStore? skillActivationStore = null)
    {
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sessionRepo = sessionRepo ?? throw new ArgumentNullException(nameof(sessionRepo));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _agentContextComposer = agentContextComposer;
        _emitAccepted = emitAccepted;
        _startRunSequence = startRunSequence;
        _toolStateStore = toolStateStore;
        _toolGateway = toolGateway;
        _toolCatalogSnapshot = toolCatalogSnapshot;
        _liveCatalogProvider = liveCatalogProvider;
        _toolConsentCoordinator = toolConsentCoordinator;
        _workflowApprovalHandler = workflowApprovalHandler
            ?? (toolConsentCoordinator is { CanRequestApproval: true }
                ? toolConsentCoordinator.RequestWorkflowApprovalAsync
                : null);
        _credentialRefreshHandler = credentialRefreshHandler;
        _skillCatalogSnapshotProvider = skillCatalogSnapshotProvider;
        _scriptInvoker = scriptInvoker;
        _isHostModeSkills = isHostModeSkills;
        _skillActivationStore = skillActivationStore;
        _runtimeLimits = runtimeLimits ?? new RuntimeLimits(
            MaxToolResultBytesPerInvocation: 4 * 1024 * 1024);
        if (workRepo is not null)
        {
            _workRepo = workRepo;
        }
        else if (connection is not null)
        {
            _workRepo = new SqliteWorkRepository(connection);
        }
        else
        {
            // Best-effort: session repository owns the same connection for V1 wiring.
            _workRepo = new SqliteWorkRepository(sessionRepo.Connection);
        }
    }

    public RuntimeMode Mode => RuntimeMode.Work;

    /// <summary>绑定本轮 Run 的 dispatch_step 同步调度桥。</summary>
    public void AttachDispatchBridge(WorkStepDispatchBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        _dispatchBridge = bridge;
    }

    /// <summary>进程内入队用户插话；完整 JSON-RPC run.interrupt 仍属 Run Control 范围。</summary>
    public Task<WorkPendingUserInput> EnqueueUserInputAsync(
        string sessionId,
        string contentText,
        string source = "user",
        string? inputId = null,
        CancellationToken ct = default) =>
        _workRepo.EnqueuePendingUserInputAsync(sessionId, contentText, source, inputId, ct);

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
        ValidateWorkRequest(request);

        var options = (WorkModeOptions)request.Selection.ModeOptions;
        var policy = options.EffectiveWorkflowPolicy;
        policy.Validate();
        _dispatchContext = new DispatchRunContext(
            runId,
            sessionId,
            runtimeInstanceId,
            request,
            options,
            new RunSequenceAllocator(_startRunSequence));
        if (_dispatchBridge is not null)
        {
            _dispatchBridge.Bind(HandleDispatchStepAsync);
        }

        try
        {
        await foreach (var envelope in RunCoreAsync(runId, sessionId, request, runtimeInstanceId, options, policy, ct)
            .ConfigureAwait(false))
        {
            yield return envelope;
        }
        }
        finally
        {
            _dispatchBridge?.Unbind();
            _dispatchContext = null;
        }
    }

    private async IAsyncEnumerable<RuntimeEventEnvelope> RunCoreAsync(
        string runId,
        string sessionId,
        NewSessionRunRequest request,
        string runtimeInstanceId,
        WorkModeOptions options,
        WorkflowPolicy policy,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var sequence = _dispatchContext?.Sequence
            ?? new RunSequenceAllocator(_startRunSequence);
        var goal = ExtractGoalText(request.InitialInput);
        var fallbackStepId = $"{runId}:step-1";
        var legacyInputHash = ComputeLegacyStepInputHash(request.InitialInput);
        var completedEarly = await _sessionRepo.GetCompletedWorkStepResultAsync(
            fallbackStepId,
            "1",
            legacyInputHash,
            ct).ConfigureAwait(false);
        if (completedEarly is not null)
        {
            if (_emitAccepted)
            {
                yield return await CreateAcceptedEnvelopeAsync(
                    runId, sessionId, runtimeInstanceId, sequence.Take(), ct).ConfigureAwait(false);
            }

            var replayAgent = options.AvailableAgents[0];
            var replayInvocation = await CreateInvocationRequestAsync(
                runId, sessionId, request, replayAgent, StepProjectionVersion, ct,
                stepGoal: goal).ConfigureAwait(false);
            yield return await CreateInvocationStartedEnvelopeAsync(
                replayInvocation, runtimeInstanceId, sequence.Take(), ct).ConfigureAwait(false);
            var cached = JsonSerializer.Deserialize(
                completedEarly,
                RuntimeJsonContext.Default.TextContentBlock)
                ?? throw new InvalidDataException(
                    $"Work step '{fallbackStepId}' has an empty persisted result.");
            if (cached.Text.Length > 0)
            {
                yield return await CreateTextDeltaEnvelopeAsync(
                    runId, runtimeInstanceId, replayInvocation.InvocationId, cached.Text,
                    sequence.Take(), ct).ConfigureAwait(false);
            }

            await EnsureWorkStepToolIntentsTerminalAsync(
                sessionId,
                fallbackStepId,
                "1",
                ct).ConfigureAwait(false);
            yield return await CreateInvocationCompletedEnvelopeAsync(
                runId, runtimeInstanceId, replayInvocation.InvocationId, sequence.Take(), ct)
                .ConfigureAwait(false);
            await CompleteRunAsync(runId, ct).ConfigureAwait(false);
            var replayCompleted = await CreateCompletedEnvelopeAsync(
                runId, sessionId, runtimeInstanceId, sequence.Take(), ct).ConfigureAwait(false);
            if (replayCompleted is not null)
            {
                yield return replayCompleted;
            }
            yield break;
        }

        var priorResume = await _workRepo.GetResumeStateAsync(sessionId, ct).ConfigureAwait(false);
        var publishedPlanVersion = await _workRepo
            .GetLatestPublishedPlanVersionAsync(sessionId, ct)
            .ConfigureAwait(false);
        var priorSessionStatus = priorResume?.Status;
        var waitingForPlanConfirmation = priorSessionStatus
            is WorkSessionStatus.WaitingForPlanConfirmation;

        // 规划轮先消费 FIFO 插话，再决定确认/修订分支。
        var consumedPendingInputs = await _workRepo
            .ConsumePendingUserInputsAsync(sessionId, runId, ct: ct)
            .ConfigureAwait(false);
        var pendingTexts = consumedPendingInputs
            .Select(static item => item.ContentText)
            .ToArray();
        var planningGoal = CombineGoalWithPendingInputs(goal, pendingTexts);
        var userConfirmsPlan = IsExplicitPlanConfirmation(planningGoal)
            || pendingTexts.Any(IsExplicitPlanConfirmation);
        var userRequestsRevision = IsPlanRevisionRequest(planningGoal)
            || pendingTexts.Any(IsPlanRevisionRequest)
            || ContainsPlanJson(planningGoal)
            || pendingTexts.Any(ContainsPlanJson);
        await _workRepo.BeginPlanningTurnAsync(
            sessionId,
            runId,
            options.GeneralManager.AgentId,
            policy,
            options.EffectiveContextPolicy,
            ct).ConfigureAwait(false);

        if (_emitAccepted)
        {
            yield return await CreateAcceptedEnvelopeAsync(
                runId, sessionId, runtimeInstanceId, sequence.Take(), ct).ConfigureAwait(false);
        }

        WorkPlanDraft plan;
        string? parentInvocationId = null;
        var skipManagerPlanning = policy.RequirePlanConfirmation
            && waitingForPlanConfirmation
            && userConfirmsPlan
            && !userRequestsRevision
            && pendingTexts.Length == 0;

        if (skipManagerPlanning)
        {
            plan = await _workRepo.GetCurrentPlanDraftAsync(sessionId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Session '{sessionId}' is waiting for plan confirmation but has no published plan.");
            await _workRepo.UpdateSessionStatusAsync(
                sessionId,
                WorkSessionStatus.Executing,
                ct).ConfigureAwait(false);
            yield return await CreateStatusEnvelopeAsync(
                runtimeInstanceId,
                runId,
                sequence.Take(),
                RunStatus.Running,
                ct,
                workPhase: "executing").ConfigureAwait(false);
        }
        else
        {
            _capturedSetPlanDraft = null;
            var managerInvocation = await CreateInvocationRequestAsync(
                runId,
                sessionId,
                request,
                options.GeneralManager,
                PlanProjectionVersion,
                ct,
                publishedPlanVersion: publishedPlanVersion,
                priorResume: priorResume,
                pendingUserInputs: pendingTexts)
                .ConfigureAwait(false);
            parentInvocationId = managerInvocation.InvocationId;
            if (!string.IsNullOrWhiteSpace(planningGoal))
            {
                await AppendPlainDialogueAsync(
                    sessionId,
                    managerInvocation.InvocationId,
                    "user",
                    "user",
                    planningGoal,
                    ct).ConfigureAwait(false);
            }

            yield return await CreateInvocationStartedEnvelopeAsync(
                managerInvocation, runtimeInstanceId, sequence.Take(), ct).ConfigureAwait(false);

            var managerText = new StringBuilder();
            await foreach (var providerEvent in ExecuteInvocationAsync(managerInvocation, ct)
                .ConfigureAwait(false))
            {
                switch (providerEvent)
                {
                    case TextDeltaProviderEvent textDelta:
                        managerText.Append(textDelta.Delta);
                        yield return await CreateTextDeltaEnvelopeAsync(
                            runId, runtimeInstanceId, textDelta.InvocationId, textDelta.Delta,
                            sequence.Take(), ct).ConfigureAwait(false);
                        break;
                    case ReasoningDeltaProviderEvent reasoningDelta:
                        yield return await CreateReasoningDeltaEnvelopeAsync(
                            runId, runtimeInstanceId, reasoningDelta.InvocationId, reasoningDelta.Delta,
                            sequence.Take(), ct).ConfigureAwait(false);
                        break;
                    case ReasoningCompletedProviderEvent reasoningCompleted:
                        yield return await CreateReasoningCompletedEnvelopeAsync(
                            runId, runtimeInstanceId, reasoningCompleted.InvocationId,
                            sequence.Take(), ct).ConfigureAwait(false);
                        break;
                    case ToolCallDeltaProviderEvent toolCallDelta:
                        yield return await CreateToolCallDeltaEnvelopeAsync(
                            runId, runtimeInstanceId, toolCallDelta.InvocationId, toolCallDelta.CallId,
                            toolCallDelta.Name, toolCallDelta.ArgumentsDelta,
                            sequence.Take(), ct).ConfigureAwait(false);
                        break;
                    case UsageUpdatedProviderEvent usage:
                        yield return await CreateUsageUpdatedEnvelopeAsync(
                            runId, runtimeInstanceId, usage.InvocationId, usage.InputTokens, usage.OutputTokens,
                            sequence.Take(), ct).ConfigureAwait(false);
                        break;
                    case InvocationFailedProviderEvent failed:
                        await _workRepo.UpdateSessionStatusAsync(
                            sessionId, WorkSessionStatus.Failed, ct).ConfigureAwait(false);
                        throw new InvalidOperationException(
                            $"General-manager invocation '{failed.InvocationId}' failed: {failed.Error.Message}");
                    case WorkRunStatusProviderEvent statusChanged:
                        yield return await CreateStatusEnvelopeAsync(
                            runtimeInstanceId,
                            runId,
                            sequence.Take(),
                            statusChanged.Status,
                            ct,
                            workPhase: "planning").ConfigureAwait(false);
                        break;
                }
            }

            yield return await CreateInvocationCompletedEnvelopeAsync(
                runId, runtimeInstanceId, managerInvocation.InvocationId, sequence.Take(), ct)
                .ConfigureAwait(false);

            // Prefer mcli_work_set_plan tool payload; fall back to bare JSON in manager text.
            var parsedPlan = _capturedSetPlanDraft ?? TryParsePlan(managerText.ToString());
            _capturedSetPlanDraft = null;
            WorkPlanDraft? validatedPlan = null;
            string? validationError = null;
            if (parsedPlan is not null)
            {
                try
                {
                    validatedPlan = WorkPlanValidator.ValidateOrThrow(parsedPlan, options);
                }
                catch (ArgumentException ex)
                {
                    validatedPlan = null;
                    validationError = ex.Message;
                }
            }

            if (validatedPlan is null)
            {
                await AppendPlanningDialogueAsync(
                    sessionId,
                    managerInvocation.InvocationId,
                    options.GeneralManager.AgentId,
                    managerText.ToString(),
                    validationError,
                    ct).ConfigureAwait(false);

                var resumeStatus = waitingForPlanConfirmation
                    ? WorkSessionStatus.WaitingForPlanConfirmation
                    : priorSessionStatus ?? WorkSessionStatus.Planning;
                await _workRepo.UpdateSessionStatusAsync(sessionId, resumeStatus, ct)
                    .ConfigureAwait(false);
                await CompleteRunAsync(runId, ct).ConfigureAwait(false);
                var discussionCompleted = await CreateCompletedEnvelopeAsync(
                    runId, sessionId, runtimeInstanceId, sequence.Current, ct).ConfigureAwait(false);
                if (discussionCompleted is not null)
                {
                    yield return discussionCompleted;
                }

                yield break;
            }

            plan = WorkPlanValidator.NormalizePlanRevision(validatedPlan, publishedPlanVersion);
            plan = WorkPlanValidator.ValidateOrThrow(plan, options);
            if (ShouldKeepPublishedPlan(publishedPlanVersion, priorResume, userRequestsRevision))
            {
                await _workRepo.UpdateSessionStatusAsync(
                    sessionId,
                    policy.RequirePlanConfirmation
                        ? WorkSessionStatus.WaitingForPlanConfirmation
                        : priorSessionStatus ?? WorkSessionStatus.Planning,
                    ct).ConfigureAwait(false);
                await CompleteRunAsync(runId, ct).ConfigureAwait(false);
                var kept = await CreateCompletedEnvelopeAsync(
                    runId, sessionId, runtimeInstanceId, sequence.Current, ct).ConfigureAwait(false);
                if (kept is not null)
                {
                    yield return kept;
                }

                yield break;
            }
            var canonicalMessages = await AppendPlanRevisionAsync(
                sessionId,
                managerInvocation.InvocationId,
                options.GeneralManager.AgentId,
                plan,
                ct).ConfigureAwait(false);
            await _workRepo.SavePlanRevisionAsync(
                sessionId,
                runId,
                options.GeneralManager.AgentId,
                policy,
                options.EffectiveContextPolicy,
                plan,
                canonicalMessages.PlanMessageId,
                canonicalMessages.StepMessageIds,
                ct).ConfigureAwait(false);

            var workPlanEvent = MapToWorkPlanUpdatedEvent(plan);
            var eventJson = JsonSerializer.Serialize(
                workPlanEvent,
                RuntimeJsonContext.Default.WorkPlanUpdatedEvent);
            var gsn = await _outbox.AppendAsync(
                runId,
                sequence.Current,
                MessageTypes.WorkPlanUpdated,
                eventJson,
                ct).ConfigureAwait(false);

            yield return CreateWorkPlanUpdatedEnvelope(
                runtimeInstanceId,
                gsn,
                runId,
                sequence.Take(),
                workPlanEvent);

            // 本轮新发布的计划一律等待下一轮明确确认，不因本轮输入像确认而立刻执行。
            if (policy.RequirePlanConfirmation)
            {
                await _workRepo.UpdateSessionStatusAsync(
                    sessionId,
                    WorkSessionStatus.WaitingForPlanConfirmation,
                    ct).ConfigureAwait(false);
                yield return await CreateStatusEnvelopeAsync(
                    runtimeInstanceId,
                    runId,
                    sequence.Take(),
                    RunStatus.Running,
                    ct,
                    workPhase: "waiting-for-plan-confirmation").ConfigureAwait(false);
                await CompleteRunAsync(runId, ct).ConfigureAwait(false);
                var waitingCompleted = await CreateCompletedEnvelopeAsync(
                    runId, sessionId, runtimeInstanceId, sequence.Current, ct).ConfigureAwait(false);
                if (waitingCompleted is not null)
                {
                    yield return waitingCompleted;
                }

                yield break;
            }
        }

        await _workRepo.UpdateSessionStatusAsync(
            sessionId,
            WorkSessionStatus.Executing,
            ct).ConfigureAwait(false);
        var agentById = options.AvailableAgents.ToDictionary(
            static a => a.AgentId, StringComparer.Ordinal);
        var completedCount = 0;
        var approvedLoopRisks = new HashSet<string>(StringComparer.Ordinal);
        var stoppedForPendingInput = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // 步骤间隙：发现未消费插话则停靠本轮，保留未完成步骤，交下一轮规划消费。
            if (await _workRepo.HasPendingUserInputsAsync(sessionId, ct).ConfigureAwait(false))
            {
                stoppedForPendingInput = true;
                await _workRepo.UpdateSessionStatusAsync(
                    sessionId,
                    WorkSessionStatus.Paused,
                    ct).ConfigureAwait(false);
                yield return await CreateStatusEnvelopeAsync(
                    runtimeInstanceId,
                    runId,
                    sequence.Take(),
                    RunStatus.Running,
                    ct,
                    workPhase: "paused-for-pending-input").ConfigureAwait(false);
                break;
            }

            var steps = await _workRepo.ListStepsAsync(sessionId, plan.PlanVersion, ct)
                .ConfigureAwait(false);
            if (steps.All(static s => s.Status is WorkStepLifecycleStatus.Completed
                    or WorkStepLifecycleStatus.Skipped
                    or WorkStepLifecycleStatus.Failed))
            {
                break;
            }

            var ready = WorkStepScheduler.SelectReadySteps(steps, policy, currentlyRunningCount: 0);
            if (ready.Count == 0)
            {
                if (steps.Any(static s => s.Status == WorkStepLifecycleStatus.Running))
                {
                    throw new InvalidOperationException(
                        "Work scheduler found running steps but no ready continuation.");
                }

                // Check for steps blocked by dependency failures
                var blockedSteps = steps.Where(s =>
                    s.Status == WorkStepLifecycleStatus.Pending &&
                    (s.DependsOn ?? Array.Empty<string>()).Any(dep =>
                        steps.FirstOrDefault(x => string.Equals(x.StepId, dep, StringComparison.Ordinal))?.Status
                            == WorkStepLifecycleStatus.Failed)).ToList();

                foreach (var step in blockedSteps)
                {
                    await _workRepo.MarkStepSkippedAsync(
                        sessionId,
                        step.StepId,
                        step.PlanVersion,
                        "dependency_failed",
                        ct).ConfigureAwait(false);

                    var skippedEvent = new WorkStepSkippedEvent(step.StepId, "dependency_failed");
                    var skippedEventJson = JsonSerializer.Serialize(
                        skippedEvent,
                        RuntimeJsonContext.Default.WorkStepSkippedEvent);
                    var skippedGsn = await _outbox.AppendAsync(
                        runId,
                        sequence.Current,
                        MessageTypes.WorkStepSkipped,
                        skippedEventJson,
                        ct).ConfigureAwait(false);

                    yield return new RuntimeEventEnvelope(
                        runtimeInstanceId,
                        skippedGsn,
                        runId,
                        sequence.Take(),
                        MessageTypes.WorkStepSkipped,
                        DateTimeOffset.UtcNow,
                        JsonSerializer.SerializeToElement(
                            skippedEvent,
                            RuntimeJsonContext.Default.WorkStepSkippedEvent));
                    completedCount++;
                }

                // Refresh after skips so terminal-state checks see updated statuses.
                steps = await _workRepo.ListStepsAsync(sessionId, plan.PlanVersion, ct)
                    .ConfigureAwait(false);
                if (steps.All(static s => s.Status is WorkStepLifecycleStatus.Completed
                        or WorkStepLifecycleStatus.Skipped
                        or WorkStepLifecycleStatus.Failed))
                {
                    if (policy.FailurePolicy is WorkStepFailurePolicy.Continue
                        || steps.All(static s => s.Status is WorkStepLifecycleStatus.Completed
                            or WorkStepLifecycleStatus.Skipped))
                    {
                        break;
                    }

                    var terminalFailed = steps.First(static s =>
                        s.Status == WorkStepLifecycleStatus.Failed);
                    await _workRepo.UpdateSessionStatusAsync(
                        sessionId, WorkSessionStatus.Failed, ct).ConfigureAwait(false);
                    throw new InvalidOperationException(
                        $"Work step '{terminalFailed.StepId}' failed: {terminalFailed.ErrorMessage}");
                }

                var failed = steps.FirstOrDefault(static s =>
                    s.Status == WorkStepLifecycleStatus.Failed);
                if (failed is not null
                    && policy.FailurePolicy is not WorkStepFailurePolicy.Continue)
                {
                    await _workRepo.UpdateSessionStatusAsync(
                        sessionId, WorkSessionStatus.Failed, ct).ConfigureAwait(false);
                    throw new InvalidOperationException(
                        $"Work step '{failed.StepId}' failed: {failed.ErrorMessage}");
                }

                if (failed is not null && policy.FailurePolicy is WorkStepFailurePolicy.Continue)
                {
                    // Remaining pending steps may still become ready after skips; re-loop.
                    continue;
                }

                throw new InvalidOperationException(
                    "Work plan has pending steps but no ready steps (possible dependency deadlock).");
            }

            // 同波就绪步骤可并行：Provider/工具环并行，准备与落盘仍串行。
            // Sqlite 各仓储共用 SqliteConnectionSync 连接级闸门，允许挂载 Tool Gateway。
            var executeConcurrently = ready.Count > 1
                && policy.MaxConcurrentSteps > 1
                && policy.FailurePolicy is WorkStepFailurePolicy.Stop
                    or WorkStepFailurePolicy.Continue;

            if (executeConcurrently)
            {
                await foreach (var envelope in ExecuteReadyStepsConcurrentAsync(
                    ready,
                    agentById,
                    plan,
                    steps,
                    request,
                    options,
                    policy,
                    goal,
                    fallbackStepId,
                    legacyInputHash,
                    parentInvocationId,
                    runId,
                    sessionId,
                    runtimeInstanceId,
                    approvedLoopRisks,
                    sequence.Current,
                    ct).ConfigureAwait(false))
                {
                    sequence.CatchUp(envelope.RunSequence + 1);
                    yield return envelope;
                    if (envelope.MessageType is MessageTypes.InvocationCompleted
                        or MessageTypes.InvocationFailed)
                    {
                        completedCount++;
                    }
                }

                if (completedCount > policy.HardMaxStepsPerRun)
                {
                    throw new InvalidOperationException("Work run exceeded hard step limit.");
                }

                continue;
            }

            foreach (var readyStep in ready)
            {
                if (!agentById.TryGetValue(readyStep.TargetAgentId, out var agent))
                {
                    var availableAgents = string.Join(", ", agentById.Keys);
                    throw new InvalidOperationException(
                        $"Step '{readyStep.StepId}' targets unknown agent '{readyStep.TargetAgentId}'. Available agents: [{availableAgents}]");
                }

                var stepInputHash = string.Equals(
                        readyStep.StepId,
                        fallbackStepId,
                        StringComparison.Ordinal)
                    ? legacyInputHash
                    : (string.IsNullOrWhiteSpace(readyStep.StepInputHash)
                        ? WorkPlanValidator.ComputeStepInputHash(
                            readyStep.StepId,
                            readyStep.PlanVersion,
                            readyStep.Goal ?? string.Empty)
                        : readyStep.StepInputHash);

                var completedResult = await _sessionRepo.GetCompletedWorkStepResultAsync(
                    readyStep.StepId, readyStep.PlanVersion, stepInputHash, ct)
                    .ConfigureAwait(false);

                var childInvocation = await CreateInvocationRequestAsync(
                    runId, sessionId, request, agent, StepProjectionVersion, ct,
                    stepGoal: readyStep.Goal ?? goal,
                    parentAgentId: options.GeneralManager.AgentId,
                    parentInvocationId: parentInvocationId,
                    workStepId: readyStep.StepId,
                    workPlanVersion: readyStep.PlanVersion,
                    workPlan: plan,
                    currentStepState: readyStep,
                    planSteps: steps).ConfigureAwait(false);

                if (completedResult is null)
                {
                    var childSnapshotJson = JsonSerializer.Serialize(
                        childInvocation.Snapshot,
                        RuntimeJsonContext.Default.InvocationSnapshot);
                    var checkpointJson = CreateStepCheckpointJson(
                        readyStep,
                        childInvocation.InvocationId,
                        stepInputHash);
                    var started = await _sessionRepo.TryStartWorkStepWithCheckpointAsync(
                        readyStep.StepId,
                        readyStep.PlanVersion,
                        runId,
                        sessionId,
                        readyStep.TargetAgentId,
                        stepInputHash,
                        childInvocation.InvocationId,
                        childInvocation.Snapshot,
                        childSnapshotJson,
                        checkpointJson,
                        ct).ConfigureAwait(false);
                    if (!started)
                    {
                        completedResult = await _sessionRepo.GetCompletedWorkStepResultAsync(
                            readyStep.StepId, readyStep.PlanVersion, stepInputHash, ct)
                            .ConfigureAwait(false);
                        if (completedResult is null)
                        {
                            throw new InvalidOperationException(
                                $"Work step '{readyStep.StepId}' already exists without a reusable result.");
                        }
                    }
                }

                var loopRisk = DetectLoopRisk(readyStep, steps, policy);
                if (completedResult is null
                    && loopRisk is not null
                    && approvedLoopRisks.Add(loopRisk.ApprovalKey))
                {
                    var approved = await RequestWorkflowApprovalAsync(
                        sessionId,
                        runId,
                        readyStep,
                        childInvocation,
                        loopRisk.Reason,
                        ct).ConfigureAwait(false);
                    if (!approved)
                    {
                        await _sessionRepo.FailWorkStepAsync(
                            readyStep.StepId,
                            readyStep.PlanVersion,
                            loopRisk.Reason,
                            ct).ConfigureAwait(false);
                        await _workRepo.UpdateSessionStatusAsync(
                            sessionId,
                            WorkSessionStatus.Failed,
                            ct).ConfigureAwait(false);
                        throw new InvalidOperationException(
                            $"The host denied continuation for work step '{readyStep.StepId}': {loopRisk.Reason}");
                    }
                }

                yield return await CreateInvocationStartedEnvelopeAsync(
                    childInvocation, runtimeInstanceId, sequence.Take(), ct).ConfigureAwait(false);
                if (IsProjectionAdjusted(childInvocation.Snapshot.ContextProjection))
                {
                    yield return await CreateContextProjectionAdjustedEnvelopeAsync(
                        childInvocation,
                        runtimeInstanceId,
                        sequence.Take(),
                        ct).ConfigureAwait(false);
                }

                if (completedResult is not null)
                {
                    var cached = JsonSerializer.Deserialize(
                        completedResult,
                        RuntimeJsonContext.Default.TextContentBlock)
                        ?? throw new InvalidDataException(
                            $"Work step '{readyStep.StepId}' has an empty persisted result.");
                    if (cached.Text.Length > 0)
                    {
                        yield return await CreateTextDeltaEnvelopeAsync(
                            runId, runtimeInstanceId, childInvocation.InvocationId, cached.Text,
                            sequence.Take(), ct).ConfigureAwait(false);
                    }

                    await EnsureWorkStepToolIntentsTerminalAsync(
                        sessionId,
                        readyStep.StepId,
                        readyStep.PlanVersion,
                        ct).ConfigureAwait(false);
                    yield return await CreateInvocationCompletedEnvelopeAsync(
                        runId, runtimeInstanceId, childInvocation.InvocationId, sequence.Take(), ct,
                        workStepId: readyStep.StepId)
                        .ConfigureAwait(false);
                    completedCount++;
                    continue;
                }

                var resultBuilder = new StringBuilder();
                var stepFailed = false;
                RuntimeError? stepError = null;
                await foreach (var providerEvent in ExecuteInvocationAsync(childInvocation, ct)
                    .ConfigureAwait(false))
                {
                    switch (providerEvent)
                    {
                        case TextDeltaProviderEvent textDelta:
                            resultBuilder.Append(textDelta.Delta);
                            yield return await CreateTextDeltaEnvelopeAsync(
                                runId, runtimeInstanceId, textDelta.InvocationId, textDelta.Delta,
                                sequence.Take(), ct).ConfigureAwait(false);
                            break;
                        case ReasoningDeltaProviderEvent reasoningDelta:
                            yield return await CreateReasoningDeltaEnvelopeAsync(
                                runId, runtimeInstanceId, reasoningDelta.InvocationId, reasoningDelta.Delta,
                                sequence.Take(), ct).ConfigureAwait(false);
                            break;
                        case ReasoningCompletedProviderEvent reasoningCompleted:
                            yield return await CreateReasoningCompletedEnvelopeAsync(
                                runId, runtimeInstanceId, reasoningCompleted.InvocationId,
                                sequence.Take(), ct).ConfigureAwait(false);
                            break;
                        case ToolCallDeltaProviderEvent toolCallDelta:
                            yield return await CreateToolCallDeltaEnvelopeAsync(
                                runId, runtimeInstanceId, toolCallDelta.InvocationId, toolCallDelta.CallId,
                                toolCallDelta.Name, toolCallDelta.ArgumentsDelta,
                                sequence.Take(), ct).ConfigureAwait(false);
                            break;
                        case UsageUpdatedProviderEvent usage:
                            yield return await CreateUsageUpdatedEnvelopeAsync(
                                runId, runtimeInstanceId, usage.InvocationId, usage.InputTokens, usage.OutputTokens,
                                sequence.Take(), ct).ConfigureAwait(false);
                            break;
                        case InvocationFailedProviderEvent failed:
                            stepFailed = true;
                            stepError = failed.Error;
                            if (policy.FailurePolicy is WorkStepFailurePolicy.Stop)
                            {
                                await _sessionRepo.FailWorkStepAsync(
                                    readyStep.StepId, readyStep.PlanVersion, failed.Error.Message, ct)
                                    .ConfigureAwait(false);
                                yield return await CreateInvocationFailedEnvelopeAsync(
                                    runId,
                                    runtimeInstanceId,
                                    failed.InvocationId,
                                    failed.Error,
                                    sequence.Take(),
                                    ct,
                                    workStepId: readyStep.StepId).ConfigureAwait(false);
                                await _workRepo.UpdateSessionStatusAsync(
                                    sessionId, WorkSessionStatus.Failed, ct).ConfigureAwait(false);
                                throw new InvalidOperationException(
                                    $"Work-step invocation '{failed.InvocationId}' failed: {failed.Error.Message}");
                            }

                            break;
                        case WorkRunStatusProviderEvent statusChanged:
                            yield return await CreateStatusEnvelopeAsync(
                                runtimeInstanceId,
                                runId,
                                sequence.Take(),
                                statusChanged.Status,
                                ct,
                                workPhase: "executing",
                                currentStep: readyStep.StepId).ConfigureAwait(false);
                            break;
                    }

                    if (stepFailed)
                    {
                        break;
                    }
                }

                if (stepFailed)
                {
                    if (policy.FailurePolicy is WorkStepFailurePolicy.AskHost)
                    {
                        var reason = stepError?.Message ?? "Work step failed.";
                        var approved = await RequestWorkflowApprovalAsync(
                            sessionId,
                            runId,
                            readyStep,
                            childInvocation,
                            reason,
                            ct).ConfigureAwait(false);
                        await _sessionRepo.FailWorkStepAsync(
                            readyStep.StepId,
                            readyStep.PlanVersion,
                            reason,
                            ct).ConfigureAwait(false);
                        if (!approved)
                        {
                            yield return await CreateInvocationFailedEnvelopeAsync(
                                runId,
                                runtimeInstanceId,
                                childInvocation.InvocationId,
                                stepError ?? CreateRuntimeError(
                                    "WorkStepFailed",
                                    reason),
                                sequence.Take(),
                                ct,
                                workStepId: readyStep.StepId).ConfigureAwait(false);
                            await _workRepo.UpdateSessionStatusAsync(
                                sessionId, WorkSessionStatus.Failed, ct).ConfigureAwait(false);
                            throw new InvalidOperationException(
                                $"The host stopped work step '{readyStep.StepId}' after failure: {reason}");
                        }

                        await _workRepo.ResetStepForRetryAsync(
                            readyStep.StepId,
                            readyStep.PlanVersion,
                            reason,
                            ct).ConfigureAwait(false);
                        continue;
                    }

                    if (policy.FailurePolicy is WorkStepFailurePolicy.Retry)
                    {
                        var retryReason = stepError?.Message ?? "Work step failed.";
                        await _sessionRepo.FailWorkStepAsync(
                            readyStep.StepId,
                            readyStep.PlanVersion,
                            retryReason,
                            ct).ConfigureAwait(false);
                        yield return await CreateInvocationFailedEnvelopeAsync(
                            runId,
                            runtimeInstanceId,
                            childInvocation.InvocationId,
                            stepError ?? CreateRuntimeError("WorkStepFailed", retryReason),
                            sequence.Take(),
                            ct,
                            workStepId: readyStep.StepId).ConfigureAwait(false);
                        if (readyStep.AttemptCount < policy.MaxRetriesPerStep)
                        {
                            await _workRepo.ResetStepForRetryAsync(
                                readyStep.StepId,
                                readyStep.PlanVersion,
                                retryReason,
                                ct).ConfigureAwait(false);
                            var retryDelay = ComputeRetryDelay(policy, readyStep.AttemptCount);
                            if (retryDelay > TimeSpan.Zero)
                            {
                                await Task.Delay(retryDelay, ct)
                                    .ConfigureAwait(false);
                            }

                            continue;
                        }

                        await _workRepo.UpdateSessionStatusAsync(
                            sessionId, WorkSessionStatus.Failed, ct).ConfigureAwait(false);
                        throw new InvalidOperationException(
                            $"Work-step invocation '{childInvocation.InvocationId}' failed after {policy.MaxRetriesPerStep} retries: {stepError?.Message}");
                    }

                    // Continue policy: mark the step failed, keep the run going.
                    var continueReason = stepError?.Message ?? "Work step failed.";
                    await _sessionRepo.FailWorkStepAsync(
                        readyStep.StepId,
                        readyStep.PlanVersion,
                        continueReason,
                        ct).ConfigureAwait(false);
                    yield return await CreateInvocationFailedEnvelopeAsync(
                        runId,
                        runtimeInstanceId,
                        childInvocation.InvocationId,
                        stepError ?? CreateRuntimeError("WorkStepFailed", continueReason),
                        sequence.Take(),
                        ct,
                        workStepId: readyStep.StepId).ConfigureAwait(false);
                    completedCount++;
                    continue;
                }

                await EnsureWorkStepToolIntentsTerminalAsync(
                    sessionId,
                    readyStep.StepId,
                    readyStep.PlanVersion,
                    ct).ConfigureAwait(false);
                yield return await CreateInvocationCompletedEnvelopeAsync(
                    runId, runtimeInstanceId, childInvocation.InvocationId, sequence.Take(), ct,
                    workStepId: readyStep.StepId)
                    .ConfigureAwait(false);

                var resultJson = JsonSerializer.Serialize(
                    new TextContentBlock(resultBuilder.ToString()),
                    RuntimeJsonContext.Default.TextContentBlock);
                await _sessionRepo.CompleteWorkStepAsync(
                    readyStep.StepId,
                    readyStep.PlanVersion,
                    stepInputHash,
                    resultJson,
                    ct).ConfigureAwait(false);
                completedCount++;
            }

            if (completedCount > policy.HardMaxStepsPerRun)
            {
                throw new InvalidOperationException("Work run exceeded hard step limit.");
            }
        }

        if (!stoppedForPendingInput)
        {
            await _workRepo.UpdateSessionStatusAsync(
                sessionId, WorkSessionStatus.Completed, ct).ConfigureAwait(false);
        }

        await CompleteRunAsync(runId, ct).ConfigureAwait(false);
        var completed = await CreateCompletedEnvelopeAsync(
            runId, sessionId, runtimeInstanceId, sequence.Current, ct).ConfigureAwait(false);
        if (completed is not null)
        {
            yield return completed;
        }
    }

    /// <summary>
    /// 同波就绪步骤并行执行（含工具环）。步骤准备与结果落盘仍串行；
    /// 仓储写通过连接级闸门串行化，因此可与 Tool Gateway 同用。
    /// </summary>
    private async IAsyncEnumerable<RuntimeEventEnvelope> ExecuteReadyStepsConcurrentAsync(
        IReadOnlyList<WorkStepSnapshot> ready,
        Dictionary<string, AgentRef> agentById,
        WorkPlanDraft plan,
        IReadOnlyList<WorkStepSnapshot> planSteps,
        NewSessionRunRequest request,
        WorkModeOptions options,
        WorkflowPolicy policy,
        string goal,
        string fallbackStepId,
        string legacyInputHash,
        string? parentInvocationId,
        string runId,
        string sessionId,
        string runtimeInstanceId,
        HashSet<string> approvedLoopRisks,
        long runSequence,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var prepared = new List<PreparedConcurrentStep>(ready.Count);
        foreach (var readyStep in ready.OrderBy(static s => s.StepId, StringComparer.Ordinal))
        {
            if (!agentById.TryGetValue(readyStep.TargetAgentId, out var agent))
            {
                throw new InvalidOperationException(
                    $"Step '{readyStep.StepId}' targets unknown agent '{readyStep.TargetAgentId}'.");
            }

            var stepInputHash = string.Equals(
                    readyStep.StepId,
                    fallbackStepId,
                    StringComparison.Ordinal)
                ? legacyInputHash
                : (string.IsNullOrWhiteSpace(readyStep.StepInputHash)
                    ? WorkPlanValidator.ComputeStepInputHash(
                        readyStep.StepId,
                        readyStep.PlanVersion,
                        readyStep.Goal ?? string.Empty)
                    : readyStep.StepInputHash);

            var completedResult = await _sessionRepo.GetCompletedWorkStepResultAsync(
                readyStep.StepId, readyStep.PlanVersion, stepInputHash, ct)
                .ConfigureAwait(false);

            var childInvocation = await CreateInvocationRequestAsync(
                runId, sessionId, request, agent, StepProjectionVersion, ct,
                stepGoal: readyStep.Goal ?? goal,
                parentAgentId: options.GeneralManager.AgentId,
                parentInvocationId: parentInvocationId,
                workStepId: readyStep.StepId,
                workPlanVersion: readyStep.PlanVersion,
                workPlan: plan,
                currentStepState: readyStep,
                planSteps: planSteps).ConfigureAwait(false);

            if (completedResult is null)
            {
                var childSnapshotJson = JsonSerializer.Serialize(
                    childInvocation.Snapshot,
                    RuntimeJsonContext.Default.InvocationSnapshot);
                var checkpointJson = CreateStepCheckpointJson(
                    readyStep,
                    childInvocation.InvocationId,
                    stepInputHash);
                var started = await _sessionRepo.TryStartWorkStepWithCheckpointAsync(
                    readyStep.StepId,
                    readyStep.PlanVersion,
                    runId,
                    sessionId,
                    readyStep.TargetAgentId,
                    stepInputHash,
                    childInvocation.InvocationId,
                    childInvocation.Snapshot,
                    childSnapshotJson,
                    checkpointJson,
                    ct).ConfigureAwait(false);
                if (!started)
                {
                    completedResult = await _sessionRepo.GetCompletedWorkStepResultAsync(
                        readyStep.StepId, readyStep.PlanVersion, stepInputHash, ct)
                        .ConfigureAwait(false);
                    if (completedResult is null)
                    {
                        throw new InvalidOperationException(
                            $"Work step '{readyStep.StepId}' already exists without a reusable result.");
                    }
                }
            }

            var loopRisk = DetectLoopRisk(readyStep, planSteps, policy);
            if (completedResult is null
                && loopRisk is not null
                && approvedLoopRisks.Add(loopRisk.ApprovalKey))
            {
                var approved = await RequestWorkflowApprovalAsync(
                    sessionId,
                    runId,
                    readyStep,
                    childInvocation,
                    loopRisk.Reason,
                    ct).ConfigureAwait(false);
                if (!approved)
                {
                    await _sessionRepo.FailWorkStepAsync(
                        readyStep.StepId,
                        readyStep.PlanVersion,
                        loopRisk.Reason,
                        ct).ConfigureAwait(false);
                    await _workRepo.UpdateSessionStatusAsync(
                        sessionId,
                        WorkSessionStatus.Failed,
                        ct).ConfigureAwait(false);
                    throw new InvalidOperationException(
                        $"The host denied continuation for work step '{readyStep.StepId}': {loopRisk.Reason}");
                }
            }

            yield return await CreateInvocationStartedEnvelopeAsync(
                childInvocation, runtimeInstanceId, runSequence++, ct).ConfigureAwait(false);
            if (IsProjectionAdjusted(childInvocation.Snapshot.ContextProjection))
            {
                yield return await CreateContextProjectionAdjustedEnvelopeAsync(
                    childInvocation,
                    runtimeInstanceId,
                    runSequence++,
                    ct).ConfigureAwait(false);
            }

            prepared.Add(new PreparedConcurrentStep(
                readyStep,
                childInvocation,
                stepInputHash,
                completedResult));
        }

        var providerResults = new ConcurrentDictionary<string, ConcurrentStepProviderResult>(
            StringComparer.Ordinal);
        await Task.WhenAll(prepared.Select(async item =>
        {
            if (item.CompletedResultJson is not null)
            {
                providerResults[item.Step.StepId] = new ConcurrentStepProviderResult(
                    item.CompletedResultJson,
                    Failed: false,
                    Error: null,
                    ProviderEvents: []);
                return;
            }

            var events = new List<RuntimeProviderEvent>();
            RuntimeError? error = null;
            await foreach (var providerEvent in ExecuteInvocationAsync(item.Invocation, ct)
                .ConfigureAwait(false))
            {
                events.Add(providerEvent);
                if (providerEvent is InvocationFailedProviderEvent failed)
                {
                    error = failed.Error;
                }
            }

            providerResults[item.Step.StepId] = new ConcurrentStepProviderResult(
                CompletedResultJson: null,
                Failed: error is not null,
                Error: error,
                ProviderEvents: events);
        })).ConfigureAwait(false);

        foreach (var item in prepared)
        {
            var providerResult = providerResults[item.Step.StepId];
            if (providerResult.CompletedResultJson is not null)
            {
                var cached = JsonSerializer.Deserialize(
                    providerResult.CompletedResultJson,
                    RuntimeJsonContext.Default.TextContentBlock)
                    ?? throw new InvalidDataException(
                        $"Work step '{item.Step.StepId}' has an empty persisted result.");
                if (cached.Text.Length > 0)
                {
                    yield return await CreateTextDeltaEnvelopeAsync(
                        runId,
                        runtimeInstanceId,
                        item.Invocation.InvocationId,
                        cached.Text,
                        runSequence++,
                        ct).ConfigureAwait(false);
                }

                await EnsureWorkStepToolIntentsTerminalAsync(
                    sessionId,
                    item.Step.StepId,
                    item.Step.PlanVersion,
                    ct).ConfigureAwait(false);
                yield return await CreateInvocationCompletedEnvelopeAsync(
                    runId,
                    runtimeInstanceId,
                    item.Invocation.InvocationId,
                    runSequence++,
                    ct,
                    workStepId: item.Step.StepId).ConfigureAwait(false);
                continue;
            }

            var resultBuilder = new StringBuilder();
            foreach (var providerEvent in providerResult.ProviderEvents)
            {
                switch (providerEvent)
                {
                    case TextDeltaProviderEvent textDelta:
                        resultBuilder.Append(textDelta.Delta);
                        yield return await CreateTextDeltaEnvelopeAsync(
                            runId,
                            runtimeInstanceId,
                            textDelta.InvocationId,
                            textDelta.Delta,
                            runSequence++,
                            ct).ConfigureAwait(false);
                        break;
                    case ReasoningDeltaProviderEvent reasoningDelta:
                        yield return await CreateReasoningDeltaEnvelopeAsync(
                            runId,
                            runtimeInstanceId,
                            reasoningDelta.InvocationId,
                            reasoningDelta.Delta,
                            runSequence++,
                            ct).ConfigureAwait(false);
                        break;
                    case ReasoningCompletedProviderEvent reasoningCompleted:
                        yield return await CreateReasoningCompletedEnvelopeAsync(
                            runId,
                            runtimeInstanceId,
                            reasoningCompleted.InvocationId,
                            runSequence++,
                            ct).ConfigureAwait(false);
                        break;
                    case ToolCallDeltaProviderEvent toolCallDelta:
                        yield return await CreateToolCallDeltaEnvelopeAsync(
                            runId,
                            runtimeInstanceId,
                            toolCallDelta.InvocationId,
                            toolCallDelta.CallId,
                            toolCallDelta.Name,
                            toolCallDelta.ArgumentsDelta,
                            runSequence++,
                            ct).ConfigureAwait(false);
                        break;
                    case UsageUpdatedProviderEvent usage:
                        yield return await CreateUsageUpdatedEnvelopeAsync(
                            runId,
                            runtimeInstanceId,
                            usage.InvocationId,
                            usage.InputTokens,
                            usage.OutputTokens,
                            runSequence++,
                            ct).ConfigureAwait(false);
                        break;
                    case WorkRunStatusProviderEvent statusChanged:
                        yield return await CreateStatusEnvelopeAsync(
                            runtimeInstanceId,
                            runId,
                            runSequence++,
                            statusChanged.Status,
                            ct,
                            workPhase: "executing",
                            currentStep: item.Step.StepId).ConfigureAwait(false);
                        break;
                }
            }

            if (providerResult.Failed)
            {
                var reason = providerResult.Error?.Message ?? "Work step failed.";
                await _sessionRepo.FailWorkStepAsync(
                    item.Step.StepId,
                    item.Step.PlanVersion,
                    reason,
                    ct).ConfigureAwait(false);
                yield return await CreateInvocationFailedEnvelopeAsync(
                    runId,
                    runtimeInstanceId,
                    item.Invocation.InvocationId,
                    providerResult.Error ?? CreateRuntimeError("WorkStepFailed", reason),
                    runSequence++,
                    ct,
                    workStepId: item.Step.StepId).ConfigureAwait(false);
                continue;
            }

            await EnsureWorkStepToolIntentsTerminalAsync(
                sessionId,
                item.Step.StepId,
                item.Step.PlanVersion,
                ct).ConfigureAwait(false);
            yield return await CreateInvocationCompletedEnvelopeAsync(
                runId,
                runtimeInstanceId,
                item.Invocation.InvocationId,
                runSequence++,
                ct,
                workStepId: item.Step.StepId).ConfigureAwait(false);

            var resultJson = JsonSerializer.Serialize(
                new TextContentBlock(resultBuilder.ToString()),
                RuntimeJsonContext.Default.TextContentBlock);
            await _sessionRepo.CompleteWorkStepAsync(
                item.Step.StepId,
                item.Step.PlanVersion,
                item.StepInputHash,
                resultJson,
                ct).ConfigureAwait(false);
        }

        if (policy.FailurePolicy is WorkStepFailurePolicy.Stop
            && providerResults.Values.Any(static result => result.Failed))
        {
            var failed = providerResults.First(static pair => pair.Value.Failed);
            await _workRepo.UpdateSessionStatusAsync(
                sessionId, WorkSessionStatus.Failed, ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Work step '{failed.Key}' failed: {failed.Value.Error?.Message ?? "Work step failed."}");
        }
    }

    private sealed record PreparedConcurrentStep(
        WorkStepSnapshot Step,
        AgentInvocationRequest Invocation,
        string StepInputHash,
        string? CompletedResultJson);

    private sealed record ConcurrentStepProviderResult(
        string? CompletedResultJson,
        bool Failed,
        RuntimeError? Error,
        IReadOnlyList<RuntimeProviderEvent> ProviderEvents);

    private async Task CompleteRunAsync(string runId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = await _sessionRepo.GetRunStatusAsync(runId, ct).ConfigureAwait(false);
            switch (current)
            {
                case RunStatus.Completed:
                    return;
                case RunStatus.Failed or RunStatus.Cancelled or RunStatus.Interrupted:
                    throw new InvalidOperationException(
                        $"Work run '{runId}' cannot complete because it is already {current}.");
                case RunStatus.Running:
                    try
                    {
                        await _sessionRepo.TransitionRunStatusAsync(
                            runId,
                            RunStatus.Running,
                            RunStatus.Persisting,
                            ct).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    break;
                case RunStatus.Persisting:
                    return;
                default:
                    throw new InvalidOperationException(
                        $"Work run '{runId}' cannot complete from {current}.");
            }
        }

        throw new InvalidOperationException($"Work run '{runId}' could not be marked Completed.");
    }

    private async Task EnsureWorkStepToolIntentsTerminalAsync(
        string sessionId,
        string workStepId,
        string planVersion,
        CancellationToken ct)
    {
        if (_toolStateStore is null)
        {
            return;
        }

        var recoverable = await _toolStateStore.ListRecoverableWorkStepIntentsAsync(
            sessionId,
            workStepId,
            planVersion,
            ct).ConfigureAwait(false);
        if (recoverable.Count == 0)
        {
            return;
        }

        var sample = string.Join(
            ", ",
            recoverable
                .Take(5)
                .Select(static intent => $"{intent.CallId}:{intent.Status}"));
        throw new InvalidOperationException(
            $"Work step '{workStepId}' cannot be completed because {recoverable.Count} tool intent(s) are not terminal: {sample}.");
    }

    private void TryCaptureSetPlanFromToolOutput(string outputJson)
    {
        try
        {
            using var document = JsonDocument.Parse(outputJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ok", out var ok)
                || ok.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("plan", out var planElement)
                || planElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var plan = JsonSerializer.Deserialize(
                planElement.GetRawText(),
                RuntimeJsonContext.Default.WorkPlanDraft);
            if (plan is not null)
            {
                _capturedSetPlanDraft = plan;
            }
        }
        catch (JsonException)
        {
            // Keep planning on text fallback when tool output is malformed.
        }
    }

    private static WorkPlanDraft? TryParsePlan(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var candidate = text.Trim();
        var start = candidate.IndexOf('{');
        var end = candidate.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        candidate = candidate[start..(end + 1)];
        try
        {
            return JsonSerializer.Deserialize(candidate, RuntimeJsonContext.Default.WorkPlanDraft);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ExtractGoalText(ContentBlock[] input)
    {
        var sb = new StringBuilder();
        foreach (var block in input)
        {
            if (block is TextContentBlock text && text.Text.Length > 0)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(text.Text);
            }
        }

        return sb.Length == 0 ? "Complete the assigned work." : sb.ToString();
    }

    private async Task AppendPlanningDialogueAsync(
        string sessionId,
        string invocationId,
        string agentId,
        string assistantText,
        string? validationError,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(assistantText))
        {
            await AppendPlainDialogueAsync(sessionId, invocationId, agentId, "assistant", assistantText, ct)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(validationError))
        {
            await AppendPlainDialogueAsync(
                sessionId,
                invocationId,
                "runtime",
                "tool",
                "Plan validation failed: " + validationError
                + " Correct the plan with mcli_work_set_plan. Do not explain this failure to the user.",
                ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 已发布计划尚无完成结果，且用户没有要求修订时，保持原计划。
    /// </summary>
    private static bool ShouldKeepPublishedPlan(
        string? publishedPlanVersion,
        WorkResumeState? priorResume,
        bool userRequestsRevision)
    {
        if (userRequestsRevision || string.IsNullOrWhiteSpace(publishedPlanVersion))
        {
            return false;
        }

        var steps = priorResume?.Steps;
        return steps is { Length: > 0 }
            && steps.All(static step => step.Status is WorkStepLifecycleStatus.Pending
                or WorkStepLifecycleStatus.Interrupted);
    }

    private async Task AppendPlainDialogueAsync(
        string sessionId,
        string invocationId,
        string agentId,
        string role,
        string text,
        CancellationToken ct)
    {
        var content = JsonSerializer.SerializeToElement(
            new ContentBlock[] { new TextContentBlock(text) },
            RuntimeJsonContext.Default.ContentBlockArray);
        await _store.AppendMessageAsync(
            sessionId,
            Mode.ToString(),
            new ConversationMessageDraft(
                invocationId,
                agentId,
                role,
                content,
                DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);
    }

    private async Task<string> ReadPlanningTranscriptTailAsync(string sessionId, CancellationToken ct)
    {
        var records = await _store.ReadAllAsync(sessionId, ct).ConfigureAwait(false);
        if (records.Count == 0)
        {
            return string.Empty;
        }

        var lines = new List<string>();
        for (var index = records.Count - 1; index >= 0 && lines.Count < 8; index--)
        {
            var record = records[index];
            if (IsCanonicalPlanBody(record))
            {
                continue;
            }

            var text = ExtractDialogueText(record.Content);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (text.Length > 800)
            {
                text = text[..800] + "...";
            }

            lines.Add($"[{record.Role}] {text}");
        }

        lines.Reverse();
        return string.Join('\n', lines);
    }

    private static bool IsCanonicalPlanBody(ConversationRecordV1 record)
    {
        if (record.SummaryMetadata is not { ValueKind: JsonValueKind.Object } metadata
            || !metadata.TryGetProperty("kind", out var kind)
            || kind.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = kind.GetString();
        return value is "work.plan" or "work.step";
    }

    private static string ExtractDialogueText(JsonElement content)
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
            return string.Empty;
        }
    }

    private async Task<(string PlanMessageId, IReadOnlyDictionary<string, string> StepMessageIds)>
        AppendPlanRevisionAsync(
            string sessionId,
            string managerInvocationId,
            string managerAgentId,
            WorkPlanDraft plan,
            CancellationToken ct)
    {
        var planJson = JsonSerializer.Serialize(plan, RuntimeJsonContext.Default.WorkPlanDraft);
        var planRecord = await AppendCanonicalWorkBodyAsync(
            sessionId,
            managerInvocationId,
            managerAgentId,
            planJson,
            kind: "work.plan",
            plan.PlanVersion,
            stepId: null,
            ct).ConfigureAwait(false);

        var stepMessageIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in plan.Steps)
        {
            var stepJson = JsonSerializer.Serialize(
                step,
                RuntimeJsonContext.Default.WorkPlanStepDraft);
            var stepRecord = await AppendCanonicalWorkBodyAsync(
                sessionId,
                managerInvocationId,
                managerAgentId,
                stepJson,
                kind: "work.step",
                plan.PlanVersion,
                step.StepId,
                ct).ConfigureAwait(false);
            stepMessageIds.Add(step.StepId, stepRecord.MessageId);
        }

        return (planRecord.MessageId, stepMessageIds);
    }

    private async Task<ConversationRecordV1> AppendCanonicalWorkBodyAsync(
        string sessionId,
        string invocationId,
        string agentId,
        string bodyJson,
        string kind,
        string planVersion,
        string? stepId,
        CancellationToken ct)
    {
        var content = JsonSerializer.SerializeToElement(
            new ContentBlock[] { new TextContentBlock(bodyJson) },
            RuntimeJsonContext.Default.ContentBlockArray);
        var metadata = CreateWorkMessageMetadata(kind, planVersion, stepId);
        return await _store.AppendMessageAsync(
            sessionId,
            Mode.ToString(),
            new ConversationMessageDraft(
                invocationId,
                agentId,
                "assistant",
                content,
                DateTimeOffset.UtcNow,
                SummaryMetadata: metadata),
            ct).ConfigureAwait(false);
    }

    private static JsonElement CreateWorkMessageMetadata(
        string kind,
        string planVersion,
        string? stepId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", kind);
            writer.WriteString("planVersion", planVersion);
            if (stepId is not null)
            {
                writer.WriteString("stepId", stepId);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static string CreateStepCheckpointJson(
        WorkStepSnapshot step,
        string invocationId,
        string stepInputHash)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "work.step.running");
            writer.WriteString("stepId", step.StepId);
            writer.WriteString("planVersion", step.PlanVersion);
            writer.WriteString("invocationId", invocationId);
            writer.WriteString("stepInputHash", stepInputHash);
            writer.WriteString("stepMessageId", step.StepMessageId);
            writer.WriteStartArray("dependsOn");
            foreach (var dependency in step.DependsOn ?? [])
            {
                writer.WriteStringValue(dependency);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private async Task<bool> RequestWorkflowApprovalAsync(
        string sessionId,
        string runId,
        WorkStepSnapshot step,
        AgentInvocationRequest invocation,
        string reason,
        CancellationToken ct)
    {
        var approvalRequestId = CreateWorkflowApprovalRequestId(
            runId,
            step.StepId,
            step.PlanVersion,
            step.AttemptCount + 1,
            reason);
        var correlationId = Guid.NewGuid().ToString("N");
        var request = new ApprovalRequest(
            correlationId,
            approvalRequestId,
            $"work:{step.StepId}:{step.AttemptCount + 1}",
            runId,
            invocation.AgentId,
            invocation.ParentAgentId,
            "work.failure-policy",
            ComputeHash(reason),
            ToolRiskLevel.Write,
            $"work-step:{step.StepId}",
            reason,
            sessionId,
            invocation.InvocationId,
            invocation.Snapshot.ParentInvocationId,
            step.StepId,
            step.PlanVersion);
        var requestJson = JsonSerializer.Serialize(
            request,
            RuntimeJsonContext.Default.ApprovalRequest);
        await _workRepo.MarkStepWaitingForApprovalAsync(
            sessionId,
            step.StepId,
            step.PlanVersion,
            approvalRequestId,
            requestJson,
            ct).ConfigureAwait(false);

        if (_workflowApprovalHandler is null)
        {
            await _workRepo.MarkRequiresManualInterventionAsync(
                sessionId,
                step.StepId,
                step.PlanVersion,
                "Workflow approval is required but the host approval channel is unavailable.",
                ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                "Workflow approval is required but the host approval channel is unavailable.");
        }

        var response = await _workflowApprovalHandler(request, ct)
            .ConfigureAwait(false);
        if (!string.Equals(response.CorrelationId, correlationId, StringComparison.Ordinal)
            || !string.Equals(response.ApprovalRequestId, approvalRequestId, StringComparison.Ordinal))
        {
            await _workRepo.MarkRequiresManualInterventionAsync(
                sessionId,
                step.StepId,
                step.PlanVersion,
                "The host returned a workflow approval response for another request.",
                ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                "The host returned a workflow approval response for another request.");
        }

        await _workRepo.MarkStepRunningAfterApprovalAsync(
            sessionId,
            step.StepId,
            step.PlanVersion,
            approvalRequestId,
            ct).ConfigureAwait(false);
        return response.Decision is ToolAuthorizationDecision.Granted;
    }

    private static WorkLoopRisk? DetectLoopRisk(
        WorkStepSnapshot current,
        IReadOnlyList<WorkStepSnapshot> allSteps,
        WorkflowPolicy policy)
    {
        if (policy.LoopDetection is WorkLoopDetection.Disabled
            || !policy.RequireHumanConfirmationOnLoop)
        {
            return null;
        }

        if (current.Depth >= policy.MaxAgentDepth)
        {
            return new WorkLoopRisk(
                $"depth:{current.StepId}:{current.PlanVersion}",
                $"Step '{current.StepId}' reached the configured Agent depth threshold {policy.MaxAgentDepth}.");
        }

        var repeatedGoal = allSteps
            .Where(step => string.Equals(step.TargetAgentId, current.TargetAgentId, StringComparison.Ordinal)
                && string.Equals(
                    NormalizeGoal(step.Goal),
                    NormalizeGoal(current.Goal),
                    StringComparison.Ordinal))
            .OrderBy(static step => step.StepId, StringComparer.Ordinal)
            .ToArray();
        if (repeatedGoal.Length > 1
            && !string.Equals(repeatedGoal[0].StepId, current.StepId, StringComparison.Ordinal))
        {
            return new WorkLoopRisk(
                $"goal:{current.TargetAgentId}:{NormalizeGoal(current.Goal)}",
                $"Step '{current.StepId}' repeats the same target and normalized goal without a distinct objective.");
        }

        if (current.AttemptCount > Math.Max(1, policy.MaxRetriesPerStep))
        {
            return new WorkLoopRisk(
                $"progress:{current.StepId}:{current.PlanVersion}:{current.AttemptCount}",
                $"Step '{current.StepId}' is being attempted again without a completed output.");
        }

        return null;
    }

    private static TimeSpan ComputeRetryDelay(WorkflowPolicy policy, int completedAttempts)
    {
        if (policy.RetryBackoffMilliseconds <= 0)
        {
            return TimeSpan.Zero;
        }

        var exponent = Math.Clamp(completedAttempts, 0, 6);
        var milliseconds = Math.Min(
            60_000L,
            checked((long)policy.RetryBackoffMilliseconds * (1L << exponent)));
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static string CreateWorkflowApprovalRequestId(
        string runId,
        string stepId,
        string planVersion,
        int attemptNumber,
        string reason)
    {
        var identity = $"{runId}\n{stepId}\n{planVersion}\n{attemptNumber}\n{reason}";
        return $"work-approval-{Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)))}";
    }

    private static string NormalizeGoal(string? goal) =>
        string.Join(
            ' ',
            (goal ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();

    private sealed record WorkLoopRisk(string ApprovalKey, string Reason);

    private sealed record WorkRunStatusProviderEvent(
        string InvocationId,
        RunStatus Status) : RuntimeProviderEvent(InvocationId);

    private Task<RuntimeEventEnvelope> CreateAcceptedEnvelopeAsync(
        string runId, string sessionId, string runtimeInstanceId, long runSequence, CancellationToken ct) =>
        CreateEnvelopeAsync(
            runtimeInstanceId, runId, runSequence, MessageTypes.RunAccepted,
            new RunAcceptedEvent(sessionId, runId), RuntimeJsonContext.Default.RunAcceptedEvent, ct);

    private async Task<RuntimeEventEnvelope?> CreateCompletedEnvelopeAsync(
        string runId,
        string sessionId,
        string runtimeInstanceId,
        long runSequence,
        CancellationToken ct)
    {
        var value = new RunCompletedEvent(runId, sessionId);
        var payload = JsonSerializer.SerializeToElement(
            value,
            RuntimeJsonContext.Default.RunCompletedEvent);
        var gsn = await _sessionRepo.TryCommitTerminalRunAsync(
            runId,
            RunStatus.Completed,
            null,
            null,
            MessageTypes.RunCompleted,
            payload.GetRawText(),
            ct).ConfigureAwait(false);
        return gsn is null
            ? null
            : new RuntimeEventEnvelope(
                runtimeInstanceId,
                gsn.Value,
                runId,
                runSequence,
                MessageTypes.RunCompleted,
                DateTimeOffset.UtcNow,
                payload);
    }

    private Task<RuntimeEventEnvelope> CreateStatusEnvelopeAsync(
        string runtimeInstanceId,
        string runId,
        long runSequence,
        RunStatus status,
        CancellationToken ct,
        string? workPhase = null,
        string? currentStep = null) =>
        CreateEnvelopeAsync(
            runtimeInstanceId,
            runId,
            runSequence,
            MessageTypes.RunStatusChanged,
            new RunStatusChangedEvent(runId, status.ToString())
            {
                Mode = RuntimeMode.Work,
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
                },
                WorkPhase = workPhase,
                CurrentStep = currentStep
            },
            RuntimeJsonContext.Default.RunStatusChangedEvent,
            ct);

    private async Task<RuntimeEventEnvelope> CreateInvocationStartedEnvelopeAsync(
        AgentInvocationRequest request,
        string runtimeInstanceId,
        long runSequence,
        CancellationToken ct)
    {
        var snapshotJson = JsonSerializer.Serialize(
            request.Snapshot,
            RuntimeJsonContext.Default.InvocationSnapshot);
        await _sessionRepo.SaveInvocationSnapshotAsync(
            request.RunId,
            request.SessionId,
            request.Snapshot,
            snapshotJson,
            ct).ConfigureAwait(false);
        return await CreateEnvelopeAsync(
            runtimeInstanceId,
            request.RunId,
            runSequence,
            MessageTypes.InvocationStarted,
            new InvocationStartedEvent(request.InvocationId, request.Snapshot),
            RuntimeJsonContext.Default.InvocationStartedEvent,
            ct).ConfigureAwait(false);
    }

    private Task<RuntimeEventEnvelope> CreateInvocationCompletedEnvelopeAsync(
        string runId, string runtimeInstanceId, string invocationId, long runSequence, CancellationToken ct,
        string? workStepId = null) =>
        CreateEnvelopeAsync(
            runtimeInstanceId, runId, runSequence, MessageTypes.InvocationCompleted,
            new InvocationCompletedEvent(invocationId, workStepId),
            RuntimeJsonContext.Default.InvocationCompletedEvent, ct);

    private Task<RuntimeEventEnvelope> CreateContextProjectionAdjustedEnvelopeAsync(
        AgentInvocationRequest request,
        string runtimeInstanceId,
        long runSequence,
        CancellationToken ct)
    {
        var projection = request.Snapshot.ContextProjection
            ?? throw new InvalidOperationException(
                $"Invocation '{request.InvocationId}' has no context-projection snapshot.");
        return CreateEnvelopeAsync(
            runtimeInstanceId,
            request.RunId,
            runSequence,
            MessageTypes.ContextProjectionAdjusted,
            new ContextProjectionAdjustedEvent(
                request.InvocationId,
                projection.DroppedMessageCount,
                projection.IncludedMessageCount,
                projection.EstimatedTokens,
                projection.EstimateSource,
                projection.Strategy,
                projection.RetainedItems,
                projection.TokenLimit,
                projection.SummarizedUnitCount),
            RuntimeJsonContext.Default.ContextProjectionAdjustedEvent,
            ct);
    }

    private static bool IsProjectionAdjusted(ContextProjectionSnapshot? projection) =>
        projection is not null
        && (projection.DroppedMessageCount > 0
            || projection.SummarizedUnitCount > 0
            || !string.Equals(projection.Strategy, "Full", StringComparison.Ordinal));

    private Task<RuntimeEventEnvelope> CreateInvocationFailedEnvelopeAsync(
        string runId,
        string runtimeInstanceId,
        string invocationId,
        RuntimeError error,
        long runSequence,
        CancellationToken ct,
        string? workStepId = null) =>
        CreateEnvelopeAsync(
            runtimeInstanceId, runId, runSequence, MessageTypes.InvocationFailed,
            new InvocationFailedEvent(invocationId, error, workStepId),
            RuntimeJsonContext.Default.InvocationFailedEvent,
            ct);

    private Task<RuntimeEventEnvelope> CreateTextDeltaEnvelopeAsync(
        string runId, string runtimeInstanceId, string invocationId, string delta,
        long runSequence, CancellationToken ct) =>
        CreateEnvelopeAsync(
            runtimeInstanceId, runId, runSequence, MessageTypes.TextDelta,
            new TextDeltaEvent(invocationId, delta), RuntimeJsonContext.Default.TextDeltaEvent, ct);

    private Task<RuntimeEventEnvelope> CreateReasoningDeltaEnvelopeAsync(
        string runId, string runtimeInstanceId, string invocationId, string delta,
        long runSequence, CancellationToken ct) =>
        CreateEnvelopeAsync(
            runtimeInstanceId, runId, runSequence, MessageTypes.ReasoningDelta,
            new ReasoningDeltaEvent(invocationId, delta), RuntimeJsonContext.Default.ReasoningDeltaEvent, ct);

    private Task<RuntimeEventEnvelope> CreateReasoningCompletedEnvelopeAsync(
        string runId, string runtimeInstanceId, string invocationId,
        long runSequence, CancellationToken ct) =>
        CreateEnvelopeAsync(
            runtimeInstanceId, runId, runSequence, MessageTypes.ReasoningCompleted,
            new ReasoningCompletedEvent(invocationId), RuntimeJsonContext.Default.ReasoningCompletedEvent, ct);

    private Task<RuntimeEventEnvelope> CreateToolCallDeltaEnvelopeAsync(
        string runId, string runtimeInstanceId, string invocationId, string callId, string name, string delta,
        long runSequence, CancellationToken ct) =>
        CreateEnvelopeAsync(
            runtimeInstanceId, runId, runSequence, MessageTypes.ToolCallDelta,
            new ToolCallDeltaEvent(invocationId, callId, name, delta), RuntimeJsonContext.Default.ToolCallDeltaEvent, ct);

    private Task<RuntimeEventEnvelope> CreateUsageUpdatedEnvelopeAsync(
        string runId, string runtimeInstanceId, string invocationId, int? inputTokens, int? outputTokens,
        long runSequence, CancellationToken ct) =>
        CreateEnvelopeAsync(
            runtimeInstanceId, runId, runSequence, MessageTypes.UsageUpdated,
            new UsageUpdatedEvent(invocationId, inputTokens, outputTokens), RuntimeJsonContext.Default.UsageUpdatedEvent, ct);

    private async Task<RuntimeEventEnvelope> CreateEnvelopeAsync<T>(
        string runtimeInstanceId, string runId, long runSequence, string messageType,
        T value, JsonTypeInfo<T> typeInfo, CancellationToken ct)
        where T : notnull
    {
        var payload = JsonSerializer.SerializeToElement(value, typeInfo);
        var gsn = await _outbox.AppendAsync(runId, runSequence, messageType, payload.GetRawText(), ct)
            .ConfigureAwait(false);
        return new RuntimeEventEnvelope(
            runtimeInstanceId, gsn, runId, runSequence, messageType, DateTimeOffset.UtcNow, payload);
    }

    private async IAsyncEnumerable<RuntimeProviderEvent> ExecuteInvocationAsync(
        AgentInvocationRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateToolCallLimit(_runtimeLimits.MaxToolCallsPerRound);
        var provider = _providerFactory(request.ProviderId)
            ?? throw new InvalidOperationException($"Provider '{request.ProviderId}' was not found.");

        var messages = request.Messages.ToList();
        var tools = request.AvailableTools;
        var maxToolRounds = _runtimeLimits.MaxToolRounds;
        var maxToolCallsPerRound = _runtimeLimits.MaxToolCallsPerRound;
        var maxToolResultBytes = _runtimeLimits.MaxToolResultBytesPerInvocation
            ?? 4L * 1024 * 1024;
        long totalToolResultBytes = 0;
        var hasIrreversibleToolSideEffects = false;

        for (var toolRound = 0; ; toolRound++)
        {
            var providerRequest = new RuntimeProviderRequest(
                request.InvocationId,
                request.AgentId,
                request.ProviderId,
                request.ModelId,
                [.. messages],
                tools,
                IsIdempotent: !hasIrreversibleToolSideEffects,
                HasIrreversibleToolSideEffects: hasIrreversibleToolSideEffects,
                CancellationToken: ct,
                Skills: CreateSkillsContext(request))
            {
                HistoryMessageCount = request.HistoryMessageCount
            };

            List<ContentBlock> assistantContent;
            RuntimeError? failure;
            var completed = false;
            for (var providerAttempt = 0; ; providerAttempt++)
            {
                assistantContent = [];
                failure = null;
                completed = false;
                var hasObservedProviderOutput = false;
                var reasoningOpen = false;
                var attemptRequest = providerRequest.CreateAttempt(providerAttempt);
                await foreach (var providerEvent in ProviderInvocationBoundary.CompleteStreamingAsync(
                        provider,
                        attemptRequest,
                        ct)
                    .ConfigureAwait(false))
                {
                    if (providerEvent is ReasoningDeltaProviderEvent)
                    {
                        reasoningOpen = true;
                    }
                    else if (reasoningOpen)
                    {
                        reasoningOpen = false;
                        yield return new ReasoningCompletedProviderEvent(request.InvocationId);
                    }

                    switch (providerEvent)
                    {
                        case TextDeltaProviderEvent textDelta:
                            hasObservedProviderOutput = true;
                            AccumulateAssistantContent(assistantContent, textDelta);
                            yield return textDelta;
                            break;
                        case ReasoningDeltaProviderEvent reasoningDelta:
                            hasObservedProviderOutput = true;
                            AccumulateAssistantContent(assistantContent, reasoningDelta);
                            yield return reasoningDelta;
                            break;
                        case ToolCallDeltaProviderEvent toolCallDelta:
                            hasObservedProviderOutput = true;
                            yield return toolCallDelta;
                            break;
                        case ToolCallCompleteProviderEvent toolCall:
                            hasObservedProviderOutput = true;
                            AccumulateAssistantContent(assistantContent, toolCall);
                            yield return toolCall;
                            break;
                        case UsageUpdatedProviderEvent usage:
                            hasObservedProviderOutput = true;
                            yield return usage;
                            break;
                        case InvocationCompletedProviderEvent:
                            completed = true;
                            break;
                        case InvocationFailedProviderEvent failed:
                            failure = failed.Error;
                            break;
                    }

                    if (failure is not null)
                    {
                        break;
                    }
                }

                if (reasoningOpen)
                {
                    yield return new ReasoningCompletedProviderEvent(request.InvocationId);
                }

                if (failure is null)
                {
                    break;
                }

                if (providerAttempt != 0
                    || !IsAuthenticationFailure(failure)
                    || !ProviderRetryPolicy.CanRetry(providerRequest, hasObservedProviderOutput)
                    || _credentialRefreshHandler is null)
                {
                    yield return new InvocationFailedProviderEvent(request.InvocationId, failure);
                    yield break;
                }

                await _sessionRepo.TransitionRunStatusAsync(
                    request.RunId,
                    RunStatus.Running,
                    RunStatus.WaitingForCredentials,
                    ct).ConfigureAwait(false);
                await _workRepo.MarkCredentialsWaitAsync(
                    request.SessionId,
                    request.Snapshot.WorkStepId,
                    request.Snapshot.WorkPlanVersion,
                    ct).ConfigureAwait(false);
                yield return new WorkRunStatusProviderEvent(
                    request.InvocationId,
                    RunStatus.WaitingForCredentials);

                bool refreshed;
                try
                {
                    refreshed = await _credentialRefreshHandler(
                            new CredentialsRefreshRequestedEvent(
                                request.RunId,
                                request.ProviderId,
                                "Unauthorized"),
                            ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                    await _workRepo.MarkSessionInterruptedAsync(
                            request.SessionId,
                            "Provider credential refresh was interrupted.",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    throw;
                }

                await _workRepo.MarkCredentialsWaitEndedAsync(
                        request.SessionId,
                        request.Snapshot.WorkStepId,
                        request.Snapshot.WorkPlanVersion,
                        ct)
                    .ConfigureAwait(false);
                await _sessionRepo.TransitionRunStatusAsync(
                    request.RunId,
                    RunStatus.WaitingForCredentials,
                    RunStatus.Running,
                    ct).ConfigureAwait(false);
                yield return new WorkRunStatusProviderEvent(
                    request.InvocationId,
                    RunStatus.Running);
                if (!refreshed)
                {
                    yield return new InvocationFailedProviderEvent(
                        request.InvocationId,
                        CreateRuntimeError(
                            "CredentialRefreshTimeout",
                            "Provider credentials were not refreshed before the waiting period expired."));
                    yield break;
                }
            }

            if (!completed)
            {
                yield return new InvocationFailedProviderEvent(
                    request.InvocationId,
                    CreateRuntimeError(
                        RuntimeErrorCodes.ProviderProtocolError,
                        "The Provider stream ended before InvocationCompleted."));
                yield break;
            }

            var toolCalls = assistantContent.OfType<ToolCallContentBlock>().ToArray();
            if (toolCalls.Length == 0)
            {
                yield break;
            }

            if (maxToolRounds is { } toolRoundLimit && toolRound >= toolRoundLimit)
            {
                yield return new InvocationFailedProviderEvent(
                    request.InvocationId,
                    CreateRuntimeError(
                        RuntimeErrorCodes.LimitsIncompatible,
                        $"The Invocation exceeded the {toolRoundLimit}-round tool limit."));
                yield break;
            }

            if (maxToolCallsPerRound is { } toolCallLimit
                && toolCalls.Length > toolCallLimit)
            {
                yield return new InvocationFailedProviderEvent(
                    request.InvocationId,
                    CreateRuntimeError(
                        RuntimeErrorCodes.LimitsIncompatible,
                        $"The Provider requested {toolCalls.Length} tools in one round; the limit is {toolCallLimit}."));
                yield break;
            }

            if (toolCalls.Select(static call => call.CallId)
                    .Distinct(StringComparer.Ordinal)
                    .Count()
                != toolCalls.Length)
            {
                yield return new InvocationFailedProviderEvent(
                    request.InvocationId,
                    CreateRuntimeError(
                        RuntimeErrorCodes.ToolCallConflict,
                        "The Provider returned duplicate tool call ids in one round."));
                yield break;
            }

            var liveCatalog = CaptureCatalogSnapshot();
            if (_toolGateway is null || liveCatalog is null)
            {
                yield return new InvocationFailedProviderEvent(
                    request.InvocationId,
                    CreateRuntimeError(
                        RuntimeErrorCodes.ToolExecutorUnavailable,
                        "The selected Runtime does not have a tool gateway."));
                yield break;
            }

            // 允许集外的调用回工具错误，不中断本轮，让模型改调已暴露的工具。
            var allowedToolIds = tools is null
                ? null
                : tools.Select(static tool => tool.ToolId).ToHashSet(StringComparer.Ordinal);
            var deniedCalls = allowedToolIds is null
                ? []
                : toolCalls.Where(call => !allowedToolIds.Contains(call.ToolId)).ToArray();
            if (deniedCalls.Length > 0)
            {
                toolCalls = toolCalls
                    .Where(call => allowedToolIds!.Contains(call.ToolId))
                    .ToArray();
            }

            messages.Add(new RuntimeProviderMessage(
                RuntimeProviderRoles.Assistant,
                [.. assistantContent]));
            foreach (var denied in deniedCalls)
            {
                messages.Add(new RuntimeProviderMessage(
                    RuntimeProviderRoles.Tool,
                    [CreateDeniedToolResult(denied)]));
            }

            if (toolCalls.Length == 0)
            {
                continue;
            }

            foreach (var toolCall in toolCalls)
            {
                var callCatalog = CaptureCatalogSnapshot() ?? liveCatalog;
                var invocation = new ToolInvocation(
                    toolCall.CallId,
                    toolCall.ToolId,
                    request.AgentId,
                    request.ParentAgentId,
                    toolCall.Arguments.GetRawText(),
                    request.RunId,
                    request.SessionId,
                    request.InvocationId,
                    ToolCatalogVersion: callCatalog.EffectiveVersion,
                    WorkStepId: request.Snapshot.WorkStepId,
                    PlanVersion: request.Snapshot.WorkPlanVersion,
                    ParentInvocationId: request.Snapshot.ParentInvocationId);

                ToolGatewayResult gatewayResult;
                try
                {
                    gatewayResult = await _toolGateway.ExecuteAsync(
                        invocation,
                        permission: null,
                        callCatalog,
                        ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }

                if (gatewayResult.Kind is ToolGatewayResultKind.NeedsApproval
                    or ToolGatewayResultKind.NeedsPermission)
                {
                    if (_toolConsentCoordinator is null)
                    {
                        yield return new InvocationFailedProviderEvent(
                            request.InvocationId,
                            gatewayResult.Error ?? CreateRuntimeError(
                                RuntimeErrorCodes.ToolPermissionRequired,
                                "The Work tool call requires host consent."));
                        yield break;
                    }

                    var consentRequestId = ToolConsentCoordinator.CreateConsentRequestId(
                        gatewayResult.Kind,
                        invocation);
                    if (!string.IsNullOrWhiteSpace(invocation.WorkStepId)
                        && !string.IsNullOrWhiteSpace(invocation.PlanVersion))
                    {
                        await _workRepo.MarkStepWaitingForApprovalAsync(
                            invocation.SessionId,
                            invocation.WorkStepId,
                            invocation.PlanVersion,
                            consentRequestId,
                            CreatePendingConsentJson(gatewayResult.Kind, invocation, consentRequestId),
                            ct).ConfigureAwait(false);
                    }

                    gatewayResult = await _toolConsentCoordinator.ResolveAndExecuteAsync(
                        invocation,
                        gatewayResult,
                        ct).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(invocation.WorkStepId)
                        && !string.IsNullOrWhiteSpace(invocation.PlanVersion))
                    {
                        await _workRepo.MarkStepRunningAfterApprovalAsync(
                            invocation.SessionId,
                            invocation.WorkStepId,
                            invocation.PlanVersion,
                            consentRequestId,
                            ct).ConfigureAwait(false);
                    }
                }

                var toolResult = CreateToolResultContent(toolCall, gatewayResult);
                if (gatewayResult.Kind is ToolGatewayResultKind.Success
                    && gatewayResult.Result is { Success: true, OutputJson: { } setPlanOutput }
                    && string.Equals(toolCall.ToolId, WorkSetPlanToolId, StringComparison.Ordinal))
                {
                    TryCaptureSetPlanFromToolOutput(setPlanOutput);
                    if (_capturedSetPlanDraft is { } captured
                        && _dispatchContext?.Request.Selection.ModeOptions is WorkModeOptions workOptions)
                    {
                        try
                        {
                            WorkPlanValidator.ValidateOrThrow(captured, workOptions);
                        }
                        catch (ArgumentException ex)
                        {
                            _capturedSetPlanDraft = null;
                            toolResult = CreateDeniedToolResult(toolCall) with
                            {
                                Content =
                                [
                                    new TextContentBlock(
                                        "{\"ok\":false,\"error\":\"invalid_plan\",\"message\":\""
                                        + ex.Message.Replace("\\", "\\\\", StringComparison.Ordinal)
                                            .Replace("\"", "\\\"", StringComparison.Ordinal)
                                            .Replace("\r", " ", StringComparison.Ordinal)
                                            .Replace("\n", " ", StringComparison.Ordinal)
                                        + "\"}")
                                ]
                            };
                        }
                    }
                }

                if (_isHostModeSkills
                    && gatewayResult.Kind is ToolGatewayResultKind.Success
                    && gatewayResult.Result is { Success: true, OutputJson: { } outputJson })
                {
                    HostModeSkillsSnapshot.TryApplyToolResult(
                        _skillActivationStore,
                        request.SkillAllowlistIds,
                        toolCall.ToolId,
                        toolCall.Name,
                        outputJson,
                        request.SkillDiscoveryMode);
                }

                totalToolResultBytes += GetContentByteCount(toolResult);
                if (HasOversizedInlineContent(toolResult, _runtimeLimits.MaxInlineContentBytes)
                    || totalToolResultBytes > maxToolResultBytes)
                {
                    yield return new InvocationFailedProviderEvent(
                        request.InvocationId,
                        CreateRuntimeError(
                            RuntimeErrorCodes.LimitsIncompatible,
                            "Tool results exceeded the configured inline or Invocation size limit."));
                    yield break;
                }

                messages.Add(new RuntimeProviderMessage(
                    RuntimeProviderRoles.Tool,
                    [toolResult]));
                hasIrreversibleToolSideEffects = true;
            }

            // search 注入后刷新本轮 tools，下一轮 Provider 能看到短名工具
            tools = RefreshExposedTools(tools);
        }
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
                    content[^1] = previousReasoning with
                    {
                        Content = previousReasoning.Content + reasoning.Delta
                    };
                }
                else
                {
                    content.Add(new ReasoningContentBlock(reasoning.Delta, reasoning.ProviderExtensions));
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

    private static string CreatePendingConsentJson(
        ToolGatewayResultKind kind,
        ToolInvocation invocation,
        string consentRequestId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", kind switch
            {
                ToolGatewayResultKind.NeedsApproval => "approval",
                ToolGatewayResultKind.NeedsPermission => "permission",
                _ => "unknown"
            });
            writer.WriteString("requestId", consentRequestId);
            writer.WriteString("callId", invocation.CallId);
            writer.WriteString("toolId", invocation.ToolId);
            writer.WriteString("agentId", invocation.AgentId);
            writer.WriteString("parentAgentId", invocation.ParentAgentId);
            writer.WriteString("runId", invocation.RunId);
            writer.WriteString("sessionId", invocation.SessionId);
            writer.WriteString("invocationId", invocation.InvocationId);
            writer.WriteString("parentInvocationId", invocation.ParentInvocationId);
            writer.WriteString("workStepId", invocation.WorkStepId);
            writer.WriteString("planVersion", invocation.PlanVersion);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static ToolResultContentBlock CreateDeniedToolResult(ToolCallContentBlock toolCall)
    {
        var payload = "{\"ok\":false,\"error\":\"tool_not_allowed\",\"toolId\":\""
            + toolCall.ToolId.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
            + "\",\"message\":\"The tool is outside this Work invocation's allowed tool set.\"}";
        return new ToolResultContentBlock(
            toolCall.CallId,
            toolCall.ToolId,
            false,
            [new TextContentBlock(payload)]);
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

    private static RuntimeError CreateRuntimeError(string code, string message) =>
        new(
            code,
            "orchestration",
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

    private static bool IsAuthenticationFailure(RuntimeError error) =>
        string.Equals(error.Code, RuntimeErrorCodes.AuthenticationFailed, StringComparison.Ordinal)
        || string.Equals(error.Category, "authentication", StringComparison.Ordinal);


    private async Task<AgentInvocationRequest> CreateInvocationRequestAsync(
        string runId,
        string sessionId,
        NewSessionRunRequest request,
        AgentRef agent,
        string projectionVersion,
        CancellationToken ct,
        string? stepGoal = null,
        string? parentAgentId = null,
        string? parentInvocationId = null,
        string? workStepId = null,
        string? workPlanVersion = null,
        WorkPlanDraft? workPlan = null,
        WorkStepSnapshot? currentStepState = null,
        IReadOnlyList<WorkStepSnapshot>? planSteps = null,
        string? publishedPlanVersion = null,
        WorkResumeState? priorResume = null,
        IReadOnlyList<string>? pendingUserInputs = null)
    {
        var providerId = agent.ProviderId ?? request.Selection.DefaultSelection.ProviderId;
        var modelId = agent.ModelId ?? request.Selection.DefaultSelection.ModelId;
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var provider = _providerFactory(providerId)
            ?? throw new InvalidOperationException($"Provider '{providerId}' was not found.");
        var invocationId = Guid.NewGuid().ToString("N");
        var context = _agentContextComposer is null
            ? AgentContextComposer.WithoutMemory(agent.SystemPrompt)
            : await _agentContextComposer.ComposeAsync(agent.SystemPrompt, ct).ConfigureAwait(false);
        var workOptionsForTools = request.Selection.ModeOptions as WorkModeOptions;
        var availableTools = ResolveAvailableTools(agent, providerId, workOptionsForTools);

        // Append available agents list for GM if in Work mode with delegatable agents
        var systemInstructions = context.Instructions;
        if (workOptionsForTools is not null
            && agent.AgentId == workOptionsForTools.GeneralManager.AgentId
            && workOptionsForTools.AvailableAgents.Any(a => !string.Equals(a.AgentId, agent.AgentId, StringComparison.Ordinal)))
        {
            systemInstructions = AppendAvailableAgentsList(systemInstructions, workOptionsForTools.AvailableAgents);
        }

        var systemMessage = new RuntimeProviderMessage(
            RuntimeProviderRoles.System,
            [new TextContentBlock(systemInstructions)]);
        RuntimeProviderMessage[] projectedMessages;
        ContextProjectionSnapshot? projectionSnapshot = null;
        if (string.Equals(projectionVersion, PlanProjectionVersion, StringComparison.Ordinal)
            && request.Selection.ModeOptions is WorkModeOptions workOptions)
        {
            projectedMessages =
            [
                new RuntimeProviderMessage(
                    RuntimeProviderRoles.User,
                    [new TextContentBlock(BuildManagerPlanningInput(
                        ExtractGoalText(request.InitialInput),
                        workOptions,
                        availableTools,
                        publishedPlanVersion,
                        priorResume,
                        pendingUserInputs,
                        await ReadPlanningTranscriptTailAsync(sessionId, ct).ConfigureAwait(false)))])
            ];
        }
        else if (workPlan is not null
            && currentStepState is not null
            && planSteps is not null
            && request.Selection.ModeOptions is WorkModeOptions projectedWorkOptions)
        {
            var currentStep = workPlan.Steps.FirstOrDefault(step =>
                string.Equals(step.StepId, currentStepState.StepId, StringComparison.Ordinal))
                ?? throw new InvalidDataException(
                    $"Work plan '{workPlan.PlanVersion}' does not contain step '{currentStepState.StepId}'.");
            var modelContextWindow = provider.Models
                .FirstOrDefault(candidate => string.Equals(candidate.Id, modelId, StringComparison.Ordinal))
                ?.ContextWindow;
            var modelContextLimit = modelContextWindow is > 0
                ? (int)Math.Max(1, modelContextWindow.Value * 9L / 10L)
                : int.MaxValue;
            var configuredLimit = projectedWorkOptions.EffectiveContextPolicy.MaxTokens;
            var tokenLimit = configuredLimit is > 0
                ? Math.Min(configuredLimit.Value, modelContextLimit)
                : modelContextLimit;
            ValueTask<ProviderTokenEstimate> EstimateProjectionAsync(
                RuntimeProviderMessage[] candidateMessages,
                CancellationToken token) =>
                provider.EstimateTokensAsync(
                    new RuntimeProviderRequest(
                        invocationId,
                        agent.AgentId,
                        providerId,
                        modelId,
                        [systemMessage, .. candidateMessages],
                        Tools: availableTools,
                        CancellationToken: token),
                    token);
            var projection = await WorkContextProjectionBuilder.BuildAsync(
                    workPlan,
                    currentStep,
                    currentStepState,
                    planSteps,
                    projectedWorkOptions.EffectiveContextPolicy,
                    tokenLimit,
                    EstimateProjectionAsync,
                    ct: ct)
                .ConfigureAwait(false);
            projectedMessages = projection.Messages;
            projectionSnapshot = new ContextProjectionSnapshot(
                projection.Strategy,
                projection.RetainedItems,
                projection.DroppedMessageCount,
                projection.Messages.Length,
                projection.EstimatedTokens,
                projection.EstimateSource,
                projection.TokenLimit,
                projection.SummarizedUnitCount);
        }
        else
        {
            var userContent = stepGoal is null
                ? request.InitialInput
                : [new TextContentBlock(stepGoal)];
            projectedMessages =
            [
                new RuntimeProviderMessage(RuntimeProviderRoles.User, userContent)
            ];
        }

        var fullSkillCatalog = await CaptureSkillCatalogSnapshotAsync(ct).ConfigureAwait(false);
        var skillCatalogSnapshot = fullSkillCatalog?.FilterBySkillIds(agent.SkillIds);
        var hostMaps = _isHostModeSkills
            ? HostModeSkillsSnapshot.BuildMaps(skillCatalogSnapshot)
            : null;

        // Query plan to get step semantic fields for work mode
        WorkPlanStepDraft? stepDraft = null;
        if (workStepId is not null && !string.IsNullOrEmpty(workPlanVersion) && workPlan is not null)
        {
            stepDraft = workPlan.Steps.FirstOrDefault(s => string.Equals(s.StepId, workStepId, StringComparison.Ordinal));
        }

        var snapshot = new InvocationSnapshot(
            invocationId,
            agent.AgentId,
            providerId,
            modelId,
            ComputeHash(agent.SystemPrompt),
            context.GlobalMemoryHash,
            context.ProjectMemoryHash,
            request.Selection.ToolCatalogVersion,
            projectionVersion,
            DateTimeOffset.UtcNow,
            parentInvocationId,
            workStepId,
            workPlanVersion,
            Title: stepDraft?.Title ?? stepDraft?.Goal,
            ParentStepId: stepDraft?.ParentStepId,
            Depth: stepDraft?.Depth ?? 0,
            DisplayName: agent.AgentId,
            Role: null,
            ParticipantId: null,
            RoundIndex: null,
            projectionSnapshot,
            SkillCatalogVersion: skillCatalogSnapshot?.Version ?? fullSkillCatalog?.Version,
            SkillNames: skillCatalogSnapshot?.Skills.Select(static skill => skill.Name).ToArray(),
            SkillContentHashes: skillCatalogSnapshot?.Skills.Select(static skill => skill.ContentHash).ToArray(),
            SkillSourceIds: skillCatalogSnapshot?.Skills.Select(static skill => skill.SourceId).ToArray(),
            SkillMaterializedPaths: skillCatalogSnapshot?.Skills.Select(static skill => skill.MaterializedPath).ToArray());
        if (_isHostModeSkills)
        {
            snapshot = HostModeSkillsSnapshot.ProjectAdvertiseEmpty(
                snapshot,
                skillCatalogSnapshot ?? fullSkillCatalog);
        }

        return new AgentInvocationRequest(
            invocationId,
            runId,
            sessionId,
            agent.AgentId,
            providerId,
            modelId,
            [
                systemMessage,
                .. projectedMessages
            ],
            snapshot,
            AvailableTools: availableTools,
            ParentAgentId: parentAgentId,
            HistoryMessageCount: Math.Max(0, projectedMessages.Length),
            IsHostModeSkills: _isHostModeSkills,
            SkillActivationStore: _skillActivationStore,
            SkillAllowlistIds: agent.SkillIds,
            SkillCatalogPathsByName: hostMaps?.PathsByName,
            SkillCatalogDescriptionsByName: hostMaps?.DescriptionsByName,
            SkillScopeSourceRoots: hostMaps?.ScopeSourceRoots,
            SkillDiscoveryMode: _isHostModeSkills
                ? SkillDiscoveryModes.Normalize(agent.SkillDiscoveryMode)
                : null);
    }

    private static string BuildManagerPlanningInput(
        string goal,
        WorkModeOptions options,
        ToolDescriptor[]? tools,
        string? publishedPlanVersion = null,
        WorkResumeState? priorResume = null,
        IReadOnlyList<string>? pendingUserInputs = null,
        string? recentTranscript = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Create a structured WorkPlanDraft for this goal:");
        builder.AppendLine(goal);
        var maxSteps = options.EffectiveWorkflowPolicy.MaxStepsPerRun;
        builder.AppendLine("Step granularity:");
        builder.AppendLine("- One steps[] entry, or one main_steps[] entry, is one dispatch to one agent.");
        builder.AppendLine("- Create/verify gates belong in that step's goal or sub_steps. They are not extra plan steps.");
        builder.AppendLine("- Do not multiply deliverable count by internal gates. Ten documents with ten gates each is about ten to twelve plan steps (one per document, plus optional prepare and close).");
        builder.Append(CultureInfo.InvariantCulture, $"- A request for at least K steps means at least K dispatch steps, not K gates inside every deliverable. Stay at or under MaxStepsPerRun ({maxSteps}).");
        builder.AppendLine();
        if (!string.IsNullOrWhiteSpace(recentTranscript))
        {
            builder.AppendLine("Recent session transcript. Continue from it. Do not ask the user to restate the goal:");
            builder.AppendLine(recentTranscript);
        }
        if (pendingUserInputs is { Count: > 0 })
        {
            builder.AppendLine("Pending user inputs (FIFO, already accepted this turn):");
            for (var index = 0; index < pendingUserInputs.Count; index++)
            {
                builder.Append(CultureInfo.InvariantCulture, $"[{index + 1}] ")
                    .AppendLine(pendingUserInputs[index]);
            }

            builder.AppendLine(
                "Absorb every pending user input before deciding discussion, confirmation, or a revised plan.");
        }

        builder.AppendLine("Available agents and declared capabilities:");
        foreach (var agent in options.AvailableAgents)
        {
            builder.Append("- ").Append(agent.AgentId);
            if (agent.AllowedToolIds is { Length: > 0 })
            {
                builder.Append("; tools=").AppendJoin(',', agent.AllowedToolIds);
            }

            if (agent.SkillIds is { Length: > 0 })
            {
                builder.Append("; skills=").AppendJoin(',', agent.SkillIds);
            }

            builder.AppendLine();
        }

        builder.AppendLine("Runtime tool catalog visible to the general manager:");
        if (tools is null || tools.Length == 0)
        {
            builder.AppendLine("- none");
        }
        else
        {
            foreach (var tool in tools)
            {
                builder.Append("- ").Append(tool.ToolId).Append(':').AppendLine(tool.DisplayName);
            }
        }

        builder.Append("WorkflowPolicy: ").AppendLine(
            JsonSerializer.Serialize(
                options.EffectiveWorkflowPolicy,
                RuntimeJsonContext.Default.WorkflowPolicy));

        if (priorResume?.Status is WorkSessionStatus.WaitingForPlanConfirmation)
        {
            builder.AppendLine(
                "A plan is waiting for user confirmation. Do not invent a new plan unless the user asked to revise it.");
            builder.AppendLine(
                "If the user only discusses without approving execution, reply in plain text with no JSON object.");
        }

        if (tools is { Length: > 0 }
            && tools.Any(static tool => string.Equals(tool.ToolId, WorkSetPlanToolId, StringComparison.Ordinal)))
        {
            builder.AppendLine(
                "Preferred path: call mcli_work_set_plan with a WorkPlanDraft (planVersion/goal/steps) or main_steps/sub_steps when proposing an executable plan.");
            builder.AppendLine(
                "Do not expand {{placeholders}}. Template tools only supply materials; set_plan is required to publish.");
        }

        if (string.IsNullOrWhiteSpace(publishedPlanVersion))
        {
            builder.AppendLine(
                "Alternatively return only a WorkPlanDraft JSON object when proposing an executable plan.");
            builder.AppendLine(
                "The first executable planVersion must be '1' with no previousPlanVersion.");
            builder.AppendLine(
                "If the user only wants discussion or has not approved a plan, reply in plain text with no JSON object and do not call set_plan.");
        }
        else
        {
            var nextVersion = publishedPlanVersion;
            if (int.TryParse(
                    publishedPlanVersion,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var currentVersion))
            {
                nextVersion = (currentVersion + 1).ToString(CultureInfo.InvariantCulture);
            }

            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"A plan is already published at planVersion '{publishedPlanVersion}'.");
            var keepPublishedPlan = !IsPlanRevisionRequest(goal)
                && !ContainsPlanJson(goal)
                && priorResume?.Steps is { Length: > 0 } openSteps
                && openSteps.All(static step => step.Status is WorkStepLifecycleStatus.Pending
                    or WorkStepLifecycleStatus.Interrupted);
            if (keepPublishedPlan)
            {
                builder.AppendLine(
                    "This plan has no completed step result. Do not call mcli_work_set_plan and do not publish a new planVersion.");
                builder.AppendLine(
                    "Reply in plain text that the current plan is unchanged and still waiting for confirmation.");
                builder.AppendLine(
                    "Do not mention stepId, Interrupted, publish failures, or internal repairs.");
            }
            else
            {
                builder.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"Any revised executable plan must use planVersion '{nextVersion}' and previousPlanVersion '{publishedPlanVersion}'.");
                builder.AppendLine(
                    "Preserve completed step outcomes via reusesStepId when the work should not be redone.");
            }

            builder.AppendLine(
                "If the user only wants discussion and has not approved a revised plan, reply in plain text with no JSON object and do not call set_plan.");
            if (priorResume?.Steps is { Length: > 0 } steps)
            {
                builder.AppendLine("Current step statuses:");
                foreach (var step in steps)
                {
                    builder.Append("- ")
                        .Append(step.StepId)
                        .Append(": ")
                        .Append(step.Status)
                        .Append(" / ")
                        .AppendLine(step.Goal);
                }
            }
        }

        return builder.ToString();
    }

    private static bool IsExplicitPlanConfirmation(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (ContainsAny(text,
                "不要执行", "别执行", "先不要", "未批准", "还未确认", "先讨论",
                "不是执行批准", "不是批准", "禁止执行", "等待批准", "等待确认",
                "do not execute", "don't execute", "dont execute", "not yet", "do not confirm",
                "not an approval"))
        {
            return false;
        }

        var trimmed = text.Trim().TrimEnd('。', '.', '!', '！');
        if (ShortPlanConfirmations.Contains(trimmed))
        {
            return true;
        }

        // 避免「可执行计划」误命中「执行计划」；只用明确批准执行的短语。
        return ContainsAny(text,
            "确认执行", "批准执行", "同意执行", "开始执行", "可以执行", "确认计划",
            "批准计划", "确认并执行", "按计划执行", "开始按计划执行",
            "confirm and execute", "approve and execute", "execute the plan",
            "confirm the plan", "approve the plan", "lgtm", "ship it");
    }

    private static readonly HashSet<string> ShortPlanConfirmations = new(StringComparer.OrdinalIgnoreCase)
    {
        "开始",
        "开始吧",
        "执行",
        "执行吧",
        "同意",
        "确认",
        "批准",
        "ok",
        "yes",
        "go",
        "lgtm"
    };

    private static bool IsPlanRevisionRequest(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return ContainsAny(text,
            "修改计划", "修订计划", "改一下计划", "更新计划", "调整步骤", "增加步骤", "删除步骤",
            "revise the plan", "revise plan", "change the plan", "update the plan",
            "add a step", "remove a step", "modify the plan");
    }

    private static string CombineGoalWithPendingInputs(
        string goal,
        string[] pendingTexts)
    {
        if (pendingTexts.Length == 0)
        {
            return goal;
        }

        var builder = new StringBuilder(goal);
        foreach (var pending in pendingTexts)
        {
            if (string.IsNullOrWhiteSpace(pending))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine().AppendLine();
            }

            builder.Append(pending);
        }

        return builder.ToString();
    }

    private static bool ContainsPlanJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("\"planVersion\"", StringComparison.Ordinal)
            || text.Contains("\"steps\"", StringComparison.Ordinal);
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private ToolDescriptor[]? ResolveAvailableTools(
        AgentRef agent,
        string providerId,
        WorkModeOptions? workOptions = null)
    {
        var catalog = CaptureCatalogSnapshot();
        if (_toolGateway is null || catalog is null)
        {
            return null;
        }

        // Work 模式按 Mode + 逻辑角色过滤（GM 全开，project-lead 仅 dispatch/read_template）。
        // 角色按 GeneralManager.AgentId 判定，避免宿主自定义 GM id（如 gm）被当成普通员工。
        catalog = catalog.FilterByMode("work", ResolveWorkToolRole(agent, workOptions));

        if (agent.AllowedToolIds is { Length: > 0 } allowedToolIds)
        {
            var requestedToolIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var toolId in allowedToolIds)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
                if (!requestedToolIds.Add(toolId))
                {
                    throw new ArgumentException(
                        $"Work-mode Agent '{agent.AgentId}' declares duplicate tool ID '{toolId}'.",
                        nameof(agent));
                }

                if (!catalog.TryGetTool(toolId, out _))
                {
                    throw new ArgumentException(
                        $"Work-mode Agent '{agent.AgentId}' declares unknown tool ID '{toolId}'.",
                        nameof(agent));
                }
            }

            var provider = _providerFactory(providerId)
                ?? throw new InvalidOperationException($"Provider '{providerId}' was not found.");
            if (!provider.Capabilities.ToolCalling)
            {
                throw new ArgumentException(
                    $"Provider '{providerId}' does not support tools required by Work-mode Agent '{agent.AgentId}'.",
                    nameof(agent));
            }

            return catalog.Tools
                .Where(tool => requestedToolIds.Contains(tool.ToolId))
                .ToArray();
        }

        return [.. catalog.Tools];
    }

    private ToolCatalogSnapshot? CaptureCatalogSnapshot()
    {
        if (_liveCatalogProvider is not null)
        {
            return _liveCatalogProvider();
        }

        return _toolCatalogSnapshot;
    }

    private static string ResolveWorkToolRole(AgentRef agent, WorkModeOptions? workOptions)
    {
        if (workOptions is not null
            && string.Equals(agent.AgentId, workOptions.GeneralManager.AgentId, StringComparison.Ordinal))
        {
            return "general-manager";
        }

        return agent.AgentId;
    }

    /// <summary>
    /// Refresh tools exposed to the Provider after a tool round.
    /// Full-catalog agents follow live growth (search injection).
    /// Allowlist agents only refresh descriptors for already-allowed ToolIds.
    /// </summary>
    private ToolDescriptor[]? RefreshExposedTools(ToolDescriptor[]? previouslyExposed)
    {
        var catalog = CaptureCatalogSnapshot();
        if (_toolGateway is null || catalog is null)
        {
            return previouslyExposed;
        }

        // 刷新时仍限制在 Work 可见工具；角色收窄由 previousIds / allowlist 保持。
        catalog = catalog.FilterByMode("work");

        if (previouslyExposed is null)
        {
            return [.. catalog.Tools];
        }

        var previousIds = previouslyExposed
            .Select(static tool => tool.ToolId)
            .ToHashSet(StringComparer.Ordinal);

        // Full catalog: initial snapshot tools were all exposed -> follow live growth
        // (search/browse 注入后下一轮必须带上新短名)。
        // Allowlist: initial snapshot had tools not in previousIds -> do not expand.
        // initial 缺失时不推断为全量，避免把 allowlist 放大成 live 全目录。
        var initial = _toolCatalogSnapshot;
        var wasFullCatalog = initial is not null
            && initial.Tools.Count > 0
            && initial.Tools.All(tool => previousIds.Contains(tool.ToolId))
            && previouslyExposed.Length >= initial.Tools.Count;

        if (wasFullCatalog)
        {
            return [.. catalog.Tools];
        }

        return catalog.Tools
            .Where(tool => previousIds.Contains(tool.ToolId))
            .ToArray();
    }
    private static void ValidateWorkRequest(NewSessionRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Selection);
        ArgumentNullException.ThrowIfNull(request.InitialInput);
        if (request.Mode is not RuntimeMode.Work
            || request.Selection.Mode is not RuntimeMode.Work
            || request.Selection.ModeOptions is not WorkModeOptions options)
        {
            throw new ArgumentException("The request must contain Work mode options.", nameof(request));
        }

        options.EffectiveWorkflowPolicy.Validate();
        ValidateAgent(options.GeneralManager, nameof(options.GeneralManager));
        ArgumentNullException.ThrowIfNull(options.AvailableAgents);
        if (options.AvailableAgents.Length == 0)
        {
            throw new ArgumentException(
                "Work mode requires at least one available child Agent.", nameof(request));
        }

        var agentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var agent in options.AvailableAgents)
        {
            ValidateAgent(agent, nameof(options.AvailableAgents));
            if (!agentIds.Add(agent.AgentId))
            {
                throw new ArgumentException(
                    $"Work-mode Agent ID '{agent.AgentId}' is duplicated.", nameof(request));
            }
        }
    }

    private static void ValidateAgent(AgentRef agent, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(agent, parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent.PromptTemplateVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent.SystemPrompt);
    }

    private static string ComputeLegacyStepInputHash(ContentBlock[] input)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            input,
            RuntimeJsonContext.Default.ContentBlockArray);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string ComputeHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private Task<SkillCatalogSnapshot?> CaptureSkillCatalogSnapshotAsync(CancellationToken ct) =>
        _skillCatalogSnapshotProvider?.Invoke(ct)
        ?? Task.FromResult<SkillCatalogSnapshot?>(null);

    private SkillsInvocationContext? CreateSkillsContext(AgentInvocationRequest request)
    {
        var snapshot = request.Snapshot;
        if (string.IsNullOrWhiteSpace(snapshot.SkillCatalogVersion))
        {
            return null;
        }

        if (request.IsHostModeSkills)
        {
            var hostContext = new SkillsInvocationContext(
                snapshot.SkillCatalogVersion,
                snapshot.SkillNames ?? [],
                snapshot.SkillContentHashes ?? [],
                snapshot.SkillSourceIds ?? [],
                snapshot.SkillMaterializedPaths,
                _scriptInvoker,
                request.RunId,
                request.SessionId,
                request.AgentId,
                request.InvocationId,
                request.ParentAgentId,
                snapshot.ParentInvocationId,
                snapshot.WorkStepId,
                snapshot.WorkPlanVersion,
                IsHostMode: true,
                ActivationStore: request.SkillActivationStore ?? _skillActivationStore,
                AllowlistSkillIds: request.SkillAllowlistIds,
                CatalogMaterializedPathsByName: request.SkillCatalogPathsByName,
                CatalogDescriptionsByName: request.SkillCatalogDescriptionsByName,
                ScopeSourceRoots: request.SkillScopeSourceRoots,
                SkillDiscoveryMode: request.SkillDiscoveryMode);
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
            request.RunId,
            request.SessionId,
            request.AgentId,
            request.InvocationId,
            request.ParentAgentId,
            snapshot.ParentInvocationId,
            snapshot.WorkStepId,
            snapshot.WorkPlanVersion);
    }

    /// <summary>
    /// Appends available agents list to system instructions for GM agent delegation.
    /// </summary>
    private static string AppendAvailableAgentsList(string baseInstructions, AgentRef[] availableAgents)
    {
        if (availableAgents.Length <= 1)
        {
            return baseInstructions;
        }

        var agentListBuilder = new StringBuilder();
        agentListBuilder.AppendLine();
        agentListBuilder.AppendLine();
        agentListBuilder.AppendLine("## Agent Delegation in Work Mode");
        agentListBuilder.AppendLine();
        agentListBuilder.AppendLine("When creating the WorkPlanDraft, assign each step to the most appropriate agent by setting the `targetAgentId` field in each WorkPlanStepDraft.");
        agentListBuilder.AppendLine();
        agentListBuilder.AppendLine("Available agents:");
        agentListBuilder.AppendLine();

        foreach (var agent in availableAgents)
        {
            agentListBuilder.AppendLine(CultureInfo.InvariantCulture, $"- **{agent.AgentId}**");
        }

        agentListBuilder.AppendLine();
        agentListBuilder.AppendLine("When the user mentions \"@AgentName\" or \"let {Agent} do...\", assign that step's targetAgentId to the specified agent.");
        agentListBuilder.AppendLine("The orchestrator will automatically invoke the correct agent for each step during execution.");

        return baseInstructions + agentListBuilder.ToString();
    }

    /// <summary>
    /// 将 WorkPlanDraft 映射为 work.plan.updated 事件载荷。
    ///
    /// 状态映射策略说明：
    /// - v3 规范：work.plan.updated 仅在计划生成时发送一次
    /// - 此时所有步骤状态均为 Pending（尚未开始执行）
    /// - 后续步骤状态变更通过 invocation.started/completed/failed 传递
    /// - 如未来支持动态计划更新，需从数据库查询实际状态而非硬编码
    /// </summary>
    private static WorkPlanUpdatedEvent MapToWorkPlanUpdatedEvent(WorkPlanDraft plan)
    {
        var steps = plan.Steps.Select(s => new WorkPlanStepSnapshot(
            s.StepId,
            s.Title ?? s.Goal,
            s.Goal,
            s.TargetAgentId,
            s.ParentStepId,
            s.Depth,
            s.DependsOn ?? Array.Empty<string>(),
            Contracts.Work.WorkStepStatus.Pending
        )).ToArray();

        return new WorkPlanUpdatedEvent(
            int.Parse(plan.PlanVersion, CultureInfo.InvariantCulture),
            plan.Goal,
            steps);
    }

    private static RuntimeEventEnvelope CreateWorkPlanUpdatedEnvelope(
        string runtimeInstanceId,
        long gsn,
        string runId,
        long runSequence,
        WorkPlanUpdatedEvent evt)
    {
        var payloadJson = JsonSerializer.SerializeToElement(
            evt,
            RuntimeJsonContext.Default.WorkPlanUpdatedEvent);

        return new RuntimeEventEnvelope(
            runtimeInstanceId,
            gsn,
            runId,
            runSequence,
            MessageTypes.WorkPlanUpdated,
            DateTimeOffset.UtcNow,
            payloadJson);
    }

    private async Task<WorkDispatchStepResult> HandleDispatchStepAsync(
        WorkDispatchStepRequest dispatch,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        var context = _dispatchContext;
        if (context is null)
        {
            return new WorkDispatchStepResult(
                false,
                ErrorCode: "dispatch_runner_unavailable",
                ErrorMessage: "Work orchestrator dispatch context is not active.");
        }

        if (!string.Equals(dispatch.SessionId, context.SessionId, StringComparison.Ordinal)
            || !string.Equals(dispatch.RunId, context.RunId, StringComparison.Ordinal))
        {
            return new WorkDispatchStepResult(
                false,
                ErrorCode: "dispatch_session_mismatch",
                ErrorMessage: "dispatch_step session/run does not match the active Work run.");
        }

        var resume = await _workRepo.GetResumeStateAsync(context.SessionId, ct).ConfigureAwait(false);
        if (resume?.Status is not WorkSessionStatus.Executing)
        {
            return new WorkDispatchStepResult(
                false,
                ErrorCode: "plan_not_confirmed",
                ErrorMessage: "The published plan is not executing. Wait for explicit confirmation; the orchestrator dispatches steps.");
        }

        var agentById = context.Options.AvailableAgents
            .Concat([context.Options.GeneralManager])
            .GroupBy(static agent => agent.AgentId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        if (!agentById.TryGetValue(dispatch.TargetAgentId, out var agent))
        {
            return new WorkDispatchStepResult(
                false,
                ErrorCode: "target_agent_not_found",
                ErrorMessage: $"Target agent '{dispatch.TargetAgentId}' is not in AvailableAgents.");
        }

        var plan = await _workRepo.GetCurrentPlanDraftAsync(context.SessionId, ct).ConfigureAwait(false);
        if (plan is null
            || !string.Equals(plan.PlanVersion, dispatch.PlanVersion, StringComparison.Ordinal))
        {
            return new WorkDispatchStepResult(
                false,
                ErrorCode: "plan_not_set",
                ErrorMessage: "Published plan is missing or planVersion mismatch for dispatch.");
        }

        var planSteps = await _workRepo.ListStepsAsync(context.SessionId, plan.PlanVersion, ct)
            .ConfigureAwait(false);
        var currentStep = planSteps.FirstOrDefault(step =>
            string.Equals(step.StepId, dispatch.StepId, StringComparison.Ordinal));
        if (currentStep is null)
        {
            return new WorkDispatchStepResult(
                false,
                ErrorCode: "step_not_materialized",
                ErrorMessage: $"Step '{dispatch.StepId}' is not materialized in the published plan ledger.");
        }

        if (currentStep.Status is not WorkStepLifecycleStatus.Pending)
        {
            return new WorkDispatchStepResult(
                false,
                ErrorCode: "step_not_dispatchable",
                ErrorMessage: $"Step '{dispatch.StepId}' status is '{currentStep.Status}'; only Pending steps can be dispatched.");
        }

        var stepGoal = string.IsNullOrWhiteSpace(dispatch.Instruction)
            ? (dispatch.Goal ?? string.Empty)
            : $"{dispatch.Goal}\n\nDispatch instruction:\n{dispatch.Instruction}";
        var stepInputHash = string.IsNullOrWhiteSpace(currentStep.StepInputHash)
            ? WorkPlanValidator.ComputeStepInputHash(
                currentStep.StepId,
                currentStep.PlanVersion,
                currentStep.Goal ?? stepGoal)
            : currentStep.StepInputHash;

        var childInvocation = await CreateInvocationRequestAsync(
                context.RunId,
                context.SessionId,
                context.Request,
                agent,
                StepProjectionVersion,
                ct,
                stepGoal: stepGoal,
                parentAgentId: dispatch.AgentId,
                parentInvocationId: dispatch.InvocationId ?? dispatch.CallId,
                workStepId: dispatch.StepId,
                workPlanVersion: plan.PlanVersion,
                workPlan: plan,
                currentStepState: currentStep,
                planSteps: planSteps.Count == 0 ? null : planSteps)
            .ConfigureAwait(false);

        var childSnapshotJson = JsonSerializer.Serialize(
            childInvocation.Snapshot,
            RuntimeJsonContext.Default.InvocationSnapshot);
        var checkpointJson = CreateStepCheckpointJson(
            currentStep,
            childInvocation.InvocationId,
            stepInputHash);
        var started = await _sessionRepo.TryStartWorkStepWithCheckpointAsync(
                currentStep.StepId,
                currentStep.PlanVersion,
                context.RunId,
                context.SessionId,
                currentStep.TargetAgentId,
                stepInputHash,
                childInvocation.InvocationId,
                childInvocation.Snapshot,
                childSnapshotJson,
                checkpointJson,
                ct)
            .ConfigureAwait(false);
        if (!started)
        {
            return new WorkDispatchStepResult(
                false,
                ErrorCode: "step_start_failed",
                ErrorMessage: $"Failed to start step '{dispatch.StepId}' from Pending; another runner may own it.",
                ChildInvocationId: childInvocation.InvocationId);
        }

        var sequence = context.Sequence;
        // Nested under GM tool execution: persist the same lifecycle events to outbox.
        // Live DispatchPendingEvents flushes them on the next outer RunAsync yield.
        _ = await CreateInvocationStartedEnvelopeAsync(
                childInvocation,
                context.RuntimeInstanceId,
                sequence.Take(),
                ct)
            .ConfigureAwait(false);
        if (IsProjectionAdjusted(childInvocation.Snapshot.ContextProjection))
        {
            _ = await CreateContextProjectionAdjustedEnvelopeAsync(
                    childInvocation,
                    context.RuntimeInstanceId,
                    sequence.Take(),
                    ct)
                .ConfigureAwait(false);
        }

        var textBuilder = new StringBuilder();
        RuntimeError? error = null;
        await foreach (var providerEvent in ExecuteInvocationAsync(childInvocation, ct)
            .ConfigureAwait(false))
        {
            switch (providerEvent)
            {
                case TextDeltaProviderEvent textDelta:
                    textBuilder.Append(textDelta.Delta);
                    _ = await CreateTextDeltaEnvelopeAsync(
                            context.RunId,
                            context.RuntimeInstanceId,
                            textDelta.InvocationId,
                            textDelta.Delta,
                            sequence.Take(),
                            ct)
                        .ConfigureAwait(false);
                    break;
                case ReasoningDeltaProviderEvent reasoningDelta:
                    _ = await CreateReasoningDeltaEnvelopeAsync(
                            context.RunId,
                            context.RuntimeInstanceId,
                            reasoningDelta.InvocationId,
                            reasoningDelta.Delta,
                            sequence.Take(),
                            ct)
                        .ConfigureAwait(false);
                    break;
                case ReasoningCompletedProviderEvent reasoningCompleted:
                    _ = await CreateReasoningCompletedEnvelopeAsync(
                            context.RunId,
                            context.RuntimeInstanceId,
                            reasoningCompleted.InvocationId,
                            sequence.Take(),
                            ct)
                        .ConfigureAwait(false);
                    break;
                case ToolCallDeltaProviderEvent toolCallDelta:
                    _ = await CreateToolCallDeltaEnvelopeAsync(
                            context.RunId,
                            context.RuntimeInstanceId,
                            toolCallDelta.InvocationId,
                            toolCallDelta.CallId,
                            toolCallDelta.Name,
                            toolCallDelta.ArgumentsDelta,
                            sequence.Take(),
                            ct)
                        .ConfigureAwait(false);
                    break;
                case UsageUpdatedProviderEvent usage:
                    _ = await CreateUsageUpdatedEnvelopeAsync(
                            context.RunId,
                            context.RuntimeInstanceId,
                            usage.InvocationId,
                            usage.InputTokens,
                            usage.OutputTokens,
                            sequence.Take(),
                            ct)
                        .ConfigureAwait(false);
                    break;
                case InvocationFailedProviderEvent failed:
                    error = failed.Error;
                    break;
                case WorkRunStatusProviderEvent statusChanged:
                    _ = await CreateStatusEnvelopeAsync(
                            context.RuntimeInstanceId,
                            context.RunId,
                            sequence.Take(),
                            statusChanged.Status,
                            ct,
                            workPhase: "dispatch-step")
                        .ConfigureAwait(false);
                    break;
            }
        }

        if (error is not null)
        {
            await _sessionRepo.FailWorkStepAsync(
                    currentStep.StepId,
                    currentStep.PlanVersion,
                    error.Message ?? error.Code,
                    ct)
                .ConfigureAwait(false);
            _ = await CreateInvocationFailedEnvelopeAsync(
                    context.RunId,
                    context.RuntimeInstanceId,
                    childInvocation.InvocationId,
                    error,
                    sequence.Take(),
                    ct,
                    workStepId: currentStep.StepId)
                .ConfigureAwait(false);
            return new WorkDispatchStepResult(
                false,
                ErrorCode: error.Code ?? "dispatch_invocation_failed",
                ErrorMessage: error.Message ?? "Child agent invocation failed.",
                ChildInvocationId: childInvocation.InvocationId);
        }

        var outputText = textBuilder.ToString().Trim();
        var resultJson = JsonSerializer.Serialize(
            new TextContentBlock(outputText),
            RuntimeJsonContext.Default.TextContentBlock);
        await _sessionRepo.CompleteWorkStepAsync(
                currentStep.StepId,
                currentStep.PlanVersion,
                stepInputHash,
                resultJson,
                ct)
            .ConfigureAwait(false);
        _ = await CreateInvocationCompletedEnvelopeAsync(
                context.RunId,
                context.RuntimeInstanceId,
                childInvocation.InvocationId,
                sequence.Take(),
                ct,
                workStepId: currentStep.StepId)
            .ConfigureAwait(false);

        return new WorkDispatchStepResult(
            true,
            outputText,
            ChildInvocationId: childInvocation.InvocationId);
    }

    /// <summary>Run-local sequence allocator shared by RunCoreAsync and sync dispatch_step.</summary>
    private sealed class RunSequenceAllocator
    {
        private long _next;

        public RunSequenceAllocator(long start)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(start);
            _next = start;
        }

        public long Current => _next;

        public long Take() => _next++;

        public void CatchUp(long nextSequence)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(nextSequence);
            if (nextSequence > _next)
            {
                _next = nextSequence;
            }
        }
    }

    private sealed record DispatchRunContext(
        string RunId,
        string SessionId,
        string RuntimeInstanceId,
        NewSessionRunRequest Request,
        WorkModeOptions Options,
        RunSequenceAllocator Sequence);
}
