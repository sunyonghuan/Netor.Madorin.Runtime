using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Orchestration.Abstractions;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services;
using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Services.Skills;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Modes.Meeting;

/// <summary>Runs the deterministic participant loop used by the V1 meeting skeleton.</summary>
public sealed class MeetingModeOrchestrator : IModeOrchestrator
{
    private const int MaximumParticipantCount = 20;
    private const int MinimumParticipantCount = 2;
    private const int RecommendedMaximumParticipantCount = 10;
    private const string ProjectionVersion = "meeting-round-v1";
    private const string SummaryProjectionVersion = "meeting-summary-v1";
    private const string EndConfirmationApprovalSuffix = "host-after-summary";
    private const string StartHitlApprovalSuffix = "host-before-round";

    private readonly Func<string, IRuntimeProviderAdapter> _providerFactory;
    private readonly MeetingInvocationExecutor _invocationExecutor;
    private readonly ConversationStore _store;
    private readonly SqliteSessionRepository _sessionRepo;
    private readonly SqliteMeetingRepository _meetingRepo;
    private readonly SqliteEventOutbox _outbox;
    private readonly AgentContextComposer? _agentContextComposer;
    private readonly bool _emitAccepted;
    private readonly long _startRunSequence;
    private readonly Func<MeetingHitlRequest, CancellationToken, Task<MeetingHitlResponse>>? _hitlHandler;
    private readonly ToolGateway? _toolGateway;
    private readonly ToolCatalogSnapshot? _toolCatalogSnapshot;
    private readonly Func<ToolCatalogSnapshot>? _liveCatalogProvider;
    private readonly Func<CancellationToken, Task<SkillCatalogSnapshot?>>? _skillCatalogSnapshotProvider;
    private readonly MeetingHitlResponse? _resumedHitlResponse;
    private readonly MeetingInvocationRecord? _resumedInvocation;
    private readonly ConversationRecordV1? _resumedCanonicalMessage;
    private readonly RuntimeError? _resumedFailure;
    private readonly bool _isHostModeSkills;
    private readonly SkillActivationStore? _skillActivationStore;

    /// <summary>Initializes the meeting orchestrator and its V1 persistence dependencies.</summary>
    /// <param name="providerFactory">Resolves a provider by its ID.</param>
    /// <param name="store">Conversation store for persistence.</param>
    /// <param name="sessionRepo">Session repository for status transitions.</param>
    /// <param name="meetingRepo">Meeting repository for persisting meeting-scoped aggregates.</param>
    /// <param name="outbox">Event outbox for appending run events.</param>
    /// <param name="agentContextComposer">Optional memory-aware context composer.</param>
    /// <param name="emitAccepted">
    /// When <c>true</c> (default), the orchestrator emits a <c>run.accepted</c> envelope at the start.
    /// Set to <c>false</c> when the runtime server has already written <c>run.accepted</c> at sequence 0.
    /// </param>
    /// <param name="startRunSequence">
    /// The first <c>run_sequence</c> value to use. Defaults to 0 for standalone orchestration.
    /// Set to 1 when the runtime server has already consumed sequence 0 for <c>run.accepted</c>.
    /// </param>
    /// <param name="hitlHandler">
    /// Optional human-in-the-loop handler invoked for start-gate HITL and end-confirmation HITL.
    /// The handler owns approval persistence and Waiting state transitions.
    /// </param>
    public MeetingModeOrchestrator(
        Func<string, IRuntimeProviderAdapter> providerFactory,
        ConversationStore store,
        SqliteSessionRepository sessionRepo,
        SqliteMeetingRepository meetingRepo,
        SqliteEventOutbox outbox,
        AgentContextComposer? agentContextComposer = null,
        bool emitAccepted = true,
        long startRunSequence = 0,
        Func<MeetingHitlRequest, CancellationToken, Task<MeetingHitlResponse>>? hitlHandler = null,
        ToolGateway? toolGateway = null,
        ToolCatalogSnapshot? toolCatalogSnapshot = null,
        RuntimeLimits? runtimeLimits = null,
        ToolConsentCoordinator? toolConsentCoordinator = null,
        MeetingHitlResponse? resumedHitlResponse = null,
        MeetingInvocationRecord? resumedInvocation = null,
        ConversationRecordV1? resumedCanonicalMessage = null,
        RuntimeError? resumedFailure = null,
        Func<CancellationToken, Task<SkillCatalogSnapshot?>>? skillCatalogSnapshotProvider = null,
        Func<RuntimeSkillScriptInvocation, CancellationToken, Task<RuntimeSkillScriptResult>>? scriptInvoker = null,
        bool isHostModeSkills = false,
        SkillActivationStore? skillActivationStore = null,
        Func<ToolCatalogSnapshot>? liveCatalogProvider = null)
    {
        ArgumentNullException.ThrowIfNull(providerFactory);
        _providerFactory = providerFactory;
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _isHostModeSkills = isHostModeSkills;
        _skillActivationStore = skillActivationStore;
        _liveCatalogProvider = liveCatalogProvider;
        _invocationExecutor = new MeetingInvocationExecutor(
            new ProviderAgentInvocationExecutor(providerFactory, scriptInvoker),
            store,
            toolGateway,
            toolCatalogSnapshot,
            runtimeLimits,
            toolConsentCoordinator,
            liveCatalogProvider,
            askUserHitlHandler: hitlHandler);
        ArgumentNullException.ThrowIfNull(sessionRepo);
        _sessionRepo = sessionRepo;
        ArgumentNullException.ThrowIfNull(meetingRepo);
        _meetingRepo = meetingRepo;
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _agentContextComposer = agentContextComposer;
        _emitAccepted = emitAccepted;
        _startRunSequence = startRunSequence;
        _hitlHandler = hitlHandler;
        _toolGateway = toolGateway;
        _toolCatalogSnapshot = toolCatalogSnapshot;
        _skillCatalogSnapshotProvider = skillCatalogSnapshotProvider;
        _resumedHitlResponse = resumedHitlResponse;
        _resumedInvocation = resumedInvocation;
        _resumedCanonicalMessage = resumedCanonicalMessage;
        _resumedFailure = resumedFailure;
        if (resumedCanonicalMessage is not null
            && (resumedInvocation is null
                || !string.Equals(
                    resumedCanonicalMessage.InvocationId,
                    resumedInvocation.InvocationId,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "A resumed canonical message must belong to the resumed Invocation.",
                nameof(resumedCanonicalMessage));
        }

        if (resumedFailure is not null && resumedInvocation is null)
        {
            throw new ArgumentException(
                "A resumed failure requires a resumed Invocation.",
                nameof(resumedFailure));
        }
    }

    /// <inheritdoc />
    public RuntimeMode Mode => RuntimeMode.Meeting;

    /// <summary>Runs one deterministic meeting round in participant join order.</summary>
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
        ValidateMeetingRequest(request);

        var options = (MeetingModeOptions)request.Selection.ModeOptions;
        ValidateMeetingToolConfiguration(request, options);
        var effectivePolicy = options.EffectivePolicy;
        var effectiveSelectorPolicy = options.EffectiveSelectorPolicy;

        var activeParticipants = options.Participants
            .Where(p => p.Status == ParticipantStatus.Active)
            .OrderBy(p => p.JoinOrder)
            .ThenBy(p => p.ParticipantId, StringComparer.Ordinal)
            .ToList();

        var maxRounds = effectiveSelectorPolicy.MaxRounds ?? activeParticipants.Count;
        if (maxRounds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Effective max rounds must be greater than 0.");
        }

        long runSequence = _startRunSequence;

        if (_emitAccepted)
        {
            yield return await CreateAcceptedEnvelopeAsync(
                runId,
                sessionId,
                runtimeInstanceId,
                runSequence++,
                ct).ConfigureAwait(false);
        }

        var policyJson = JsonSerializer.Serialize(
            options,
            RuntimeJsonContext.Default.MeetingModeOptions);
        var policyHash = ComputeHash(policyJson);
        var isSelectorDriven = effectiveSelectorPolicy.Type == MeetingSelectorPolicy.SelectorDriven;
        var isHostDriven = effectiveSelectorPolicy.Type == MeetingSelectorPolicy.HostDriven;
        var originalIsHostDriven = isHostDriven;

        MeetingSnapshot snapshot;
        int selectionVersion;
        int runFirstRoundIndex;
        var isContinuationResume = false;
        var isEndConfirmationResume = _resumedHitlResponse is not null
            && IsEndConfirmationApprovalRequestId(_resumedHitlResponse.ApprovalRequestId);
        if (_resumedInvocation is not null)
        {
            snapshot = await _meetingRepo.GetMeetingSnapshotAsync(sessionId, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Meeting session snapshot not found for resumed session '{sessionId}'.");
            ValidateInvocationResumeSnapshot(snapshot, runId, _resumedInvocation);
            selectionVersion = _resumedInvocation.SelectionVersion;
            runFirstRoundIndex = await _meetingRepo.GetRunFirstRoundIndexAsync(sessionId, runId, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"No persisted rounds were found for resumed Meeting Run '{runId}'.");
        }
        else if (_resumedHitlResponse is null)
        {
            var existingSnapshot = await _meetingRepo.GetMeetingSnapshotAsync(sessionId, ct)
                .ConfigureAwait(false);
            if (existingSnapshot is not null
                && string.Equals(existingSnapshot.Session.Status, "Paused", StringComparison.Ordinal))
            {
                // Continuation Run after cooperative pause: claim Paused → Running under the new run id.
                isContinuationResume = true;
                await _meetingRepo.ClaimPausedMeetingForContinuationAsync(sessionId, runId, ct)
                    .ConfigureAwait(false);
                await PersistContinuationResumeInputAsync(
                    sessionId,
                    runId,
                    request.InitialInput,
                    ct).ConfigureAwait(false);

                snapshot = await _meetingRepo.GetMeetingSnapshotAsync(sessionId, ct)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Meeting session snapshot not found after claiming pause for '{sessionId}'.");
                selectionVersion = snapshot.Session.SelectionVersion;
                runFirstRoundIndex = checked(snapshot.Session.CurrentRound + 1);

                // Recovery always starts with Host/Selector so pending interrupts can be absorbed.
                var continuationSchedule = isSelectorDriven
                    ? CreateSelectorSchedule(
                        sessionId,
                        runId,
                        runFirstRoundIndex,
                        selectionVersion,
                        effectiveSelectorPolicy.SelectorAgent ?? BuiltInSelectorAgent)
                    : CreateHostSchedule(
                        sessionId,
                        runId,
                        runFirstRoundIndex,
                        selectionVersion,
                        options.HostAgent ?? BuiltInHostAgent);

                await _meetingRepo.CreateRoundWithFirstInvocationAsync(
                    sessionId,
                    runId,
                    snapshot.Session.CurrentRound,
                    runFirstRoundIndex,
                    continuationSchedule,
                    ct).ConfigureAwait(false);
            }
            else
            {
                await PersistInitialInputAsync(sessionId, runId, request.InitialInput, ct)
                    .ConfigureAwait(false);
                await _meetingRepo.CreateMeetingSessionAsync(
                    sessionId,
                    runId,
                    policyJson,
                    policyHash,
                    CreateParticipantInputs(options),
                    request.Selection.SelectionVersion,
                    ct).ConfigureAwait(false);

                snapshot = await _meetingRepo.GetMeetingSnapshotAsync(sessionId, ct)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Meeting session snapshot not found for session '{sessionId}'.");
                selectionVersion = request.Selection.SelectionVersion;
                runFirstRoundIndex = checked(snapshot.Session.CurrentRound + 1);

                // Existing live session continuation (not pause): rebind active run id.
                if (!string.Equals(snapshot.Session.RunId, runId, StringComparison.Ordinal)
                    && string.Equals(snapshot.Session.Status, "Running", StringComparison.Ordinal))
                {
                    await _meetingRepo.BindActiveRunAsync(sessionId, runId, ct).ConfigureAwait(false);
                }

                var firstSchedule = isSelectorDriven
                    ? CreateSelectorSchedule(
                        sessionId,
                        runId,
                        runFirstRoundIndex,
                        selectionVersion,
                        effectiveSelectorPolicy.SelectorAgent ?? BuiltInSelectorAgent)
                    : isHostDriven
                        ? CreateHostSchedule(
                            sessionId,
                            runId,
                            runFirstRoundIndex,
                            selectionVersion,
                            options.HostAgent ?? BuiltInHostAgent)
                        : CreateParticipantRoleSchedule(
                            sessionId,
                            runId,
                            runFirstRoundIndex,
                            selectionVersion,
                            activeParticipants[
                                (runFirstRoundIndex - 1) % activeParticipants.Count]);

                await _meetingRepo.CreateRoundWithFirstInvocationAsync(
                    sessionId,
                    runId,
                    snapshot.Session.CurrentRound,
                    runFirstRoundIndex,
                    firstSchedule,
                    ct).ConfigureAwait(false);
            }
        }
        else
        {
            snapshot = await _meetingRepo.GetMeetingSnapshotAsync(sessionId, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Meeting session snapshot not found for resumed session '{sessionId}'.");
            runFirstRoundIndex = await _meetingRepo.GetRunFirstRoundIndexAsync(sessionId, runId, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"No persisted rounds were found for resumed Meeting Run '{runId}'.");
            if (isEndConfirmationResume)
            {
                ValidateEndConfirmationHitlResumeSnapshot(snapshot, runId, _resumedHitlResponse);
            }
            else
            {
                ValidateHitlResumeSnapshot(
                    snapshot,
                    runId,
                    runFirstRoundIndex,
                    _resumedHitlResponse);
            }

            selectionVersion = snapshot.Session.SelectionVersion;
        }

        // Pause continuation forces HostDriven decision parsing when the original policy was RoundRobin.
        if (isContinuationResume && !originalIsHostDriven && !isSelectorDriven)
        {
            isHostDriven = true;
        }

        var lastRoundIndex = checked(runFirstRoundIndex + maxRounds - 1);
        var meetingDeadline = effectivePolicy.MeetingTimeoutSeconds is > 0
            ? snapshot.Session.CreatedAt.AddSeconds(effectivePolicy.MeetingTimeoutSeconds.Value)
            : (DateTimeOffset?)null;

        if (effectivePolicy.HitlEnabled
            && _resumedInvocation is null
            && !isEndConfirmationResume
            && !isContinuationResume)
        {
            if (_resumedHitlResponse is null && _hitlHandler is null)
            {
                await FailMeetingAndRunAsync(
                    sessionId, runId,
                    "HITL is enabled but no hitlHandler was provided.",
                    CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "HITL is enabled but no hitlHandler was provided.");
            }

            var approvalRequestId = CreateStableId(
                "meeting-hitl-v1",
                sessionId,
                runId,
                runFirstRoundIndex.ToString(CultureInfo.InvariantCulture),
                StartHitlApprovalSuffix);
            var hostInvocationId = CreateStableId(
                "meeting-host-v1",
                sessionId,
                runId,
                runFirstRoundIndex.ToString(CultureInfo.InvariantCulture));

            var hitlNow = DateTimeOffset.UtcNow;
            DateTimeOffset? hitlExpiresAt = effectivePolicy.HitlTimeoutSeconds is > 0
                ? hitlNow.AddSeconds(effectivePolicy.HitlTimeoutSeconds.Value)
                : null;

            var hitlRequest = new MeetingHitlRequest(
                approvalRequestId,
                sessionId,
                runId,
                RoundIndex: runFirstRoundIndex,
                HostInvocationId: hostInvocationId,
                Prompt: "meeting.hitl.question.round:" + runFirstRoundIndex.ToString(CultureInfo.InvariantCulture),
                Context: request.InitialInput,
                CreatedAt: hitlNow,
                ExpiresAt: hitlExpiresAt);

            yield return await CreateStatusEnvelopeAsync(
                runtimeInstanceId,
                runId,
                runSequence++,
                RunStatus.WaitingForApproval,
                currentStep: $"round:{runFirstRoundIndex}:approval",
                completedUnits: 0,
                totalUnits: maxRounds,
                ct).ConfigureAwait(false);

            MeetingHitlResponse hitlResponse;
            if (_resumedHitlResponse is not null)
            {
                hitlResponse = _resumedHitlResponse;
            }
            else
            {
                try
                {
                    hitlResponse = await _hitlHandler!(hitlRequest, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }

            if (hitlResponse is null
                || !string.Equals(hitlResponse.ApprovalRequestId, approvalRequestId, StringComparison.Ordinal))
            {
                await FailMeetingAndRunAsync(
                    sessionId, runId,
                    "HITL handler returned a null or mismatched response.",
                    CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "HITL handler returned a null or mismatched response.");
            }

            switch (hitlResponse.Action)
            {
                case MeetingHitlAction.Approve:
                    await ResumeFromPersistedHitlAsync(sessionId, runId, ct).ConfigureAwait(false);
                    break;

                case MeetingHitlAction.Supplement:
                    if (string.IsNullOrWhiteSpace(hitlResponse.Supplement))
                    {
                        await FailMeetingAndRunAsync(
                            sessionId, runId,
                            "HITL supplement action returned with a blank supplement.",
                            CancellationToken.None).ConfigureAwait(false);
                        throw new InvalidOperationException(
                            "HITL supplement action returned with a blank supplement.");
                    }

                    await PersistHitlSupplementAsync(
                        sessionId,
                        runId,
                        hitlResponse.ApprovalRequestId,
                        hitlResponse.Supplement,
                        ct).ConfigureAwait(false);
                    await ResumeFromPersistedHitlAsync(sessionId, runId, ct).ConfigureAwait(false);
                    break;

                case MeetingHitlAction.Reject:
                case MeetingHitlAction.Cancel:
                    var cancellationReason = hitlResponse.Reason ?? "Cancelled by HITL.";
                    await CancelMeetingAndRunAsync(
                        sessionId,
                        runId,
                        cancellationReason,
                        CancellationToken.None,
                        transitionRun: false).ConfigureAwait(false);
                    var cancelledEnvelope = await CommitTerminalEnvelopeAsync(
                        runtimeInstanceId,
                        runId,
                        runSequence++,
                        RunStatus.Cancelled,
                        cancellationReason,
                        error: null,
                        value: new RunCancelledEvent(runId, sessionId),
                        typeInfo: RuntimeJsonContext.Default.RunCancelledEvent,
                        ct: ct).ConfigureAwait(false);
                    if (cancelledEnvelope is not null)
                    {
                        yield return cancelledEnvelope;
                    }
                    yield break;

                default:
                    await FailMeetingAndRunAsync(
                        sessionId, runId,
                        $"Unknown HITL action '{hitlResponse.Action}'.",
                        CancellationToken.None).ConfigureAwait(false);
                    throw new InvalidOperationException(
                        $"Unknown HITL action '{hitlResponse.Action}'.");
            }
        }

        List<string> successfulTexts = _resumedInvocation is null && !isEndConfirmationResume
            ? []
            : await LoadSuccessfulParticipantTextsAsync(sessionId, ct).ConfigureAwait(false);
        MeetingContextStrategy? projectionStrategyOverride = null;

        async Task<(bool Succeeded, string? Text, RuntimeError? Error, List<RuntimeEventEnvelope> Events)> ExecuteInvocationAsync(
            AgentInvocationRequest invocationRequest)
        {
            var collectedEvents = new List<RuntimeEventEnvelope>();
            if (_resumedFailure is not null
                && string.Equals(
                    _resumedInvocation?.InvocationId,
                    invocationRequest.InvocationId,
                    StringComparison.Ordinal))
            {
                return (false, null, _resumedFailure, collectedEvents);
            }

            using var invocationTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            invocationTimeout.CancelAfter(
                TimeSpan.FromSeconds(effectivePolicy.InvocationTimeoutSeconds));

            MeetingInvocationExecutionResult execution;
            try
            {
                execution = await _invocationExecutor.ExecuteAsync(
                    invocationRequest,
                    effectivePolicy.MaxRetriesPerInvocation,
                    invocationTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (!ct.IsCancellationRequested && invocationTimeout.IsCancellationRequested)
            {
                execution = new MeetingInvocationExecutionResult(
                    false,
                    null,
                    new RuntimeError(
                        RuntimeErrorCodes.ProviderTimeout,
                        "orchestration",
                        $"Invocation '{invocationRequest.InvocationId}' exceeded the configured "
                        + $"{effectivePolicy.InvocationTimeoutSeconds}-second timeout.",
                        IsRetryable: false,
                        ProviderDetails: null,
                        Guid.NewGuid().ToString("N")),
                    []);
            }
            foreach (var providerEvent in execution.Events)
            {
                collectedEvents.Add(await CreateProviderEventEnvelopeAsync(
                    runId,
                    runtimeInstanceId,
                    providerEvent,
                    runSequence++,
                    ct).ConfigureAwait(false));
            }

            return (
                execution.Succeeded,
                execution.Text,
                execution.Error,
                collectedEvents);
        }

        async Task<(List<RuntimeEventEnvelope> Events, RuntimeError? FatalError)> CompleteRoleStepAsync(
            string completedInvocationId,
            string? messageId,
            string? decisionJson,
            int roundIndex,
            int selectionVersion,
            MeetingInvocationScheduleInput? nextRoundSchedule,
            bool conclusionWithoutParticipant = false,
            MeetingInvocationRecord? resumedSummary = null)
        {
            var events = new List<RuntimeEventEnvelope>();
            var isFinalRound = nextRoundSchedule is null;
            var shouldSummarize = resumedSummary is not null || (conclusionWithoutParticipant
                ? effectivePolicy.SummaryMode == MeetingSummaryMode.FinalOnly
                : effectivePolicy.SummaryMode == MeetingSummaryMode.PerRound
                    || (effectivePolicy.SummaryMode == MeetingSummaryMode.FinalOnly && isFinalRound));

            if (!shouldSummarize)
            {
                if (nextRoundSchedule is null)
                {
                    await _meetingRepo.CompleteInvocationAsync(
                        completedInvocationId, "Completed", messageId,
                        decisionJson, null, null, null, ct).ConfigureAwait(false);
                }
                else
                {
                    await _meetingRepo.CompleteInvocationAndStartNextRoundAsync(
                        completedInvocationId, "Completed", messageId,
                        null, null, roundIndex + 1, nextRoundSchedule, ct).ConfigureAwait(false);
                }

                events.Add(await CreateInvocationCompletedEnvelopeAsync(
                    runId, runtimeInstanceId, completedInvocationId, runSequence++, ct)
                    .ConfigureAwait(false));
                return (events, null);
            }

            MeetingInvocationScheduleInput summarySchedule;
            if (resumedSummary is null)
            {
                var summarizerAgent = effectivePolicy.Summarizer ?? BuiltInSummarizerAgent;
                summarySchedule = CreateSummarizerSchedule(
                    sessionId, runId, roundIndex, selectionVersion, summarizerAgent);
                await _meetingRepo.CompleteInvocationAsync(
                    completedInvocationId, "Completed", messageId,
                    decisionJson, null, null, summarySchedule, ct).ConfigureAwait(false);
                events.Add(await CreateInvocationCompletedEnvelopeAsync(
                    runId, runtimeInstanceId, completedInvocationId, runSequence++, ct)
                    .ConfigureAwait(false));

                await _meetingRepo.TransitionInvocationStatusAsync(
                    summarySchedule.InvocationId, "Scheduled", "Running", ct).ConfigureAwait(false);
            }
            else
            {
                summarySchedule = new MeetingInvocationScheduleInput(
                    resumedSummary.InvocationId,
                    resumedSummary.ParticipantId,
                    resumedSummary.AgentId,
                    resumedSummary.Role ?? "summarizer",
                    resumedSummary.SelectionVersion);
            }

            var reconciledSummary = resumedSummary is null
                ? null
                : GetReconciledCanonicalText(resumedSummary, expectsSummary: true);
            if (reconciledSummary is not null)
            {
                if (reconciledSummary.Record.SummaryMetadata is not { } metadata
                    || !TryReadSummaryMetadata(metadata, out var summaryMetadata)
                    || summaryMetadata.RoundIndex != roundIndex
                    || !string.Equals(summaryMetadata.PolicyHash, policyHash, StringComparison.Ordinal)
                    || !string.Equals(
                        summaryMetadata.SummarizerAgentId,
                        resumedSummary!.AgentId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        summaryMetadata.SummarizerInvocationId,
                        resumedSummary.InvocationId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Summarizer Invocation '{resumedSummary!.InvocationId}' has invalid canonical metadata.");
                }

                await _meetingRepo.SaveRoundSummaryAsync(
                    sessionId,
                    roundIndex,
                    summaryMetadata.SummarizesThroughSeq,
                    policyHash,
                    reconciledSummary.Record.MessageId,
                    resumedSummary.AgentId,
                    resumedSummary.InvocationId,
                    ct).ConfigureAwait(false);
                if (nextRoundSchedule is null)
                {
                    await _meetingRepo.CompleteInvocationAsync(
                        resumedSummary.InvocationId,
                        "Completed",
                        reconciledSummary.Record.MessageId,
                        null,
                        null,
                        null,
                        null,
                        ct).ConfigureAwait(false);
                }
                else
                {
                    await _meetingRepo.CompleteInvocationAndStartNextRoundAsync(
                        resumedSummary.InvocationId,
                        "Completed",
                        reconciledSummary.Record.MessageId,
                        null,
                        null,
                        roundIndex + 1,
                        nextRoundSchedule,
                        ct).ConfigureAwait(false);
                }

                events.Add(await CreateInvocationCompletedEnvelopeAsync(
                    runId,
                    runtimeInstanceId,
                    resumedSummary.InvocationId,
                    runSequence++,
                    ct).ConfigureAwait(false));
                return (events, null);
            }

            var summarizesThroughSeq = await _store.GetLastSequenceAsync(sessionId, ct)
                .ConfigureAwait(false);
            var summaryContext = await CreateSummarizerInvocationRequestAsync(
                runId,
                sessionId,
                request,
                options,
                roundIndex,
                summarySchedule.InvocationId,
                summarizesThroughSeq,
                ct).ConfigureAwait(false);
            if (resumedSummary is not null)
            {
                summaryContext = await UsePersistedInvocationSnapshotAsync(
                    summaryContext,
                    resumedSummary,
                    ct).ConfigureAwait(false);
            }

            events.Add(await CreateInvocationStartedEnvelopeAsync(
                summaryContext.Request, runtimeInstanceId, runSequence++, ct).ConfigureAwait(false));
            if (summaryContext.Projection.IsAdjusted)
            {
                events.Add(await CreateProjectionAdjustedEnvelopeAsync(
                    runId,
                    runtimeInstanceId,
                    summaryContext.Request.InvocationId,
                    summaryContext.Projection,
                    runSequence++,
                    ct).ConfigureAwait(false));
            }

            var cachedSummary = await TryGetCachedSummaryAsync(
                sessionId,
                roundIndex,
                policyHash,
                summaryContext.Request,
                ct).ConfigureAwait(false);
            if (cachedSummary is not null)
            {
                if (nextRoundSchedule is null)
                {
                    await _meetingRepo.CompleteInvocationAsync(
                        summaryContext.Request.InvocationId,
                        "Completed",
                        cachedSummary.SummaryMessageId,
                        null,
                        null,
                        null,
                        null,
                        ct).ConfigureAwait(false);
                }
                else
                {
                    await _meetingRepo.CompleteInvocationAndStartNextRoundAsync(
                        summaryContext.Request.InvocationId,
                        "Completed",
                        cachedSummary.SummaryMessageId,
                        null,
                        null,
                        roundIndex + 1,
                        nextRoundSchedule,
                        ct).ConfigureAwait(false);
                }

                events.Add(await CreateInvocationCompletedEnvelopeAsync(
                    runId,
                    runtimeInstanceId,
                    summaryContext.Request.InvocationId,
                    runSequence++,
                    ct).ConfigureAwait(false));
                return (events, null);
            }

            var summaryResult = await ExecuteInvocationAsync(summaryContext.Request).ConfigureAwait(false);
            events.AddRange(summaryResult.Events);
            if (summaryResult.Succeeded && !string.IsNullOrWhiteSpace(summaryResult.Text))
            {
                var summaryMessageId = CreateStableId(
                    "meeting-summary-message-v1",
                    sessionId,
                    roundIndex.ToString(CultureInfo.InvariantCulture),
                    summarizesThroughSeq.ToString(CultureInfo.InvariantCulture),
                    policyHash);
                await PersistSummaryMessageAsync(
                    sessionId,
                    summaryContext.Request,
                    summaryResult.Text,
                    summaryMessageId,
                    roundIndex,
                    summarizesThroughSeq,
                    policyHash,
                    ct).ConfigureAwait(false);
                await _meetingRepo.SaveRoundSummaryAsync(
                    sessionId,
                    roundIndex,
                    summarizesThroughSeq,
                    policyHash,
                    summaryMessageId,
                    summaryContext.Request.AgentId,
                    summaryContext.Request.InvocationId,
                    ct).ConfigureAwait(false);

                if (nextRoundSchedule is null)
                {
                    await _meetingRepo.CompleteInvocationAsync(
                        summaryContext.Request.InvocationId, "Completed", summaryMessageId,
                        null, null, null, null, ct).ConfigureAwait(false);
                }
                else
                {
                    await _meetingRepo.CompleteInvocationAndStartNextRoundAsync(
                        summaryContext.Request.InvocationId, "Completed", summaryMessageId,
                        null, null, roundIndex + 1, nextRoundSchedule, ct).ConfigureAwait(false);
                }

                events.Add(await CreateInvocationCompletedEnvelopeAsync(
                    runId,
                    runtimeInstanceId,
                    summaryContext.Request.InvocationId,
                    runSequence++,
                    ct).ConfigureAwait(false));
                return (events, null);
            }

            var summaryError = summaryResult.Error ?? new RuntimeError(
                "summary_empty",
                "orchestration",
                "Summarizer completed without producing a summary.",
                IsRetryable: false,
                ProviderDetails: null,
                Guid.NewGuid().ToString("N"));
            events.Add(await CreateEnvelopeAsync(
                runtimeInstanceId,
                runId,
                runSequence++,
                MessageTypes.InvocationFailed,
                new InvocationFailedEvent(summaryContext.Request.InvocationId, summaryError),
                RuntimeJsonContext.Default.InvocationFailedEvent,
                ct).ConfigureAwait(false));

            var contextFallback = options.EffectiveContextPolicy.SummaryFailureFallback;
            if (effectivePolicy.SummarizerFailure == MeetingSummarizerFailurePolicy.Fail
                || contextFallback == MeetingSummaryFallbackStrategy.Fail)
            {
                await _meetingRepo.CompleteInvocationAsync(
                    summaryContext.Request.InvocationId, "Failed", null, null,
                    summaryError.Code, summaryError.Message, null, ct).ConfigureAwait(false);
                await _meetingRepo.CompleteRoundAndMeetingAsync(
                    sessionId, roundIndex, "Failed", CancellationToken.None).ConfigureAwait(false);
                await _sessionRepo.TransitionRunToTerminalAsync(
                    runId, RunStatus.Running, RunStatus.Failed, summaryError.Message,
                    CancellationToken.None).ConfigureAwait(false);
                return (events, summaryError);
            }

            projectionStrategyOverride = effectivePolicy.SummarizerFailure ==
                MeetingSummarizerFailurePolicy.FallbackFull
                || contextFallback == MeetingSummaryFallbackStrategy.Full
                    ? MeetingContextStrategy.Full
                    : MeetingContextStrategy.TailWindow;
            var fallbackHistory = await _store.ReadAllAsync(sessionId, ct).ConfigureAwait(false);
            events.Add(await CreateEnvelopeAsync(
                runtimeInstanceId,
                runId,
                runSequence++,
                MessageTypes.ContextProjectionAdjusted,
                new ContextProjectionAdjustedEvent(
                    summaryContext.Request.InvocationId,
                    DroppedMessageCount: 0,
                    IncludedMessageCount: fallbackHistory.Count,
                    EstimatedTokens: 0,
                    EstimateSource: "runtime.summary-failed",
                    Strategy: projectionStrategyOverride.Value.ToString()),
                RuntimeJsonContext.Default.ContextProjectionAdjustedEvent,
                ct).ConfigureAwait(false));

            if (nextRoundSchedule is null)
            {
                await _meetingRepo.CompleteInvocationAsync(
                    summaryContext.Request.InvocationId, "Skipped", null, null,
                    summaryError.Code, summaryError.Message, null, ct).ConfigureAwait(false);
            }
            else
            {
                await _meetingRepo.CompleteInvocationAndStartNextRoundAsync(
                    summaryContext.Request.InvocationId, "Skipped", null,
                    summaryError.Code, summaryError.Message,
                    roundIndex + 1, nextRoundSchedule, ct).ConfigureAwait(false);
            }

            return (events, null);
        }

        var completedRoundIndex = lastRoundIndex;
        var currentActiveParticipants = activeParticipants;
        var currentSelectionVersion = selectionVersion;
        if (isEndConfirmationResume)
        {
            completedRoundIndex = snapshot.Session.CurrentRound > 0
                ? snapshot.Session.CurrentRound
                : lastRoundIndex;
            (currentActiveParticipants, currentSelectionVersion) =
                await ReloadActiveParticipantsAsync(sessionId, options, ct).ConfigureAwait(false);
        }
        else
        {
        if (_resumedInvocation is not null)
        {
            (currentActiveParticipants, _) = await ReloadActiveParticipantsAsync(
                sessionId,
                options,
                ct).ConfigureAwait(false);
        }

        var pendingResume = _resumedInvocation;
        var firstRoundIndex = pendingResume?.RoundIndex ?? runFirstRoundIndex;

        if (pendingResume is { } resumedSummary
            && string.Equals(resumedSummary.Role, "summarizer", StringComparison.OrdinalIgnoreCase))
        {
            MeetingInvocationScheduleInput? nextRoundSchedule = null;
            var summaryConcludesMeeting = effectivePolicy.SummaryMode == MeetingSummaryMode.FinalOnly
                && resumedSummary.RoundIndex < lastRoundIndex;
            if (!summaryConcludesMeeting && resumedSummary.RoundIndex < lastRoundIndex)
            {
                (currentActiveParticipants, currentSelectionVersion) = await ReloadActiveParticipantsAsync(
                    sessionId,
                    options,
                    ct).ConfigureAwait(false);
                if (currentActiveParticipants.Count == 0)
                {
                    throw new InvalidOperationException(
                        "No active participants remain in the durable meeting snapshot.");
                }

                nextRoundSchedule = isSelectorDriven || isHostDriven
                    ? CreateDecisionRoleSchedule(
                        sessionId,
                        runId,
                        resumedSummary.RoundIndex + 1,
                        currentSelectionVersion,
                        options,
                        isHostDriven)
                    : CreateParticipantRoleSchedule(
                        sessionId,
                        runId,
                        resumedSummary.RoundIndex + 1,
                        currentSelectionVersion,
                        currentActiveParticipants[
                            resumedSummary.RoundIndex % currentActiveParticipants.Count]);
            }

            var summaryStep = await CompleteRoleStepAsync(
                resumedSummary.InvocationId,
                messageId: null,
                decisionJson: null,
                resumedSummary.RoundIndex,
                resumedSummary.SelectionVersion,
                nextRoundSchedule,
                resumedSummary: resumedSummary).ConfigureAwait(false);
            foreach (var summaryEvent in summaryStep.Events)
            {
                yield return summaryEvent;
            }

            if (summaryStep.FatalError is not null)
            {
                throw new InvalidOperationException(
                    $"Summarizer invocation failed in round {resumedSummary.RoundIndex}: "
                    + summaryStep.FatalError.Message);
            }

            pendingResume = null;
            if (nextRoundSchedule is null)
            {
                completedRoundIndex = resumedSummary.RoundIndex;
                firstRoundIndex = lastRoundIndex + 1;
            }
            else
            {
                firstRoundIndex = resumedSummary.RoundIndex + 1;
            }
        }

        for (var roundIndex = firstRoundIndex; roundIndex <= lastRoundIndex; roundIndex++)
        {
            ct.ThrowIfCancellationRequested();
            await DrainPendingInputsAtSafePointAsync(sessionId, runId, ct).ConfigureAwait(false);
            if (await TryPauseAtSafePointAsync(
                    sessionId,
                    runId,
                    runtimeInstanceId,
                    () => runSequence++,
                    ct).ConfigureAwait(false) is { } pauseEnvelope)
            {
                yield return pauseEnvelope;
                yield break;
            }

            var isLastRound = roundIndex == lastRoundIndex;

            if (isSelectorDriven || isHostDriven)
            {
                MeetingParticipant? selected = null;
                MeetingInvocationScheduleInput participantSchedule;
                var resumedParticipant = pendingResume is
                {
                    Role: not null
                } recovery
                    && recovery.RoundIndex == roundIndex
                    && string.Equals(recovery.Role, "participant", StringComparison.OrdinalIgnoreCase)
                        ? recovery
                        : null;
                if (resumedParticipant is null)
                {
                    var resumedDecision = pendingResume is not null
                        && pendingResume.RoundIndex == roundIndex
                            ? pendingResume
                            : null;
                    var expectedRole = isHostDriven ? "host" : "selector";
                    if (resumedDecision is not null
                        && !string.Equals(
                            resumedDecision.Role,
                            expectedRole,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Invocation '{resumedDecision.InvocationId}' has role '{resumedDecision.Role}' "
                            + $"and cannot resume the {expectedRole} step.");
                    }

                    var selectorInvocationId = resumedDecision?.InvocationId
                        ?? CreateRoleInvocationId(
                            sessionId,
                            runId,
                            roundIndex,
                            expectedRole,
                            string.Empty,
                            currentSelectionVersion);

                    if (resumedDecision is null)
                    {
                        await _meetingRepo.TransitionInvocationStatusAsync(
                            selectorInvocationId,
                            "Scheduled",
                            "Running",
                            ct).ConfigureAwait(false);
                    }

                    var selectorContext = isHostDriven
                    ? await CreateHostInvocationRequestAsync(
                        runId,
                        sessionId,
                        request,
                        options,
                        currentActiveParticipants,
                        roundIndex,
                        projectionStrategyOverride,
                        selectorInvocationId,
                        ct).ConfigureAwait(false)
                    : await CreateSelectorInvocationRequestAsync(
                        runId,
                        sessionId,
                        request,
                        options,
                        currentActiveParticipants,
                        roundIndex,
                        projectionStrategyOverride,
                        selectorInvocationId,
                        ct).ConfigureAwait(false);
                    if (resumedDecision is not null)
                    {
                        selectorContext = await UsePersistedInvocationSnapshotAsync(
                            selectorContext,
                            resumedDecision,
                            ct).ConfigureAwait(false);
                        pendingResume = null;
                    }

                    var selectorRequest = selectorContext.Request;

                yield return await CreateInvocationStartedEnvelopeAsync(
                    selectorRequest, runtimeInstanceId, runSequence++, ct).ConfigureAwait(false);
                yield return await CreateStatusEnvelopeAsync(
                    runtimeInstanceId,
                    runId,
                    runSequence++,
                    RunStatus.Running,
                    currentStep: $"round:{roundIndex}:{(isHostDriven ? "host" : "selector")}",
                    completedUnits: Math.Clamp(roundIndex - runFirstRoundIndex, 0, maxRounds),
                    totalUnits: maxRounds,
                    ct).ConfigureAwait(false);
                if (selectorContext.Projection.IsAdjusted)
                {
                    yield return await CreateProjectionAdjustedEnvelopeAsync(
                        runId, runtimeInstanceId, selectorRequest.InvocationId,
                        selectorContext.Projection, runSequence++, ct).ConfigureAwait(false);
                }

                var selectorResult = await ExecuteInvocationAsync(selectorRequest).ConfigureAwait(false);

                foreach (var evt in selectorResult.Events)
                {
                    yield return evt;
                }

                (currentActiveParticipants, currentSelectionVersion) = await ReloadActiveParticipantsAsync(
                    sessionId, options, ct).ConfigureAwait(false);
                if (currentActiveParticipants.Count == 0)
                {
                    throw new InvalidOperationException(
                        "No active participants remain in the durable meeting snapshot.");
                }

                string decisionSource;

                if (isHostDriven && selectorResult.Succeeded)
                {
                    var endConfirmedAfterHost = false;
                    var hostSnapshot = await _meetingRepo.GetMeetingSnapshotAsync(sessionId, ct)
                        .ConfigureAwait(false);
                    if (hostSnapshot?.Session.EndConfirmed == true)
                    {
                        endConfirmedAfterHost = true;
                    }

                    var parsed = TryResolveHostDecision(
                        selectorResult.Text,
                        currentActiveParticipants,
                        out selected,
                        out var conclude);
                    if (endConfirmedAfterHost)
                    {
                        conclude = true;
                        parsed = true;
                        selected = null;
                    }

                    string? hostMessageId = null;
                    if (!string.IsNullOrWhiteSpace(selectorResult.Text))
                    {
                        hostMessageId = CreateStableId(
                            "meeting-host-message-v1",
                            selectorInvocationId);
                        await PersistAssistantMessageAsync(
                            sessionId,
                            selectorRequest,
                            selectorResult.Text!,
                            hostMessageId,
                            ct).ConfigureAwait(false);
                    }

                    if (parsed && conclude)
                    {
                        var conclusionStep = await CompleteRoleStepAsync(
                            selectorInvocationId,
                            hostMessageId,
                            CreateHostDecisionJson(null, conclude: true),
                            roundIndex,
                            currentSelectionVersion,
                            nextRoundSchedule: null,
                            conclusionWithoutParticipant: true).ConfigureAwait(false);
                        foreach (var conclusionEvent in conclusionStep.Events)
                        {
                            yield return conclusionEvent;
                        }

                        if (conclusionStep.FatalError is not null)
                        {
                            throw new InvalidOperationException(
                                $"Summarizer invocation failed after the Host concluded round {roundIndex}: "
                                + conclusionStep.FatalError.Message);
                        }

                        completedRoundIndex = roundIndex;
                        break;
                    }

                    if (!parsed || selected is null)
                    {
                        // Real models often speak without a strict decision JSON; fall back to
                        // deterministic next participant instead of parking the whole meeting.
                        selected = SelectDeterministicFallback(
                            currentActiveParticipants,
                            roundIndex);
                        parsed = selected is not null;
                        conclude = false;
                    }

                    if (!parsed || selected is null)
                    {
                        selectorResult = (
                            false,
                            null,
                            new RuntimeError(
                                "host_decision_invalid",
                                "orchestration",
                                "Host output did not contain a valid participantId or conclude decision.",
                                IsRetryable: false,
                                ProviderDetails: null,
                                Guid.NewGuid().ToString("N")),
                            selectorResult.Events);
                    }
                    else
                    {
                        participantSchedule = CreateParticipantRoleSchedule(
                            sessionId,
                            runId,
                            roundIndex,
                            currentSelectionVersion,
                            selected);
                        var hostDecisionJson = CreateHostDecisionJson(
                            selected.ParticipantId,
                            conclude: false);
                        await _meetingRepo.CompleteInvocationAsync(
                            selectorInvocationId,
                            "Completed",
                            hostMessageId,
                            hostDecisionJson,
                            null,
                            null,
                            participantSchedule,
                            ct).ConfigureAwait(false);

                        yield return await CreateInvocationCompletedEnvelopeAsync(
                            runId,
                            runtimeInstanceId,
                            selectorInvocationId,
                            runSequence++,
                            ct).ConfigureAwait(false);

                        decisionSource = "host";
                        goto HostParticipantReady;
                    }
                }

                if (selectorResult.Succeeded)
                {
                    selected = isHostDriven
                        ? selected
                        : !string.IsNullOrEmpty(selectorResult.Text)
                            ? TryResolveSelectorParticipant(selectorResult.Text!, currentActiveParticipants)
                            : null;
                    if (selected is null)
                    {
                        selected = SelectDeterministicFallback(currentActiveParticipants, roundIndex);
                        decisionSource = "fallback";
                    }
                    else
                    {
                        decisionSource = isHostDriven ? "host" : "selector";
                    }

                    participantSchedule = CreateParticipantRoleSchedule(sessionId, runId, roundIndex, currentSelectionVersion, selected!);
                    var selectorDecisionJson = isHostDriven
                        ? CreateHostDecisionJson(selected!.ParticipantId, conclude: false)
                        : CreateSelectorDecisionJson(selected!.ParticipantId, decisionSource);

                    await _meetingRepo.CompleteInvocationAsync(
                        selectorInvocationId, "Completed", null, selectorDecisionJson,
                        null, null, participantSchedule, ct).ConfigureAwait(false);

                    yield return await CreateInvocationCompletedEnvelopeAsync(
                        runId, runtimeInstanceId, selectorInvocationId, runSequence++, ct).ConfigureAwait(false);
                }
                else
                {
                    var selectorError = selectorResult.Error ?? new RuntimeError(
                        "selector_failed", "orchestration", "Selector invocation failed.",
                        IsRetryable: false, ProviderDetails: null, Guid.NewGuid().ToString("N"));

                    if (isHostDriven
                        && effectivePolicy.HostFailure == MeetingHostFailurePolicy.Pause)
                    {
                        await _meetingRepo.PauseInvocationAsync(
                            selectorInvocationId,
                            selectorError.Code,
                            selectorError.Message,
                            CancellationToken.None).ConfigureAwait(false);
                        yield return await CreateEnvelopeAsync(
                            runtimeInstanceId, runId, runSequence++, MessageTypes.InvocationFailed,
                            new InvocationFailedEvent(selectorInvocationId, selectorError),
                            RuntimeJsonContext.Default.InvocationFailedEvent, ct).ConfigureAwait(false);

                        // PauseInvocation parks the Run in WaitingForApproval; emit an Interrupted
                        // terminal so hosts/CLI/smoke do not hang waiting for a completion event.
                        var pauseReason = selectorError.Message;
                        try
                        {
                            await _sessionRepo.TransitionRunToTerminalAsync(
                                runId,
                                RunStatus.WaitingForApproval,
                                RunStatus.Interrupted,
                                pauseReason,
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (InvalidOperationException)
                        {
                            await _sessionRepo.TransitionRunToTerminalAsync(
                                runId,
                                RunStatus.Running,
                                RunStatus.Interrupted,
                                pauseReason,
                                CancellationToken.None).ConfigureAwait(false);
                        }

                        var hostPauseEnvelope = await CommitTerminalEnvelopeAsync(
                            runtimeInstanceId,
                            runId,
                            runSequence++,
                            RunStatus.Interrupted,
                            pauseReason,
                            error: null,
                            value: new RunCancelledEvent(runId, sessionId),
                            typeInfo: RuntimeJsonContext.Default.RunCancelledEvent,
                            ct: ct).ConfigureAwait(false);
                        if (hostPauseEnvelope is not null)
                        {
                            yield return hostPauseEnvelope;
                        }

                        yield break;
                    }

                    if (isHostDriven
                        || effectivePolicy.SelectorFailure is MeetingSelectorFailurePolicy.Pause
                            or MeetingSelectorFailurePolicy.Fail)
                    {
                        await _meetingRepo.CompleteInvocationAsync(
                            selectorInvocationId, "Failed", null, null,
                            selectorError.Code, selectorError.Message, null, ct).ConfigureAwait(false);

                        await _meetingRepo.CompleteRoundAndMeetingAsync(
                            sessionId, roundIndex, "Failed", CancellationToken.None).ConfigureAwait(false);

                        await _sessionRepo.TransitionRunToTerminalAsync(
                            runId, RunStatus.Running, RunStatus.Failed, selectorError.Message,
                            CancellationToken.None).ConfigureAwait(false);

                        yield return await CreateEnvelopeAsync(
                            runtimeInstanceId, runId, runSequence++, MessageTypes.InvocationFailed,
                            new InvocationFailedEvent(selectorInvocationId, selectorError),
                            RuntimeJsonContext.Default.InvocationFailedEvent, ct).ConfigureAwait(false);

                        var roleName = isHostDriven ? "Host" : "Selector";
                        throw new InvalidOperationException(
                            $"{roleName} invocation failed in round {roundIndex}: {selectorError.Message}");
                    }

                    selected = SelectDeterministicFallback(currentActiveParticipants, roundIndex);
                    participantSchedule = CreateParticipantRoleSchedule(sessionId, runId, roundIndex, currentSelectionVersion, selected!);

                    await _meetingRepo.CompleteInvocationAsync(
                        selectorInvocationId, "Skipped", null, null,
                        selectorError.Code, selectorError.Message, participantSchedule, ct).ConfigureAwait(false);

                    yield return await CreateEnvelopeAsync(
                        runtimeInstanceId, runId, runSequence++, MessageTypes.InvocationFailed,
                        new InvocationFailedEvent(selectorInvocationId, selectorError),
                        RuntimeJsonContext.Default.InvocationFailedEvent, ct).ConfigureAwait(false);
                }
                }
                else
                {
                    selected = ResolveRecoveredParticipant(snapshot, options, resumedParticipant);
                    participantSchedule = new MeetingInvocationScheduleInput(
                        resumedParticipant.InvocationId,
                        resumedParticipant.ParticipantId,
                        resumedParticipant.AgentId,
                        "participant",
                        resumedParticipant.SelectionVersion);
                }

            HostParticipantReady:
                selected = selected ?? throw new InvalidOperationException(
                    $"Selector did not select a participant in round {roundIndex}.");

                var participantInvocationId = participantSchedule.InvocationId;

                if (resumedParticipant is null)
                {
                    await _meetingRepo.TransitionInvocationStatusAsync(
                        participantInvocationId, "Scheduled", "Running", ct).ConfigureAwait(false);
                }

                var reconciledParticipant = resumedParticipant is null
                    ? null
                    : GetReconciledCanonicalText(resumedParticipant, expectsSummary: false);
                AgentInvocationRequest? participantRequest = null;
                (bool Succeeded, string? Text, RuntimeError? Error, List<RuntimeEventEnvelope> Events)
                    participantResult;
                if (reconciledParticipant is null)
                {
                    var participantContext = await CreateInvocationRequestAsync(
                        runId, sessionId, request, options, selected, roundIndex,
                        projectionStrategyOverride, participantInvocationId, ct).ConfigureAwait(false);
                    if (resumedParticipant is not null)
                    {
                        participantContext = await UsePersistedInvocationSnapshotAsync(
                            participantContext,
                            resumedParticipant,
                            ct).ConfigureAwait(false);
                        pendingResume = null;
                    }

                    participantRequest = participantContext.Request;
                    yield return await CreateInvocationStartedEnvelopeAsync(
                        participantRequest, runtimeInstanceId, runSequence++, ct).ConfigureAwait(false);
                    yield return await CreateStatusEnvelopeAsync(
                        runtimeInstanceId,
                        runId,
                        runSequence++,
                        RunStatus.Running,
                        currentStep: $"round:{roundIndex}:participant:{selected.ParticipantId}",
                        completedUnits: Math.Clamp(roundIndex - runFirstRoundIndex, 0, maxRounds),
                        totalUnits: maxRounds,
                        ct).ConfigureAwait(false);
                    if (participantContext.Projection.IsAdjusted)
                    {
                        yield return await CreateProjectionAdjustedEnvelopeAsync(
                            runId, runtimeInstanceId, participantRequest.InvocationId,
                            participantContext.Projection, runSequence++, ct).ConfigureAwait(false);
                    }

                    participantResult = await ExecuteInvocationAsync(participantRequest).ConfigureAwait(false);
                }
                else
                {
                    pendingResume = null;
                    participantResult = (true, reconciledParticipant.Text, null, []);
                }

                foreach (var evt in participantResult.Events)
                {
                    yield return evt;
                }

                if (participantResult.Succeeded)
                {
                    var participantMessageId = reconciledParticipant?.Record.MessageId
                        ?? CreateStableId("meeting-message-v1", participantInvocationId);
                    if (reconciledParticipant is null)
                    {
                        await PersistAssistantMessageAsync(
                            sessionId, participantRequest!, participantResult.Text!, participantMessageId, ct)
                            .ConfigureAwait(false);
                    }

                    if (reconciledParticipant is null)
                    {
                        successfulTexts.Add(participantResult.Text!);
                    }

                    var shouldConcludeMeeting = isLastRound
                        || HasMeetingTimedOut(meetingDeadline)
                        || MatchesTerminationCondition(
                            participantResult.Text,
                            effectivePolicy.TerminationConditions);
                    MeetingInvocationScheduleInput? nextRoundSchedule = null;
                    if (!shouldConcludeMeeting)
                    {
                        (currentActiveParticipants, currentSelectionVersion) = await ReloadActiveParticipantsAsync(
                            sessionId, options, ct).ConfigureAwait(false);
                        if (currentActiveParticipants.Count == 0)
                        {
                            throw new InvalidOperationException(
                                "No active participants remain in the durable meeting snapshot.");
                        }

                        nextRoundSchedule = CreateDecisionRoleSchedule(
                            sessionId,
                            runId,
                            roundIndex + 1,
                            currentSelectionVersion,
                            options,
                            isHostDriven);
                    }

                    var summaryStep = await CompleteRoleStepAsync(
                        participantInvocationId,
                        participantMessageId,
                        decisionJson: null,
                        roundIndex,
                        currentSelectionVersion,
                        nextRoundSchedule).ConfigureAwait(false);
                    foreach (var summaryEvent in summaryStep.Events)
                    {
                        yield return summaryEvent;
                    }

                    if (summaryStep.FatalError is not null)
                    {
                        throw new InvalidOperationException(
                            $"Summarizer invocation failed in round {roundIndex}: "
                            + summaryStep.FatalError.Message);
                    }

                    if (shouldConcludeMeeting)
                    {
                        completedRoundIndex = roundIndex;
                        break;
                    }
                }
                else
                {
                    var participantError = participantResult.Error ?? new RuntimeError(
                        "invocation_failed", "orchestration", "Participant invocation failed.",
                        IsRetryable: false, ProviderDetails: null, Guid.NewGuid().ToString("N"));

                    if (effectivePolicy.ParticipantFailure == MeetingParticipantFailurePolicy.Skip)
                    {
                        var shouldConcludeMeeting = isLastRound
                            || HasMeetingTimedOut(meetingDeadline);
                        if (!shouldConcludeMeeting)
                        {
                            (currentActiveParticipants, currentSelectionVersion) = await ReloadActiveParticipantsAsync(
                                sessionId, options, ct).ConfigureAwait(false);
                            if (currentActiveParticipants.Count == 0)
                            {
                                throw new InvalidOperationException(
                                    "No active participants remain in the durable meeting snapshot.");
                            }

                            var nextRoundSelectorSchedule = CreateDecisionRoleSchedule(
                                sessionId,
                                runId,
                                roundIndex + 1,
                                currentSelectionVersion,
                                options,
                                isHostDriven);

                            await _meetingRepo.CompleteInvocationAndStartNextRoundAsync(
                                participantInvocationId, "Skipped", null,
                                participantError.Code, participantError.Message,
                                roundIndex + 1, nextRoundSelectorSchedule, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await _meetingRepo.CompleteInvocationAsync(
                                participantInvocationId, "Skipped", null,
                                null, participantError.Code, participantError.Message, null, ct).ConfigureAwait(false);
                        }

                        yield return await CreateEnvelopeAsync(
                            runtimeInstanceId, runId, runSequence++, MessageTypes.InvocationFailed,
                            new InvocationFailedEvent(participantInvocationId, participantError),
                            RuntimeJsonContext.Default.InvocationFailedEvent, ct).ConfigureAwait(false);

                        if (shouldConcludeMeeting)
                        {
                            completedRoundIndex = roundIndex;
                            break;
                        }

                        continue;
                    }

                    if (effectivePolicy.ParticipantFailure == MeetingParticipantFailurePolicy.Pause)
                    {
                        await _meetingRepo.PauseInvocationAsync(
                            participantInvocationId,
                            participantError.Code,
                            participantError.Message,
                            CancellationToken.None).ConfigureAwait(false);
                        yield return await CreateEnvelopeAsync(
                            runtimeInstanceId, runId, runSequence++, MessageTypes.InvocationFailed,
                            new InvocationFailedEvent(participantInvocationId, participantError),
                            RuntimeJsonContext.Default.InvocationFailedEvent, ct).ConfigureAwait(false);
                        yield break;
                    }

                    await _meetingRepo.CompleteInvocationAsync(
                        participantInvocationId, "Failed", null, null,
                        participantError.Code, participantError.Message, null, ct).ConfigureAwait(false);

                    await _meetingRepo.CompleteRoundAndMeetingAsync(
                        sessionId, roundIndex, "Failed", CancellationToken.None).ConfigureAwait(false);

                    await _sessionRepo.TransitionRunToTerminalAsync(
                        runId, RunStatus.Running, RunStatus.Failed, participantError.Message,
                        CancellationToken.None).ConfigureAwait(false);

                    yield return await CreateEnvelopeAsync(
                        runtimeInstanceId, runId, runSequence++, MessageTypes.InvocationFailed,
                        new InvocationFailedEvent(participantInvocationId, participantError),
                        RuntimeJsonContext.Default.InvocationFailedEvent, ct).ConfigureAwait(false);

                    switch (effectivePolicy.ParticipantFailure)
                    {
                        case MeetingParticipantFailurePolicy.Fail:
                            throw new InvalidOperationException(
                                $"Participant '{selected.ParticipantId}' invocation failed: {participantError.Message}");

                        case MeetingParticipantFailurePolicy.Retry:
                            throw new InvalidOperationException(
                                $"Retry exhausted for participant '{selected.ParticipantId}': {participantError.Message}");

                        default:
                            throw new InvalidOperationException(
                                $"Unknown participant failure policy '{effectivePolicy.ParticipantFailure}'.");
                    }
                }
            }
            else
            {
                var resumedParticipant = pendingResume is not null
                    && pendingResume.RoundIndex == roundIndex
                    ? pendingResume
                    : null;
                if (resumedParticipant is not null
                    && !string.Equals(resumedParticipant.Role, "participant", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Invocation '{resumedParticipant.InvocationId}' has role '{resumedParticipant.Role}' "
                        + "and cannot resume a RoundRobin participant step.");
                }

                var participant = resumedParticipant is null
                    ? currentActiveParticipants[(roundIndex - 1) % currentActiveParticipants.Count]
                    : ResolveRecoveredParticipant(snapshot, options, resumedParticipant);
                var participantInvocationId = resumedParticipant?.InvocationId
                    ?? CreateRoleInvocationId(
                        sessionId,
                        runId,
                        roundIndex,
                        "participant",
                        participant.ParticipantId,
                        currentSelectionVersion);

                if (resumedParticipant is null)
                {
                    await _meetingRepo.TransitionInvocationStatusAsync(
                        participantInvocationId, "Scheduled", "Running", ct).ConfigureAwait(false);
                }

                var reconciledParticipant = resumedParticipant is null
                    ? null
                    : GetReconciledCanonicalText(resumedParticipant, expectsSummary: false);
                AgentInvocationRequest? participantRequest = null;
                (bool Succeeded, string? Text, RuntimeError? Error, List<RuntimeEventEnvelope> Events)
                    participantResult;
                if (reconciledParticipant is null)
                {
                    var participantContext = await CreateInvocationRequestAsync(
                        runId, sessionId, request, options, participant, roundIndex,
                        projectionStrategyOverride, participantInvocationId, ct).ConfigureAwait(false);
                    if (resumedParticipant is not null)
                    {
                        participantContext = await UsePersistedInvocationSnapshotAsync(
                            participantContext,
                            resumedParticipant,
                            ct).ConfigureAwait(false);
                        pendingResume = null;
                    }

                    participantRequest = participantContext.Request;
                    yield return await CreateInvocationStartedEnvelopeAsync(
                        participantRequest, runtimeInstanceId, runSequence++, ct).ConfigureAwait(false);
                    yield return await CreateStatusEnvelopeAsync(
                        runtimeInstanceId,
                        runId,
                        runSequence++,
                        RunStatus.Running,
                        currentStep: $"round:{roundIndex}:participant:{participant.ParticipantId}",
                        completedUnits: Math.Clamp(roundIndex - runFirstRoundIndex, 0, maxRounds),
                        totalUnits: maxRounds,
                        ct).ConfigureAwait(false);
                    if (participantContext.Projection.IsAdjusted)
                    {
                        yield return await CreateProjectionAdjustedEnvelopeAsync(
                            runId, runtimeInstanceId, participantRequest.InvocationId,
                            participantContext.Projection, runSequence++, ct).ConfigureAwait(false);
                    }

                    participantResult = await ExecuteInvocationAsync(participantRequest).ConfigureAwait(false);
                }
                else
                {
                    pendingResume = null;
                    participantResult = (true, reconciledParticipant.Text, null, []);
                }

                foreach (var evt in participantResult.Events)
                {
                    yield return evt;
                }

                if (participantResult.Succeeded)
                {
                    var participantMessageId = reconciledParticipant?.Record.MessageId
                        ?? CreateStableId("meeting-message-v1", participantInvocationId);
                    if (reconciledParticipant is null)
                    {
                        await PersistAssistantMessageAsync(
                            sessionId, participantRequest!, participantResult.Text!, participantMessageId, ct)
                            .ConfigureAwait(false);
                    }

                    if (reconciledParticipant is null)
                    {
                        successfulTexts.Add(participantResult.Text!);
                    }

                    var shouldConcludeMeeting = isLastRound
                        || HasMeetingTimedOut(meetingDeadline)
                        || MatchesTerminationCondition(
                            participantResult.Text,
                            effectivePolicy.TerminationConditions);
                    MeetingInvocationScheduleInput? nextRoundSchedule = null;
                    if (!shouldConcludeMeeting)
                    {
                        (currentActiveParticipants, currentSelectionVersion) = await ReloadActiveParticipantsAsync(
                            sessionId, options, ct).ConfigureAwait(false);
                        if (currentActiveParticipants.Count == 0)
                        {
                            throw new InvalidOperationException(
                                "No active participants remain in the durable meeting snapshot.");
                        }

                        var nextRoundParticipant = currentActiveParticipants[roundIndex % currentActiveParticipants.Count];
                        nextRoundSchedule = CreateParticipantRoleSchedule(
                            sessionId, runId, roundIndex + 1, currentSelectionVersion, nextRoundParticipant);
                    }

                    var summaryStep = await CompleteRoleStepAsync(
                        participantInvocationId,
                        participantMessageId,
                        decisionJson: null,
                        roundIndex,
                        currentSelectionVersion,
                        nextRoundSchedule).ConfigureAwait(false);
                    foreach (var summaryEvent in summaryStep.Events)
                    {
                        yield return summaryEvent;
                    }

                    if (summaryStep.FatalError is not null)
                    {
                        throw new InvalidOperationException(
                            $"Summarizer invocation failed in round {roundIndex}: "
                            + summaryStep.FatalError.Message);
                    }

                    if (shouldConcludeMeeting)
                    {
                        completedRoundIndex = roundIndex;
                        break;
                    }
                }
                else
                {
                    var participantError = participantResult.Error ?? new RuntimeError(
                        "invocation_failed", "orchestration", "Participant invocation failed.",
                        IsRetryable: false, ProviderDetails: null, Guid.NewGuid().ToString("N"));

                    if (effectivePolicy.ParticipantFailure == MeetingParticipantFailurePolicy.Skip)
                    {
                        var shouldConcludeMeeting = isLastRound
                            || HasMeetingTimedOut(meetingDeadline);
                        if (!shouldConcludeMeeting)
                        {
                            (currentActiveParticipants, currentSelectionVersion) = await ReloadActiveParticipantsAsync(
                                sessionId, options, ct).ConfigureAwait(false);
                            if (currentActiveParticipants.Count == 0)
                            {
                                throw new InvalidOperationException(
                                    "No active participants remain in the durable meeting snapshot.");
                            }

                            var nextRoundParticipant = currentActiveParticipants[roundIndex % currentActiveParticipants.Count];
                            var nextRoundSchedule = CreateParticipantRoleSchedule(
                                sessionId, runId, roundIndex + 1, currentSelectionVersion, nextRoundParticipant);

                            await _meetingRepo.CompleteInvocationAndStartNextRoundAsync(
                                participantInvocationId, "Skipped", null,
                                participantError.Code, participantError.Message,
                                roundIndex + 1, nextRoundSchedule, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await _meetingRepo.CompleteInvocationAsync(
                                participantInvocationId, "Skipped", null,
                                null, participantError.Code, participantError.Message, null, ct).ConfigureAwait(false);
                        }

                        yield return await CreateEnvelopeAsync(
                            runtimeInstanceId, runId, runSequence++, MessageTypes.InvocationFailed,
                            new InvocationFailedEvent(participantInvocationId, participantError),
                            RuntimeJsonContext.Default.InvocationFailedEvent, ct).ConfigureAwait(false);

                        if (shouldConcludeMeeting)
                        {
                            completedRoundIndex = roundIndex;
                            break;
                        }

                        continue;
                    }

                    if (effectivePolicy.ParticipantFailure == MeetingParticipantFailurePolicy.Pause)
                    {
                        await _meetingRepo.PauseInvocationAsync(
                            participantInvocationId,
                            participantError.Code,
                            participantError.Message,
                            CancellationToken.None).ConfigureAwait(false);
                        yield return await CreateEnvelopeAsync(
                            runtimeInstanceId, runId, runSequence++, MessageTypes.InvocationFailed,
                            new InvocationFailedEvent(participantInvocationId, participantError),
                            RuntimeJsonContext.Default.InvocationFailedEvent, ct).ConfigureAwait(false);
                        yield break;
                    }

                    await _meetingRepo.CompleteInvocationAsync(
                        participantInvocationId, "Failed", null, null,
                        participantError.Code, participantError.Message, null, ct).ConfigureAwait(false);

                    await _meetingRepo.CompleteRoundAndMeetingAsync(
                        sessionId, roundIndex, "Failed", CancellationToken.None).ConfigureAwait(false);

                    await _sessionRepo.TransitionRunToTerminalAsync(
                        runId, RunStatus.Running, RunStatus.Failed, participantError.Message,
                        CancellationToken.None).ConfigureAwait(false);

                    yield return await CreateEnvelopeAsync(
                        runtimeInstanceId, runId, runSequence++, MessageTypes.InvocationFailed,
                        new InvocationFailedEvent(participantInvocationId, participantError),
                        RuntimeJsonContext.Default.InvocationFailedEvent, ct).ConfigureAwait(false);

                    switch (effectivePolicy.ParticipantFailure)
                    {
                        case MeetingParticipantFailurePolicy.Fail:
                            throw new InvalidOperationException(
                                $"Participant '{participant.ParticipantId}' invocation failed: {participantError.Message}");

                        case MeetingParticipantFailurePolicy.Retry:
                            throw new InvalidOperationException(
                                $"Retry exhausted for participant '{participant.ParticipantId}': {participantError.Message}");

                        default:
                            throw new InvalidOperationException(
                                $"Unknown participant failure policy '{effectivePolicy.ParticipantFailure}'.");
                    }
                }
            }

            // Mid-run pause requests (e.g. during a participant turn) take effect after the round finishes.
            if (await TryPauseAtSafePointAsync(
                    sessionId,
                    runId,
                    runtimeInstanceId,
                    () => runSequence++,
                    ct).ConfigureAwait(false) is { } roundEndPauseEnvelope)
            {
                yield return roundEndPauseEnvelope;
                yield break;
            }
        }
        } // !isEndConfirmationResume

        // Catch pause requests that arrived on the final round after the loop condition ends.
        if (await TryPauseAtSafePointAsync(
                sessionId,
                runId,
                runtimeInstanceId,
                () => runSequence++,
                ct).ConfigureAwait(false) is { } postLoopPauseEnvelope)
        {
            yield return postLoopPauseEnvelope;
            yield break;
        }

        var endStateSnapshot = await _meetingRepo.GetMeetingSnapshotAsync(sessionId, ct)
            .ConfigureAwait(false);
        if (endStateSnapshot?.Session.EndConfirmed == true)
        {
            goto EndConfirmationAccepted;
        }

        if (effectivePolicy.RequireEndConfirmation || isEndConfirmationResume)
        {
            if (_hitlHandler is null && _resumedHitlResponse is null)
            {
                await FailMeetingAndRunAsync(
                    sessionId,
                    runId,
                    "End confirmation is required but no hitlHandler was provided.",
                    CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "End confirmation is required but no hitlHandler was provided.");
            }

            var endConfirmationAttempt = 0;
            var pendingEndHitlResponse = isEndConfirmationResume ? _resumedHitlResponse : null;
            while (true)
            {
                await DrainPendingInputsAtSafePointAsync(sessionId, runId, ct).ConfigureAwait(false);
                if (await TryPauseAtSafePointAsync(
                        sessionId,
                        runId,
                        runtimeInstanceId,
                        () => runSequence++,
                        ct).ConfigureAwait(false) is { } endPauseEnvelope)
                {
                    yield return endPauseEnvelope;
                    yield break;
                }

                endStateSnapshot = await _meetingRepo.GetMeetingSnapshotAsync(sessionId, ct)
                    .ConfigureAwait(false);
                if (endStateSnapshot?.Session.EndConfirmed == true)
                {
                    await ResumeFromPersistedHitlAsync(sessionId, runId, ct).ConfigureAwait(false);
                    goto EndConfirmationAccepted;
                }

                endConfirmationAttempt++;
                var endApprovalRequestId = CreateStableId(
                    "meeting-hitl-v1",
                    sessionId,
                    runId,
                    completedRoundIndex.ToString(CultureInfo.InvariantCulture),
                    EndConfirmationApprovalSuffix,
                    endConfirmationAttempt.ToString(CultureInfo.InvariantCulture));
                // Stable id for the first attempt must match crash-resume ids that omit the attempt suffix.
                if (endConfirmationAttempt == 1)
                {
                    endApprovalRequestId = CreateStableId(
                        "meeting-hitl-v1",
                        sessionId,
                        runId,
                        completedRoundIndex.ToString(CultureInfo.InvariantCulture),
                        EndConfirmationApprovalSuffix);
                }

                var endHostInvocationId = CreateStableId(
                    "meeting-host-end-v1",
                    sessionId,
                    runId,
                    completedRoundIndex.ToString(CultureInfo.InvariantCulture));
                var endHitlNow = DateTimeOffset.UtcNow;
                DateTimeOffset? endHitlExpiresAt = effectivePolicy.HitlTimeoutSeconds is > 0
                    ? endHitlNow.AddSeconds(effectivePolicy.HitlTimeoutSeconds.Value)
                    : null;

                ContentBlock[] endContext =
                [
                    new TextContentBlock(
                        "Meeting discussion and summary are ready. Approve to end the meeting, "
                        + "Supplement to add notes and re-confirm, or Cancel to abort.")
                ];

                var endHitlRequest = new MeetingHitlRequest(
                    endApprovalRequestId,
                    sessionId,
                    runId,
                    RoundIndex: completedRoundIndex,
                    HostInvocationId: endHostInvocationId,
                    Prompt: "meeting.hitl.question.end-confirmation",
                    Context: endContext,
                    CreatedAt: endHitlNow,
                    ExpiresAt: endHitlExpiresAt);

                yield return await CreateStatusEnvelopeAsync(
                    runtimeInstanceId,
                    runId,
                    runSequence++,
                    RunStatus.WaitingForApproval,
                    currentStep: $"round:{completedRoundIndex}:end-confirmation",
                    completedUnits: maxRounds,
                    totalUnits: maxRounds,
                    ct).ConfigureAwait(false);

                MeetingHitlResponse endHitlResponse;
                if (pendingEndHitlResponse is not null)
                {
                    endHitlResponse = pendingEndHitlResponse;
                    pendingEndHitlResponse = null;
                }
                else
                {
                    try
                    {
                        endHitlResponse = await _hitlHandler!(endHitlRequest, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                }

                if (endHitlResponse is null
                    || !string.Equals(
                        endHitlResponse.ApprovalRequestId,
                        endApprovalRequestId,
                        StringComparison.Ordinal))
                {
                    await FailMeetingAndRunAsync(
                        sessionId,
                        runId,
                        "End-confirmation HITL handler returned a null or mismatched response.",
                        CancellationToken.None).ConfigureAwait(false);
                    throw new InvalidOperationException(
                        "End-confirmation HITL handler returned a null or mismatched response.");
                }

                switch (endHitlResponse.Action)
                {
                    case MeetingHitlAction.Approve:
                        await ResumeFromPersistedHitlAsync(sessionId, runId, ct).ConfigureAwait(false);
                        goto EndConfirmationAccepted;

                    case MeetingHitlAction.Supplement:
                        if (string.IsNullOrWhiteSpace(endHitlResponse.Supplement))
                        {
                            await FailMeetingAndRunAsync(
                                sessionId,
                                runId,
                                "End-confirmation supplement action returned with a blank supplement.",
                                CancellationToken.None).ConfigureAwait(false);
                            throw new InvalidOperationException(
                                "End-confirmation supplement action returned with a blank supplement.");
                        }

                        await PersistHitlSupplementAsync(
                            sessionId,
                            runId,
                            endHitlResponse.ApprovalRequestId,
                            endHitlResponse.Supplement,
                            ct).ConfigureAwait(false);
                        await ResumeFromPersistedHitlAsync(sessionId, runId, ct).ConfigureAwait(false);

                        // Continue discussion, then re-ask for end confirmation.
                        // HostDriven/SelectorDriven keep the decision role; RoundRobin stays participant order.
                        var continuationCount = Math.Max(
                            1,
                            Math.Min(
                                4,
                                effectiveSelectorPolicy.MaxRounds ?? activeParticipants.Count));
                        (currentActiveParticipants, currentSelectionVersion) =
                            await ReloadActiveParticipantsAsync(sessionId, options, ct)
                                .ConfigureAwait(false);
                        if (currentActiveParticipants.Count == 0)
                        {
                            throw new InvalidOperationException(
                                "No active participants remain for continued discussion.");
                        }

                        try
                        {
                            await _meetingRepo.CompleteRoundOnlyAsync(
                                sessionId,
                                completedRoundIndex,
                                ct).ConfigureAwait(false);
                        }
                        catch (InvalidOperationException)
                        {
                            // Round may already be completed when FinalOnly summarizer finished the step.
                        }

                        var continuationConcluded = false;
                        for (var offset = 1; offset <= continuationCount && !continuationConcluded; offset++)
                        {
                            var continuationRound = completedRoundIndex + offset;
                            var isContinuationLast = offset == continuationCount;
                            await DrainPendingInputsAtSafePointAsync(sessionId, runId, ct)
                                .ConfigureAwait(false);
                            if (await TryPauseAtSafePointAsync(
                                    sessionId,
                                    runId,
                                    runtimeInstanceId,
                                    () => runSequence++,
                                    ct).ConfigureAwait(false) is { } continuationPause)
                            {
                                yield return continuationPause;
                                yield break;
                            }

                            MeetingParticipant continuationParticipant;
                            MeetingInvocationScheduleInput continuationSchedule;
                            string? continuationDecisionJson = null;
                            string? hostContinuationMessageId = null;

                            if (isHostDriven || isSelectorDriven)
                            {
                                var decisionSchedule = CreateDecisionRoleSchedule(
                                    sessionId,
                                    runId,
                                    continuationRound,
                                    currentSelectionVersion,
                                    options,
                                    isHostDriven);
                                await _meetingRepo.CreateRoundWithFirstInvocationAsync(
                                    sessionId,
                                    runId,
                                    continuationRound - 1,
                                    continuationRound,
                                    decisionSchedule,
                                    ct).ConfigureAwait(false);
                                await _meetingRepo.TransitionInvocationStatusAsync(
                                    decisionSchedule.InvocationId,
                                    "Scheduled",
                                    "Running",
                                    ct).ConfigureAwait(false);

                                var decisionContext = isHostDriven
                                    ? await CreateHostInvocationRequestAsync(
                                        runId,
                                        sessionId,
                                        request,
                                        options,
                                        currentActiveParticipants,
                                        continuationRound,
                                        projectionStrategyOverride,
                                        decisionSchedule.InvocationId,
                                        ct).ConfigureAwait(false)
                                    : await CreateSelectorInvocationRequestAsync(
                                        runId,
                                        sessionId,
                                        request,
                                        options,
                                        currentActiveParticipants,
                                        continuationRound,
                                        projectionStrategyOverride,
                                        decisionSchedule.InvocationId,
                                        ct).ConfigureAwait(false);

                                yield return await CreateInvocationStartedEnvelopeAsync(
                                    decisionContext.Request,
                                    runtimeInstanceId,
                                    runSequence++,
                                    ct).ConfigureAwait(false);

                                var decisionResult = await ExecuteInvocationAsync(
                                    decisionContext.Request).ConfigureAwait(false);
                                foreach (var evt in decisionResult.Events)
                                {
                                    yield return evt;
                                }

                                (currentActiveParticipants, currentSelectionVersion) =
                                    await ReloadActiveParticipantsAsync(sessionId, options, ct)
                                        .ConfigureAwait(false);
                                if (currentActiveParticipants.Count == 0)
                                {
                                    throw new InvalidOperationException(
                                        "No active participants remain during continued discussion.");
                                }

                                MeetingParticipant? selectedContinuation = null;
                                var concludeContinuation = false;
                                if (isHostDriven && decisionResult.Succeeded)
                                {
                                    var endSnap = await _meetingRepo
                                        .GetMeetingSnapshotAsync(sessionId, ct)
                                        .ConfigureAwait(false);
                                    if (endSnap?.Session.EndConfirmed == true)
                                    {
                                        concludeContinuation = true;
                                    }
                                    else
                                    {
                                        _ = TryResolveHostDecision(
                                            decisionResult.Text,
                                            currentActiveParticipants,
                                            out selectedContinuation,
                                            out concludeContinuation);
                                    }

                                    if (!string.IsNullOrWhiteSpace(decisionResult.Text))
                                    {
                                        hostContinuationMessageId = CreateStableId(
                                            "meeting-host-message-v1",
                                            decisionSchedule.InvocationId);
                                        await PersistAssistantMessageAsync(
                                            sessionId,
                                            decisionContext.Request,
                                            decisionResult.Text!,
                                            hostContinuationMessageId,
                                            ct).ConfigureAwait(false);
                                    }
                                }
                                else if (decisionResult.Succeeded
                                    && !string.IsNullOrWhiteSpace(decisionResult.Text))
                                {
                                    selectedContinuation = TryResolveSelectorParticipant(
                                        decisionResult.Text!,
                                        currentActiveParticipants);
                                }

                                if (concludeContinuation)
                                {
                                    var conclusionStep = await CompleteRoleStepAsync(
                                        decisionSchedule.InvocationId,
                                        hostContinuationMessageId,
                                        CreateHostDecisionJson(null, conclude: true),
                                        continuationRound,
                                        currentSelectionVersion,
                                        nextRoundSchedule: null,
                                        conclusionWithoutParticipant: true).ConfigureAwait(false);
                                    foreach (var conclusionEvent in conclusionStep.Events)
                                    {
                                        yield return conclusionEvent;
                                    }

                                    if (conclusionStep.FatalError is not null)
                                    {
                                        throw new InvalidOperationException(
                                            "Summarizer failed after continued Host conclude: "
                                            + conclusionStep.FatalError.Message);
                                    }

                                    completedRoundIndex = continuationRound;
                                    continuationConcluded = true;
                                    break;
                                }

                                selectedContinuation ??= SelectDeterministicFallback(
                                    currentActiveParticipants,
                                    continuationRound);
                                if (selectedContinuation is null)
                                {
                                    throw new InvalidOperationException(
                                        $"Continuation round {continuationRound} could not select a participant.");
                                }

                                continuationParticipant = selectedContinuation;
                                continuationSchedule = CreateParticipantRoleSchedule(
                                    sessionId,
                                    runId,
                                    continuationRound,
                                    currentSelectionVersion,
                                    continuationParticipant);
                                continuationDecisionJson = isHostDriven
                                    ? CreateHostDecisionJson(
                                        continuationParticipant.ParticipantId,
                                        conclude: false)
                                    : CreateSelectorDecisionJson(
                                        continuationParticipant.ParticipantId,
                                        "selector");

                                await _meetingRepo.CompleteInvocationAsync(
                                    decisionSchedule.InvocationId,
                                    "Completed",
                                    hostContinuationMessageId,
                                    continuationDecisionJson,
                                    null,
                                    null,
                                    continuationSchedule,
                                    ct).ConfigureAwait(false);
                                yield return await CreateInvocationCompletedEnvelopeAsync(
                                    runId,
                                    runtimeInstanceId,
                                    decisionSchedule.InvocationId,
                                    runSequence++,
                                    ct).ConfigureAwait(false);
                            }
                            else
                            {
                                continuationParticipant = currentActiveParticipants[
                                    (continuationRound - 1) % currentActiveParticipants.Count];
                                continuationSchedule = CreateParticipantRoleSchedule(
                                    sessionId,
                                    runId,
                                    continuationRound,
                                    currentSelectionVersion,
                                    continuationParticipant);
                                await _meetingRepo.CreateRoundWithFirstInvocationAsync(
                                    sessionId,
                                    runId,
                                    continuationRound - 1,
                                    continuationRound,
                                    continuationSchedule,
                                    ct).ConfigureAwait(false);
                            }

                            await _meetingRepo.TransitionInvocationStatusAsync(
                                continuationSchedule.InvocationId,
                                "Scheduled",
                                "Running",
                                ct).ConfigureAwait(false);

                            var continuationRequest = await CreateInvocationRequestAsync(
                                runId,
                                sessionId,
                                request,
                                options,
                                continuationParticipant,
                                continuationRound,
                                projectionStrategyOverride,
                                continuationSchedule.InvocationId,
                                ct).ConfigureAwait(false);
                            yield return await CreateInvocationStartedEnvelopeAsync(
                                continuationRequest.Request,
                                runtimeInstanceId,
                                runSequence++,
                                ct).ConfigureAwait(false);

                            var continuationResult = await ExecuteInvocationAsync(
                                continuationRequest.Request).ConfigureAwait(false);
                            foreach (var evt in continuationResult.Events)
                            {
                                yield return evt;
                            }

                            if (!continuationResult.Succeeded
                                || string.IsNullOrWhiteSpace(continuationResult.Text))
                            {
                                var continuationError = continuationResult.Error
                                    ?? new RuntimeError(
                                        "invocation_failed",
                                        "orchestration",
                                        "Continuation participant invocation failed.",
                                        IsRetryable: false,
                                        ProviderDetails: null,
                                        Guid.NewGuid().ToString("N"));
                                await _meetingRepo.CompleteInvocationAsync(
                                    continuationSchedule.InvocationId,
                                    "Failed",
                                    null,
                                    null,
                                    continuationError.Code,
                                    continuationError.Message,
                                    null,
                                    ct).ConfigureAwait(false);
                                yield return await CreateEnvelopeAsync(
                                    runtimeInstanceId,
                                    runId,
                                    runSequence++,
                                    MessageTypes.InvocationFailed,
                                    new InvocationFailedEvent(
                                        continuationSchedule.InvocationId,
                                        continuationError),
                                    RuntimeJsonContext.Default.InvocationFailedEvent,
                                    ct).ConfigureAwait(false);
                                break;
                            }

                            var continuationMessageId = CreateStableId(
                                "meeting-message-v1",
                                continuationSchedule.InvocationId);
                            await PersistAssistantMessageAsync(
                                sessionId,
                                continuationRequest.Request,
                                continuationResult.Text!,
                                continuationMessageId,
                                ct).ConfigureAwait(false);
                            successfulTexts.Add(continuationResult.Text!);

                            // Do not pre-schedule the next continuation round here: the loop body
                            // always creates the next round explicitly (avoids double-create for Host/Selector).
                            MeetingInvocationScheduleInput? nextContinuation = null;
                            if (!isContinuationLast && !isHostDriven && !isSelectorDriven)
                            {
                                var nextParticipant = currentActiveParticipants[
                                    continuationRound % currentActiveParticipants.Count];
                                nextContinuation = CreateParticipantRoleSchedule(
                                    sessionId,
                                    runId,
                                    continuationRound + 1,
                                    currentSelectionVersion,
                                    nextParticipant);
                            }

                            var continuationSummary = await CompleteRoleStepAsync(
                                continuationSchedule.InvocationId,
                                continuationMessageId,
                                decisionJson: null,
                                continuationRound,
                                currentSelectionVersion,
                                nextContinuation).ConfigureAwait(false);
                            foreach (var summaryEvent in continuationSummary.Events)
                            {
                                yield return summaryEvent;
                            }

                            if (continuationSummary.FatalError is not null)
                            {
                                throw new InvalidOperationException(
                                    $"Summarizer failed during continued discussion round {continuationRound}: "
                                    + continuationSummary.FatalError.Message);
                            }

                            completedRoundIndex = continuationRound;

                            // Host confirm_end during continuation also ends the segment.
                            var postSnap = await _meetingRepo
                                .GetMeetingSnapshotAsync(sessionId, ct)
                                .ConfigureAwait(false);
                            if (postSnap?.Session.EndConfirmed == true)
                            {
                                continuationConcluded = true;
                            }
                        }

                        continue;

                    case MeetingHitlAction.Reject:
                    case MeetingHitlAction.Cancel:
                        var endCancellationReason = endHitlResponse.Reason
                            ?? "Meeting end confirmation was rejected or cancelled.";
                        await CancelMeetingAndRunAsync(
                            sessionId,
                            runId,
                            endCancellationReason,
                            CancellationToken.None,
                            transitionRun: false).ConfigureAwait(false);
                        var endCancelledEnvelope = await CommitTerminalEnvelopeAsync(
                            runtimeInstanceId,
                            runId,
                            runSequence++,
                            RunStatus.Cancelled,
                            endCancellationReason,
                            error: null,
                            value: new RunCancelledEvent(runId, sessionId),
                            typeInfo: RuntimeJsonContext.Default.RunCancelledEvent,
                            ct: ct).ConfigureAwait(false);
                        if (endCancelledEnvelope is not null)
                        {
                            yield return endCancelledEnvelope;
                        }

                        yield break;

                    default:
                        await FailMeetingAndRunAsync(
                            sessionId,
                            runId,
                            $"Unknown end-confirmation HITL action '{endHitlResponse.Action}'.",
                            CancellationToken.None).ConfigureAwait(false);
                        throw new InvalidOperationException(
                            $"Unknown end-confirmation HITL action '{endHitlResponse.Action}'.");
                }
            }
        }

        EndConfirmationAccepted:

        await _meetingRepo.CompleteRoundAndMeetingAsync(
            sessionId,
            completedRoundIndex,
            "Completed",
            CancellationToken.None).ConfigureAwait(false);

        var nonEmptyTexts = successfulTexts.Where(t => !string.IsNullOrEmpty(t)).ToList();
        var terminalText = nonEmptyTexts.Count > 0
            ? string.Join("\n", nonEmptyTexts)
            : null;

        await _sessionRepo.TransitionRunStatusAsync(
            runId,
            RunStatus.Running,
            RunStatus.Persisting,
            CancellationToken.None).ConfigureAwait(false);

        yield return await CreateStatusEnvelopeAsync(
            runtimeInstanceId,
            runId,
            runSequence++,
            RunStatus.Persisting,
            currentStep: "meeting:finalizing",
            completedUnits: maxRounds,
            totalUnits: maxRounds,
            ct).ConfigureAwait(false);

        var completedEnvelope = await CreateCompletedEnvelopeAsync(
            runId,
            sessionId,
            runtimeInstanceId,
            runSequence,
            terminalText,
            ct).ConfigureAwait(false);
        if (completedEnvelope is not null)
        {
            yield return completedEnvelope;
        }
    }

    private async Task<MeetingProjectionResult> BuildProjectedMessagesAsync(
        string sessionId,
        string invocationId,
        string agentId,
        string providerId,
        string modelId,
        RuntimeProviderMessage systemMessage,
        MeetingModeOptions options,
        int currentRound,
        MeetingInvocationRole targetRole,
        MeetingContextStrategy? strategyOverride,
        CancellationToken ct)
    {
        var history = await _store.ReadAllAsync(sessionId, ct).ConfigureAwait(false);
        var contextMap = await _meetingRepo.GetInvocationContextMapAsync(sessionId, ct).ConfigureAwait(false);

        var invocationToRound = new Dictionary<string, int>(StringComparer.Ordinal);
        var invocationToRole = new Dictionary<string, MeetingInvocationRole>(StringComparer.Ordinal);

        foreach (var kvp in contextMap)
        {
            invocationToRound[kvp.Key] = kvp.Value.RoundIndex;

            if (!string.IsNullOrEmpty(kvp.Value.Role))
            {
                var roleStr = kvp.Value.Role;
                if (string.Equals(roleStr, "participant", StringComparison.OrdinalIgnoreCase))
                {
                    invocationToRole[kvp.Key] = MeetingInvocationRole.Participant;
                }
                else if (string.Equals(roleStr, "selector", StringComparison.OrdinalIgnoreCase))
                {
                    invocationToRole[kvp.Key] = MeetingInvocationRole.Selector;
                }
                else if (string.Equals(roleStr, "host", StringComparison.OrdinalIgnoreCase))
                {
                    invocationToRole[kvp.Key] = MeetingInvocationRole.Host;
                }
                else if (string.Equals(roleStr, "summarizer", StringComparison.OrdinalIgnoreCase))
                {
                    invocationToRole[kvp.Key] = MeetingInvocationRole.Summarizer;
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Unknown role '{roleStr}' for invocation '{kvp.Key}'.");
                }
            }
        }

        var adapter = _providerFactory(providerId);
        if (adapter is null)
        {
            throw new InvalidOperationException(
                $"Provider adapter '{providerId}' could not be resolved.");
        }

        var contextPolicy = options.EffectiveContextPolicy;
        int tokenLimit;
        if (contextPolicy.MaxTokens is > 0)
        {
            tokenLimit = contextPolicy.MaxTokens.Value;
        }
        else
        {
            var model = adapter.Models.FirstOrDefault(
                m => string.Equals(m.Id, modelId, StringComparison.Ordinal));
            if (model?.ContextWindow is > 0)
            {
                tokenLimit = Math.Max(1, (int)(model.ContextWindow.Value * 0.9));
            }
            else
            {
                tokenLimit = int.MaxValue;
            }
        }

        Func<RuntimeProviderMessage[], CancellationToken, ValueTask<ProviderTokenEstimate>> estimator =
            (messages, estCt) =>
            {
                var allMessages = new RuntimeProviderMessage[messages.Length + 1];
                allMessages[0] = systemMessage;
                Array.Copy(messages, 0, allMessages, 1, messages.Length);
                var estRequest = new RuntimeProviderRequest(
                    invocationId, agentId, providerId, modelId, allMessages,
                    CancellationToken: estCt);
                return adapter.EstimateTokensAsync(estRequest, estCt);
            };

        var projection = await MeetingContextProjectionService.BuildAsync(
            new MeetingProjectionRequest(
                history,
                invocationToRound,
                currentRound,
                tokenLimit,
                Strategy: strategyOverride ?? contextPolicy.Strategy,
                TailMessageCount: contextPolicy.TailMessageCount,
                SlidingRoundCount: contextPolicy.SlidingRoundCount,
                InvocationToRole: invocationToRole,
                TargetRole: targetRole)
            {
                Estimator = estimator,
            },
            ct).ConfigureAwait(false);

        return projection;
    }

    private async Task<ProjectedInvocationRequest> CreateInvocationRequestAsync(
        string runId,
        string sessionId,
        NewSessionRunRequest request,
        MeetingModeOptions options,
        MeetingParticipant participant,
        int roundIndex,
        MeetingContextStrategy? projectionStrategyOverride,
        string invocationId,
        CancellationToken ct)
    {
        var agent = participant.Agent;
        var providerId = agent.ProviderId ?? request.Selection.DefaultSelection.ProviderId;
        var modelId = agent.ModelId ?? request.Selection.DefaultSelection.ModelId;
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var context = _agentContextComposer is null
            ? AgentContextComposer.WithoutMemory(agent.SystemPrompt)
            : await _agentContextComposer.ComposeAsync(agent.SystemPrompt, ct)
                .ConfigureAwait(false);
        var systemMessage = new RuntimeProviderMessage(
            RuntimeProviderRoles.System,
            [new TextContentBlock(context.Instructions)]);
        var projection = await BuildProjectedMessagesAsync(
            sessionId,
            invocationId,
            agent.AgentId,
            providerId,
            modelId,
            systemMessage,
            options,
            roundIndex,
            MeetingInvocationRole.Participant,
            projectionStrategyOverride,
            ct: ct).ConfigureAwait(false);
        var skillCatalogSnapshot = (await CaptureSkillCatalogSnapshotAsync(ct).ConfigureAwait(false))
            ?.FilterBySkillIds(agent.SkillIds);
        var snapshot = new InvocationSnapshot(
            invocationId,
            agent.AgentId,
            providerId,
            modelId,
            ComputeHash(agent.SystemPrompt),
            context.GlobalMemoryHash,
            context.ProjectMemoryHash,
            request.Selection.ToolCatalogVersion,
            ProjectionVersion,
            DateTimeOffset.UtcNow,
            ParentInvocationId: null,
            WorkStepId: null,
            WorkPlanVersion: null,
            Title: null,
            ParentStepId: null,
            Depth: 0,
            DisplayName: participant.DisplayName,
            Role: MeetingInvocationRole.Participant.ToString(),
            ParticipantId: participant.ParticipantId,
            RoundIndex: roundIndex,
            ContextProjection: null,
            SkillCatalogVersion: skillCatalogSnapshot?.Version,
            SkillNames: skillCatalogSnapshot?.Skills.Select(static skill => skill.Name).ToArray(),
            SkillContentHashes: skillCatalogSnapshot?.Skills.Select(static skill => skill.ContentHash).ToArray(),
            SkillSourceIds: skillCatalogSnapshot?.Skills.Select(static skill => skill.SourceId).ToArray(),
            SkillMaterializedPaths: skillCatalogSnapshot?.Skills.Select(static skill => skill.MaterializedPath).ToArray());
        return new ProjectedInvocationRequest(
            ApplyHostSkills(
                new AgentInvocationRequest(
                    invocationId,
                    runId,
                    sessionId,
                    agent.AgentId,
                    providerId,
                    modelId,
                    [
                        systemMessage,
                        .. projection.Messages,
                    ],
                    snapshot,
                    AvailableTools: ResolveAvailableTools(agent, providerId, "participant"),
                    HistoryMessageCount: 1 + projection.Messages.Length),
                agent.SkillIds,
                skillCatalogSnapshot,
                agent.SkillDiscoveryMode),
            projection);
    }

    private async Task<ProjectedInvocationRequest> CreateSummarizerInvocationRequestAsync(
        string runId,
        string sessionId,
        NewSessionRunRequest request,
        MeetingModeOptions options,
        int roundIndex,
        string invocationId,
        long summarizesThroughSeq,
        CancellationToken ct)
    {
        var agent = options.EffectivePolicy.Summarizer ?? BuiltInSummarizerAgent;
        var providerId = agent.ProviderId ?? request.Selection.DefaultSelection.ProviderId;
        var modelId = agent.ModelId ?? request.Selection.DefaultSelection.ModelId;
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var context = _agentContextComposer is null
            ? AgentContextComposer.WithoutMemory(agent.SystemPrompt)
            : await _agentContextComposer.ComposeAsync(agent.SystemPrompt, ct)
                .ConfigureAwait(false);
        var systemMessage = new RuntimeProviderMessage(
            RuntimeProviderRoles.System,
            [new TextContentBlock(context.Instructions)]);
        var projection = await BuildProjectedMessagesAsync(
            sessionId,
            invocationId,
            agent.AgentId,
            providerId,
            modelId,
            systemMessage,
            options,
            roundIndex,
            MeetingInvocationRole.Summarizer,
            MeetingContextStrategy.Full,
            ct).ConfigureAwait(false);
        var skillCatalogSnapshot = (await CaptureSkillCatalogSnapshotAsync(ct).ConfigureAwait(false))
            ?.FilterBySkillIds(agent.SkillIds);
        var snapshot = new InvocationSnapshot(
            invocationId,
            agent.AgentId,
            providerId,
            modelId,
            ComputeHash(agent.SystemPrompt),
            context.GlobalMemoryHash,
            context.ProjectMemoryHash,
            request.Selection.ToolCatalogVersion,
            SummaryProjectionVersion,
            DateTimeOffset.UtcNow,
            ParentInvocationId: null,
            WorkStepId: null,
            WorkPlanVersion: null,
            Title: null,
            ParentStepId: null,
            Depth: 0,
            DisplayName: agent.AgentId,
            Role: MeetingInvocationRole.Summarizer.ToString(),
            ParticipantId: null,
            RoundIndex: roundIndex,
            ContextProjection: null,
            SkillCatalogVersion: skillCatalogSnapshot?.Version,
            SkillNames: skillCatalogSnapshot?.Skills.Select(static skill => skill.Name).ToArray(),
            SkillContentHashes: skillCatalogSnapshot?.Skills.Select(static skill => skill.ContentHash).ToArray(),
            SkillSourceIds: skillCatalogSnapshot?.Skills.Select(static skill => skill.SourceId).ToArray(),
            SkillMaterializedPaths: skillCatalogSnapshot?.Skills.Select(static skill => skill.MaterializedPath).ToArray());
        var instruction = "Summarize the meeting's public discussion through canonical sequence "
            + summarizesThroughSeq.ToString(CultureInfo.InvariantCulture)
            + ". Return a concise factual summary only; do not add new decisions or tool calls.";

        return new ProjectedInvocationRequest(
            ApplyHostSkills(
                new AgentInvocationRequest(
                    invocationId,
                    runId,
                    sessionId,
                    agent.AgentId,
                    providerId,
                    modelId,
                    [
                        systemMessage,
                        .. projection.Messages,
                        new RuntimeProviderMessage(
                            RuntimeProviderRoles.User,
                            [new TextContentBlock(instruction)])
                    ],
                    snapshot,
                    AvailableTools: null,
                    HistoryMessageCount: 1 + projection.Messages.Length),
                agent.SkillIds,
                skillCatalogSnapshot,
                agent.SkillDiscoveryMode),
            projection);
    }

    private async Task PersistSummaryMessageAsync(
        string sessionId,
        AgentInvocationRequest request,
        string text,
        string messageId,
        int roundIndex,
        long summarizesThroughSeq,
        string policyHash,
        CancellationToken ct)
    {
        var sequence = await _store.GetLastSequenceAsync(sessionId, ct).ConfigureAwait(false) + 1;
        ContentBlock[] content = [new TextContentBlock(text)];
        var jsonElement = JsonSerializer.SerializeToElement(
            content,
            RuntimeJsonContext.Default.ContentBlockArray);
        var record = new ConversationRecordV1(
            messageId,
            sequence,
            request.InvocationId,
            request.Snapshot.AgentId,
            RuntimeProviderRoles.Assistant,
            jsonElement,
            DateTimeOffset.UtcNow,
            SummaryMetadata: CreateSummaryMetadata(
                roundIndex,
                summarizesThroughSeq,
                policyHash,
                request.Snapshot.AgentId,
                request.InvocationId));
        await _store.AppendMessageAsync(sessionId, Mode.ToString(), record, ct).ConfigureAwait(false);
    }

    private async Task<MeetingRoundRecord?> TryGetCachedSummaryAsync(
        string sessionId,
        int roundIndex,
        string policyHash,
        AgentInvocationRequest request,
        CancellationToken ct)
    {
        var cached = await _meetingRepo.GetRoundSummaryAsync(sessionId, roundIndex, ct)
            .ConfigureAwait(false);
        if (cached is not
            {
                SummaryMessageId: not null,
                SummarizesThroughSeq: not null,
                SummaryPolicyHash: not null,
                SummaryAgentId: not null,
                SummaryInvocationId: not null
            }
            || !string.Equals(cached.SummaryPolicyHash, policyHash, StringComparison.Ordinal)
            || !string.Equals(cached.SummaryAgentId, request.AgentId, StringComparison.Ordinal)
            || !string.Equals(
                cached.SummaryInvocationId,
                request.InvocationId,
                StringComparison.Ordinal))
        {
            return null;
        }

        var history = await _store.ReadAllAsync(sessionId, ct).ConfigureAwait(false);
        var summary = history.FirstOrDefault(record => string.Equals(
            record.MessageId,
            cached.SummaryMessageId,
            StringComparison.Ordinal));
        if (summary?.SummaryMetadata is not { } metadata
            || !TryReadSummaryMetadata(metadata, out var cachedMetadata)
            || cachedMetadata.RoundIndex != roundIndex
            || cachedMetadata.SummarizesThroughSeq != cached.SummarizesThroughSeq
            || !string.Equals(cachedMetadata.PolicyHash, policyHash, StringComparison.Ordinal)
            || !string.Equals(cachedMetadata.SummarizerAgentId, request.AgentId, StringComparison.Ordinal)
            || !string.Equals(
                cachedMetadata.SummarizerInvocationId,
                request.InvocationId,
                StringComparison.Ordinal))
        {
            return null;
        }

        var latestSourceSequence = history
            .Where(record => !string.Equals(
                record.MessageId,
                cached.SummaryMessageId,
                StringComparison.Ordinal))
            .Select(static record => record.Sequence)
            .DefaultIfEmpty(0)
            .Max();
        return latestSourceSequence == cached.SummarizesThroughSeq
            ? cached
            : null;
    }

    private static bool TryReadSummaryMetadata(
        JsonElement metadata,
        out SummaryCacheMetadata result)
    {
        result = default;
        if (!metadata.TryGetProperty("roundIndex", out var roundElement)
            || !roundElement.TryGetInt32(out var roundIndex)
            || !metadata.TryGetProperty("summarizesThroughSeq", out var sequenceElement)
            || !sequenceElement.TryGetInt64(out var summarizesThroughSeq)
            || !metadata.TryGetProperty("policyHash", out var policyElement)
            || policyElement.GetString() is not { } policyHash
            || !metadata.TryGetProperty("summarizerAgentId", out var agentElement)
            || agentElement.GetString() is not { } summarizerAgentId
            || !metadata.TryGetProperty("summarizerInvocationId", out var invocationElement)
            || invocationElement.GetString() is not { } summarizerInvocationId)
        {
            return false;
        }

        result = new SummaryCacheMetadata(
            roundIndex,
            summarizesThroughSeq,
            policyHash,
            summarizerAgentId,
            summarizerInvocationId);
        return true;
    }

    private static JsonElement CreateSummaryMetadata(
        int roundIndex,
        long summarizesThroughSeq,
        string policyHash,
        string summarizerAgentId,
        string summarizerInvocationId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("roundIndex", roundIndex);
        writer.WriteNumber("summarizesThroughSeq", summarizesThroughSeq);
        writer.WriteString("policyHash", policyHash);
        writer.WriteString("summarizerAgentId", summarizerAgentId);
        writer.WriteString("summarizerInvocationId", summarizerInvocationId);
        writer.WriteEndObject();
        writer.Flush();
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void ValidateMeetingRequest(NewSessionRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Selection);
        ArgumentNullException.ThrowIfNull(request.InitialInput);
        if (request.Mode is not RuntimeMode.Meeting
            || request.Selection.Mode is not RuntimeMode.Meeting
            || request.Selection.ModeOptions is not MeetingModeOptions options)
        {
            throw new ArgumentException("The request must contain Meeting mode options.", nameof(request));
        }

        var effectiveSelectorPolicy = options.EffectiveSelectorPolicy;
        if (effectiveSelectorPolicy.MaxRounds is not null && effectiveSelectorPolicy.MaxRounds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "EffectiveSelectorPolicy.MaxRounds must be greater than 0 when specified.");
        }

        var effectivePolicy = options.EffectivePolicy;
        if (effectivePolicy.MaxRetriesPerInvocation < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "EffectivePolicy.MaxRetriesPerInvocation must not be negative.");
        }

        if (effectivePolicy.InvocationTimeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "EffectivePolicy.InvocationTimeoutSeconds must be greater than 0.");
        }

        if (effectivePolicy.MeetingTimeoutSeconds is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "EffectivePolicy.MeetingTimeoutSeconds must be greater than 0 when specified.");
        }

        if (effectivePolicy.TerminationConditions?.Any(string.IsNullOrWhiteSpace) == true)
        {
            throw new ArgumentException(
                "EffectivePolicy.TerminationConditions must not contain blank values.",
                nameof(request));
        }

        ArgumentNullException.ThrowIfNull(options.Participants);
        if (options.Participants.Length is < MinimumParticipantCount or > MaximumParticipantCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"Meeting mode requires {MinimumParticipantCount} to {MaximumParticipantCount} participants; "
                + $"no more than {RecommendedMaximumParticipantCount} is recommended.");
        }

        var participantIds = new HashSet<string>(StringComparer.Ordinal);
        var joinOrders = new HashSet<int>();
        var hasActive = false;

        foreach (var participant in options.Participants)
        {
            ArgumentNullException.ThrowIfNull(participant);
            ArgumentException.ThrowIfNullOrWhiteSpace(participant.ParticipantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(participant.DisplayName);

            if (participant.Status == ParticipantStatus.Removed)
            {
                throw new ArgumentException(
                    $"Meeting participant '{participant.ParticipantId}' has status Removed; "
                    + "only Active or Standby participants are allowed.",
                    nameof(request));
            }

            if (participant.JoinOrder < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    $"Participant '{participant.ParticipantId}' has a negative JoinOrder.");
            }

            if (!participantIds.Add(participant.ParticipantId))
            {
                throw new ArgumentException(
                    $"Meeting participant ID '{participant.ParticipantId}' is duplicated.",
                    nameof(request));
            }

            if (!joinOrders.Add(participant.JoinOrder))
            {
                throw new ArgumentException(
                    $"Meeting participant JoinOrder '{participant.JoinOrder}' is duplicated.",
                    nameof(request));
            }

            var agent = participant.Agent;
            ArgumentNullException.ThrowIfNull(agent);
            ArgumentException.ThrowIfNullOrWhiteSpace(agent.AgentId);
            ArgumentException.ThrowIfNullOrWhiteSpace(agent.PromptTemplateVersion);
            ArgumentException.ThrowIfNullOrWhiteSpace(agent.SystemPrompt);

            if (participant.Status == ParticipantStatus.Active)
            {
                hasActive = true;
            }
        }

        if (!hasActive)
        {
            throw new ArgumentException(
                "Meeting mode requires at least one Active participant.",
                nameof(request));
        }
    }

    private static bool HasMeetingTimedOut(DateTimeOffset? deadline) =>
        deadline is not null && DateTimeOffset.UtcNow >= deadline.Value;

    private static bool MatchesTerminationCondition(
        string? participantText,
        string[]? terminationConditions) =>
        !string.IsNullOrEmpty(participantText)
        && terminationConditions?.Any(condition =>
            participantText.Contains(condition, StringComparison.OrdinalIgnoreCase)) == true;

    private void ValidateMeetingToolConfiguration(
        NewSessionRunRequest request,
        MeetingModeOptions options)
    {
        foreach (var participant in options.Participants)
        {
            var providerId = participant.Agent.ProviderId
                ?? request.Selection.DefaultSelection.ProviderId;
            _ = ResolveAvailableTools(participant.Agent, providerId, "participant");
        }

        if (options.HostAgent is { } hostAgent)
        {
            var hostProviderId = hostAgent.ProviderId
                ?? request.Selection.DefaultSelection.ProviderId;
            _ = ResolveAvailableTools(hostAgent, hostProviderId, "meeting-host");
        }
        else if (options.EffectiveSelectorPolicy.Type == MeetingSelectorPolicy.HostDriven)
        {
            var hostProviderId = request.Selection.DefaultSelection.ProviderId;
            _ = ResolveAvailableTools(BuiltInHostAgent, hostProviderId, "meeting-host");
        }
    }

    /// <summary>
    /// Builds the Meeting tool directory for a role (host / participant) via
    /// <see cref="ToolCatalogSnapshot.FilterByMode"/>, optionally intersected with
    /// <see cref="AgentRef.AllowedToolIds"/>.
    /// </summary>
    private ToolDescriptor[]? ResolveAvailableTools(
        AgentRef agent,
        string providerId,
        string meetingRole)
    {
        var catalog = CaptureCatalogSnapshot();
        if (_toolGateway is null || catalog is null)
        {
            if (agent.AllowedToolIds is { Length: > 0 })
            {
                throw new InvalidOperationException(
                    $"Meeting agent '{agent.AgentId}' declares tools, but the Runtime has no tool gateway or catalog snapshot.");
            }

            return null;
        }

        var roleCatalog = catalog.FilterByMode("meeting", meetingRole);
        IEnumerable<ToolDescriptor> selected = roleCatalog.Tools;

        // null AllowedToolIds => role default catalog;
        // empty AllowedToolIds => explicitly no tools for this agent.
        if (agent.AllowedToolIds is not null)
        {
            if (agent.AllowedToolIds.Length == 0)
            {
                return null;
            }

            var requestedToolIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var toolId in agent.AllowedToolIds)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
                if (!requestedToolIds.Add(toolId))
                {
                    throw new ArgumentException(
                        $"Meeting agent '{agent.AgentId}' declares duplicate tool ID '{toolId}'.",
                        nameof(agent));
                }

                if (!catalog.TryGetTool(toolId, out _))
                {
                    throw new ArgumentException(
                        $"Meeting agent '{agent.AgentId}' declares unknown tool ID '{toolId}'.",
                        nameof(agent));
                }
            }

            selected = roleCatalog.Tools.Where(tool => requestedToolIds.Contains(tool.ToolId));
        }

        var tools = selected.ToArray();
        if (tools.Length == 0)
        {
            return null;
        }

        var provider = _providerFactory(providerId);
        if (!provider.Capabilities.ToolCalling)
        {
            if (agent.AllowedToolIds is { Length: > 0 })
            {
                throw new ArgumentException(
                    $"Provider '{providerId}' does not support tools required by meeting agent '{agent.AgentId}'.",
                    nameof(agent));
            }

            return null;
        }

        return tools;
    }

    private ToolCatalogSnapshot? CaptureCatalogSnapshot()
    {
        if (_liveCatalogProvider is not null)
        {
            return _liveCatalogProvider();
        }

        return _toolCatalogSnapshot;
    }

    private Task<RuntimeEventEnvelope> CreateAcceptedEnvelopeAsync(
        string runId,
        string sessionId,
        string runtimeInstanceId,
        long runSequence,
        CancellationToken ct) =>
        CreateEnvelopeAsync(
            runtimeInstanceId,
            runId,
            runSequence,
            MessageTypes.RunAccepted,
            new RunAcceptedEvent(sessionId, runId),
            RuntimeJsonContext.Default.RunAcceptedEvent,
            ct);

    private async Task<RuntimeEventEnvelope?> CreateCompletedEnvelopeAsync(
        string runId,
        string sessionId,
        string runtimeInstanceId,
        long runSequence,
        string? terminalText,
        CancellationToken ct) =>
        await CommitTerminalEnvelopeAsync(
            runtimeInstanceId,
            runId,
            runSequence,
            RunStatus.Completed,
            terminalText,
            error: null,
            value: new RunCompletedEvent(runId, sessionId),
            typeInfo: RuntimeJsonContext.Default.RunCompletedEvent,
            ct: ct).ConfigureAwait(false);

    private Task<RuntimeEventEnvelope> CreateStatusEnvelopeAsync(
        string runtimeInstanceId,
        string runId,
        long runSequence,
        RunStatus status,
        string? currentStep,
        long? completedUnits,
        long? totalUnits,
        CancellationToken ct,
        string? workPhase = null) =>
        CreateEnvelopeAsync(
            runtimeInstanceId,
            runId,
            runSequence,
            MessageTypes.RunStatusChanged,
            new RunStatusChangedEvent(runId, status.ToString())
            {
                Mode = RuntimeMode.Meeting,
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
                CurrentStep = currentStep,
                WorkPhase = workPhase,
                CompletedUnits = completedUnits,
                TotalUnits = totalUnits,
                UpdatedAt = DateTimeOffset.UtcNow
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
        string runId,
        string runtimeInstanceId,
        string invocationId,
        long runSequence,
        CancellationToken ct) =>
        CreateEnvelopeAsync(
            runtimeInstanceId,
            runId,
            runSequence,
            MessageTypes.InvocationCompleted,
            new InvocationCompletedEvent(invocationId),
            RuntimeJsonContext.Default.InvocationCompletedEvent,
            ct);

    private Task<RuntimeEventEnvelope> CreateProjectionAdjustedEnvelopeAsync(
        string runId,
        string runtimeInstanceId,
        string invocationId,
        MeetingProjectionResult projection,
        long runSequence,
        CancellationToken ct) =>
        CreateEnvelopeAsync(
            runtimeInstanceId,
            runId,
            runSequence,
            MessageTypes.ContextProjectionAdjusted,
            new ContextProjectionAdjustedEvent(
                invocationId,
                projection.DroppedMessageCount,
                projection.Messages.Length,
                projection.EstimatedTokens,
                projection.EstimateSource,
                projection.Strategy),
            RuntimeJsonContext.Default.ContextProjectionAdjustedEvent,
            ct);

    private Task<RuntimeEventEnvelope> CreateProviderEventEnvelopeAsync(
        string runId,
        string runtimeInstanceId,
        RuntimeProviderEvent providerEvent,
        long runSequence,
        CancellationToken ct)
    {
        var mapped = MapProviderEvent(providerEvent);
        return CreateEnvelopeAsync(
            runtimeInstanceId,
            runId,
            runSequence,
            mapped.MessageType,
            mapped.Payload,
            ct);
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

    private async Task<RuntimeEventEnvelope?> CommitTerminalEnvelopeAsync<T>(
        string runtimeInstanceId,
        string runId,
        long runSequence,
        RunStatus terminalStatus,
        string? terminalText,
        RuntimeError? error,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
        where T : notnull
    {
        var messageType = terminalStatus switch
        {
            RunStatus.Completed => MessageTypes.RunCompleted,
            RunStatus.Cancelled => MessageTypes.RunCancelled,
            RunStatus.Interrupted => MessageTypes.RunCancelled,
            RunStatus.Failed => MessageTypes.RunFailed,
            _ => throw new ArgumentOutOfRangeException(nameof(terminalStatus))
        };
        var payload = JsonSerializer.SerializeToElement(value, typeInfo);
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

    private async Task<RuntimeEventEnvelope> CreateEnvelopeAsync<T>(
        string runtimeInstanceId,
        string runId,
        long runSequence,
        string messageType,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
        where T : notnull
    {
        var payload = JsonSerializer.SerializeToElement(value, typeInfo);
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

    private Task<SkillCatalogSnapshot?> CaptureSkillCatalogSnapshotAsync(CancellationToken ct) =>
        _skillCatalogSnapshotProvider?.Invoke(ct)
        ?? Task.FromResult<SkillCatalogSnapshot?>(null);

    private AgentInvocationRequest ApplyHostSkills(
        AgentInvocationRequest request,
        string[]? skillIds,
        SkillCatalogSnapshot? filteredCatalog,
        string? skillDiscoveryMode = null)
    {
        if (!_isHostModeSkills)
        {
            return request;
        }

        var maps = HostModeSkillsSnapshot.BuildMaps(filteredCatalog);
        var snapshot = HostModeSkillsSnapshot.ProjectAdvertiseEmpty(
            request.Snapshot with
            {
                SkillCatalogVersion = request.Snapshot.SkillCatalogVersion
                    ?? filteredCatalog?.Version
            },
            filteredCatalog);
        return request with
        {
            Snapshot = snapshot,
            IsHostModeSkills = true,
            SkillActivationStore = _skillActivationStore,
            SkillAllowlistIds = skillIds,
            SkillCatalogPathsByName = maps.PathsByName,
            SkillCatalogDescriptionsByName = maps.DescriptionsByName,
            SkillScopeSourceRoots = maps.ScopeSourceRoots,
            SkillDiscoveryMode = SkillDiscoveryModes.Normalize(skillDiscoveryMode)
        };
    }

    private static string ComputeHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static MeetingParticipantInput[] CreateParticipantInputs(
        MeetingModeOptions options)
    {
        var inputs = new MeetingParticipantInput[options.Participants.Length];
        for (var i = 0; i < options.Participants.Length; i++)
        {
            var participant = options.Participants[i];
            var agentRefJson = JsonSerializer.Serialize(
                participant.Agent,
                RuntimeJsonContext.Default.AgentRef);
            var status = participant.Status == ParticipantStatus.Active
                ? "active"
                : "standby";
            inputs[i] = new MeetingParticipantInput(
                participant.ParticipantId,
                participant.Agent.AgentId,
                agentRefJson,
                participant.DisplayName,
                participant.JoinOrder,
                status);
        }

        return inputs;
    }

    private async Task<ProjectedInvocationRequest> UsePersistedInvocationSnapshotAsync(
        ProjectedInvocationRequest projected,
        MeetingInvocationRecord invocation,
        CancellationToken ct)
    {
        var snapshotJson = await _sessionRepo.GetInvocationSnapshotJsonAsync(
            invocation.InvocationId,
            ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Invocation '{invocation.InvocationId}' has no persisted immutable snapshot.");
        var persisted = JsonSerializer.Deserialize(
            snapshotJson,
            RuntimeJsonContext.Default.InvocationSnapshot)
            ?? throw new InvalidOperationException(
                $"Invocation '{invocation.InvocationId}' has an empty persisted snapshot.");
        var regenerated = projected.Request.Snapshot with
        {
            StartedAt = persisted.StartedAt,
            SkillCatalogVersion = persisted.SkillCatalogVersion,
            SkillNames = persisted.SkillNames,
            SkillContentHashes = persisted.SkillContentHashes,
            SkillSourceIds = persisted.SkillSourceIds,
            SkillMaterializedPaths = persisted.SkillMaterializedPaths
        };
        if (!InvocationSnapshotsEquivalent(regenerated, persisted)
            || !string.Equals(invocation.InvocationId, persisted.InvocationId, StringComparison.Ordinal)
            || !string.Equals(invocation.AgentId, persisted.AgentId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Invocation '{invocation.InvocationId}' cannot resume because its immutable snapshot changed.");
        }

        return projected with
        {
            Request = projected.Request with { Snapshot = persisted }
        };
    }

    private static bool InvocationSnapshotsEquivalent(
        InvocationSnapshot first,
        InvocationSnapshot second) =>
        string.Equals(first.InvocationId, second.InvocationId, StringComparison.Ordinal)
        && string.Equals(first.AgentId, second.AgentId, StringComparison.Ordinal)
        && string.Equals(first.ProviderId, second.ProviderId, StringComparison.Ordinal)
        && string.Equals(first.ModelId, second.ModelId, StringComparison.Ordinal)
        && string.Equals(first.PromptHash, second.PromptHash, StringComparison.Ordinal)
        && string.Equals(first.GlobalMemoryHash, second.GlobalMemoryHash, StringComparison.Ordinal)
        && string.Equals(first.ProjectMemoryHash, second.ProjectMemoryHash, StringComparison.Ordinal)
        && string.Equals(first.ToolCatalogVersion, second.ToolCatalogVersion, StringComparison.Ordinal)
        && string.Equals(first.ProjectionVersion, second.ProjectionVersion, StringComparison.Ordinal)
        && first.StartedAt == second.StartedAt
        && string.Equals(first.ParentInvocationId, second.ParentInvocationId, StringComparison.Ordinal)
        && string.Equals(first.WorkStepId, second.WorkStepId, StringComparison.Ordinal)
        && string.Equals(first.WorkPlanVersion, second.WorkPlanVersion, StringComparison.Ordinal)
        && ContextProjectionEquivalent(first.ContextProjection, second.ContextProjection)
        && string.Equals(first.SkillCatalogVersion, second.SkillCatalogVersion, StringComparison.Ordinal)
        && SequenceEqual(first.SkillNames, second.SkillNames)
        && SequenceEqual(first.SkillContentHashes, second.SkillContentHashes)
        && SequenceEqual(first.SkillSourceIds, second.SkillSourceIds)
        && SequenceEqual(first.SkillMaterializedPaths, second.SkillMaterializedPaths);

    private static bool ContextProjectionEquivalent(
        ContextProjectionSnapshot? first,
        ContextProjectionSnapshot? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }

        return string.Equals(first.Strategy, second.Strategy, StringComparison.Ordinal)
            && SequenceEqual(first.RetainedItems, second.RetainedItems)
            && first.DroppedMessageCount == second.DroppedMessageCount
            && first.IncludedMessageCount == second.IncludedMessageCount
            && first.EstimatedTokens == second.EstimatedTokens
            && string.Equals(first.EstimateSource, second.EstimateSource, StringComparison.Ordinal)
            && first.TokenLimit == second.TokenLimit
            && first.SummarizedUnitCount == second.SummarizedUnitCount;
    }

    private static bool SequenceEqual<T>(T[]? first, T[]? second) =>
        first is null || second is null
            ? first is null && second is null
            : first.AsSpan().SequenceEqual(second);

    private ReconciledCanonicalText? GetReconciledCanonicalText(
        MeetingInvocationRecord invocation,
        bool expectsSummary)
    {
        if (_resumedCanonicalMessage is null)
        {
            return null;
        }

        if (!string.Equals(
                _resumedCanonicalMessage.InvocationId,
                invocation.InvocationId,
                StringComparison.Ordinal)
            || !string.Equals(
                _resumedCanonicalMessage.AgentId,
                invocation.AgentId,
                StringComparison.Ordinal)
            || !string.Equals(
                _resumedCanonicalMessage.Role,
                RuntimeProviderRoles.Assistant,
                StringComparison.Ordinal)
            || expectsSummary != (_resumedCanonicalMessage.SummaryMetadata is not null))
        {
            throw new InvalidDataException(
                $"Invocation '{invocation.InvocationId}' has an invalid canonical recovery record.");
        }

        var content = _resumedCanonicalMessage.Content.Deserialize(
            RuntimeJsonContext.Default.ContentBlockArray);
        if (content is not [TextContentBlock text]
            || string.IsNullOrWhiteSpace(text.Text))
        {
            throw new InvalidDataException(
                $"Invocation '{invocation.InvocationId}' has invalid canonical text content.");
        }

        return new ReconciledCanonicalText(_resumedCanonicalMessage, text.Text);
    }

    private async Task<List<string>> LoadSuccessfulParticipantTextsAsync(
        string sessionId,
        CancellationToken ct)
    {
        var history = await _store.ReadAllAsync(sessionId, ct).ConfigureAwait(false);
        var contextMap = await _meetingRepo.GetInvocationContextMapAsync(sessionId, ct)
            .ConfigureAwait(false);
        var texts = new List<string>();
        foreach (var record in history)
        {
            if (!string.Equals(record.Role, RuntimeProviderRoles.Assistant, StringComparison.Ordinal)
                || !contextMap.TryGetValue(record.InvocationId, out var context)
                || !string.Equals(context.Role, "participant", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var content = record.Content.Deserialize(RuntimeJsonContext.Default.ContentBlockArray);
            var text = content?.OfType<TextContentBlock>().SingleOrDefault()?.Text;
            if (!string.IsNullOrEmpty(text))
            {
                texts.Add(text);
            }
        }

        return texts;
    }

    private static void ValidateInvocationResumeSnapshot(
        MeetingSnapshot snapshot,
        string runId,
        MeetingInvocationRecord invocation)
    {
        if (!string.Equals(snapshot.Session.RunId, runId, StringComparison.Ordinal)
            || !string.Equals(snapshot.Session.SessionId, invocation.SessionId, StringComparison.Ordinal)
            || !string.Equals(invocation.RunId, runId, StringComparison.Ordinal)
            || !string.Equals(invocation.Status, "Running", StringComparison.Ordinal)
            || snapshot.Session.CurrentRound != invocation.RoundIndex
            || snapshot.CurrentRound is not { Status: "Running" }
            || snapshot.CurrentRound.RoundIndex != invocation.RoundIndex
            || !string.Equals(snapshot.Session.Status, "Running", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Meeting Invocation '{invocation.InvocationId}' is not at a recoverable running checkpoint.");
        }
    }

    private static MeetingParticipant ResolveRecoveredParticipant(
        MeetingSnapshot snapshot,
        MeetingModeOptions options,
        MeetingInvocationRecord invocation)
    {
        if (string.IsNullOrWhiteSpace(invocation.ParticipantId))
        {
            throw new InvalidOperationException(
                $"Participant Invocation '{invocation.InvocationId}' has no participant ID.");
        }

        var record = snapshot.Participants.FirstOrDefault(participant => string.Equals(
            participant.ParticipantId,
            invocation.ParticipantId,
            StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Participant '{invocation.ParticipantId}' was not found for recovered Invocation '{invocation.InvocationId}'.");
        AgentRef? agentRef = null;
        if (!string.IsNullOrWhiteSpace(record.AgentRefJson))
        {
            try
            {
                agentRef = JsonSerializer.Deserialize(
                    record.AgentRefJson,
                    RuntimeJsonContext.Default.AgentRef);
            }
            catch (JsonException)
            {
                agentRef = null;
            }
        }

        if (agentRef is null || !IsAgentRefComplete(agentRef))
        {
            agentRef = options.Participants.FirstOrDefault(participant => string.Equals(
                participant.ParticipantId,
                invocation.ParticipantId,
                StringComparison.Ordinal))?.Agent;
        }

        if (agentRef is null
            || !IsAgentRefComplete(agentRef)
            || !string.Equals(agentRef.AgentId, invocation.AgentId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Participant '{invocation.ParticipantId}' has no matching Agent definition for recovered Invocation '{invocation.InvocationId}'.");
        }

        return new MeetingParticipant(
            record.ParticipantId,
            agentRef,
            record.DisplayName ?? record.ParticipantId,
            record.JoinOrder,
            ParticipantStatus.Active);
    }

    private async Task<(List<MeetingParticipant> ActiveParticipants, int SelectionVersion)> ReloadActiveParticipantsAsync(
        string sessionId,
        MeetingModeOptions options,
        CancellationToken ct)
    {
        var snapshot = await _meetingRepo.GetMeetingSnapshotAsync(sessionId, ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            throw new InvalidOperationException(
                $"Meeting session snapshot not found for session '{sessionId}'.");
        }

        var activeParticipants = new List<MeetingParticipant>();
        foreach (var record in snapshot.Participants)
        {
            if (string.Equals(record.Status, "removed", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(record.Status, "standby", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(record.Status, "active", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Participant '{record.ParticipantId}' has unknown status '{record.Status}'.");
            }

            AgentRef? agentRef = null;
            if (!string.IsNullOrWhiteSpace(record.AgentRefJson))
            {
                try
                {
                    agentRef = JsonSerializer.Deserialize(
                        record.AgentRefJson,
                        RuntimeJsonContext.Default.AgentRef);
                }
                catch (JsonException)
                {
                    agentRef = null;
                }
            }

            if (agentRef is null || !IsAgentRefComplete(agentRef))
            {
                var fallback = options.Participants.FirstOrDefault(
                    p => string.Equals(p.ParticipantId, record.ParticipantId, StringComparison.Ordinal));
                if (fallback is null || !IsAgentRefComplete(fallback.Agent))
                {
                    throw new InvalidOperationException(
                        $"Participant '{record.ParticipantId}' has no valid agent JSON and no fallback definition.");
                }

                agentRef = fallback.Agent;
            }

            activeParticipants.Add(new MeetingParticipant(
                record.ParticipantId,
                agentRef,
                record.DisplayName ?? record.ParticipantId,
                record.JoinOrder,
                ParticipantStatus.Active));
        }

        activeParticipants.Sort((a, b) =>
        {
            var cmp = a.JoinOrder.CompareTo(b.JoinOrder);
            return cmp != 0 ? cmp : string.Compare(a.ParticipantId, b.ParticipantId, StringComparison.Ordinal);
        });

        return (activeParticipants, snapshot.Session.SelectionVersion);
    }

    private static bool IsAgentRefComplete(AgentRef agentRef)
        => !string.IsNullOrWhiteSpace(agentRef.AgentId)
           && !string.IsNullOrWhiteSpace(agentRef.PromptTemplateVersion)
           && !string.IsNullOrWhiteSpace(agentRef.SystemPrompt);

    private static MeetingInvocationPlan[] CreateInvocationPlans(
        List<MeetingParticipant> participants,
        string sessionId,
        string runId,
        int roundIndex,
        int selectionVersion)
    {
        var plans = new MeetingInvocationPlan[participants.Count];
        for (var i = 0; i < participants.Count; i++)
        {
            var participant = participants[i];
            var ordinal = i;
            var invocationId = CreateStableId(
                "meeting-invocation-v1",
                sessionId,
                runId,
                roundIndex.ToString(CultureInfo.InvariantCulture),
                ordinal.ToString(CultureInfo.InvariantCulture),
                participant.ParticipantId,
                selectionVersion.ToString(CultureInfo.InvariantCulture));
            plans[i] = new MeetingInvocationPlan(ordinal, participant, invocationId);
        }

        return plans;
    }

    private static MeetingInvocationScheduleInput CreateSchedule(
        MeetingInvocationPlan plan,
        int selectionVersion) =>
        new(
            plan.InvocationId,
            plan.Participant.ParticipantId,
            plan.Participant.Agent.AgentId,
            "participant",
            selectionVersion);

    private static string CreateStableId(params string[] components)
    {
        ArgumentNullException.ThrowIfNull(components);
        var joined = string.Join("\n", components);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
    }

    private async Task PersistInitialInputAsync(
        string sessionId,
        string runId,
        ContentBlock[] content,
        CancellationToken ct) =>
        await PersistMeetingUserMessageAsync(
            sessionId,
            CreateStableId("meeting-input-message-v1", sessionId, runId),
            CreateStableId("meeting-input-invocation-v1", sessionId, runId),
            content,
            "initial input",
            ct).ConfigureAwait(false);

    private async Task PersistHitlSupplementAsync(
        string sessionId,
        string runId,
        string approvalRequestId,
        string supplement,
        CancellationToken ct) =>
        await PersistMeetingUserMessageAsync(
            sessionId,
            CreateStableId(
                "meeting-hitl-supplement-message-v1",
                sessionId,
                runId,
                approvalRequestId),
            CreateStableId(
                "meeting-hitl-supplement-invocation-v1",
                sessionId,
                runId,
                approvalRequestId),
            [new TextContentBlock(supplement)],
            "HITL supplement",
            ct).ConfigureAwait(false);

    private async Task PersistMeetingUserMessageAsync(
        string sessionId,
        string messageId,
        string invocationId,
        ContentBlock[] content,
        string description,
        CancellationToken ct)
    {
        var serialized = JsonSerializer.SerializeToElement(
            content,
            RuntimeJsonContext.Default.ContentBlockArray);
        var history = await _store.ReadAllAsync(sessionId, ct).ConfigureAwait(false);
        var existing = history.FirstOrDefault(record =>
            string.Equals(record.MessageId, messageId, StringComparison.Ordinal));
        if (existing is not null)
        {
            if (!string.Equals(existing.InvocationId, invocationId, StringComparison.Ordinal)
                || !string.Equals(existing.AgentId, "meeting.user", StringComparison.Ordinal)
                || !string.Equals(existing.Role, "user", StringComparison.Ordinal)
                || !JsonElement.DeepEquals(existing.Content, serialized))
            {
                throw new InvalidOperationException(
                    $"The persisted {description} for meeting session '{sessionId}' does not match the Run request.");
            }

            return;
        }

        await _store.AppendMessageAsync(
            sessionId,
            Mode.ToString(),
            new ConversationRecordV1(
                messageId,
                await _store.GetLastSequenceAsync(sessionId, ct).ConfigureAwait(false) + 1,
                invocationId,
                "meeting.user",
                "user",
                serialized,
                DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);
    }

    private static bool IsEndConfirmationApprovalRequestId(string approvalRequestId) =>
        approvalRequestId.Contains(EndConfirmationApprovalSuffix, StringComparison.Ordinal);

    private async Task DrainPendingInputsAtSafePointAsync(
        string sessionId,
        string runId,
        CancellationToken ct)
    {
        var pending = await _meetingRepo.ListPendingInputsAsync(sessionId, ct).ConfigureAwait(false);
        foreach (var item in pending)
        {
            ContentBlock[] content;
            try
            {
                content = JsonSerializer.Deserialize(
                    item.ContentJson,
                    RuntimeJsonContext.Default.ContentBlockArray)
                    ?? [new TextContentBlock(item.ContentJson)];
            }
            catch (JsonException)
            {
                content = [new TextContentBlock(item.ContentJson)];
            }

            var messageId = CreateStableId(
                "meeting-pending-input-message-v1",
                sessionId,
                runId,
                item.RequestId);
            await PersistMeetingUserMessageAsync(
                sessionId,
                messageId,
                CreateStableId(
                    "meeting-pending-input-invocation-v1",
                    sessionId,
                    runId,
                    item.RequestId),
                content,
                "pending user interrupt",
                ct).ConfigureAwait(false);
            await _meetingRepo.MarkPendingInputConsumedAsync(item.RequestId, messageId, ct)
                .ConfigureAwait(false);
        }
    }

    private async Task<RuntimeEventEnvelope?> TryPauseAtSafePointAsync(
        string sessionId,
        string runId,
        string runtimeInstanceId,
        Func<long> nextRunSequence,
        CancellationToken ct)
    {
        var snapshot = await _meetingRepo.GetMeetingSnapshotAsync(sessionId, ct)
            .ConfigureAwait(false);
        if (snapshot is null || !snapshot.Session.PauseRequested)
        {
            return null;
        }

        var reason = snapshot.Session.PauseReason ?? "Paused by host request.";
        var paused = await _meetingRepo.TryTransitionMeetingStatusAsync(
            sessionId,
            "Running",
            "Paused",
            ct).ConfigureAwait(false);
        if (!paused)
        {
            // Already left Running (e.g. concurrent terminal transition); clear stale pause flag only.
            await _meetingRepo.ClearPauseRequestAsync(sessionId, ct).ConfigureAwait(false);
            return null;
        }

        await _meetingRepo.ClearPauseRequestAsync(sessionId, ct).ConfigureAwait(false);
        await _sessionRepo.TransitionRunToTerminalAsync(
            runId,
            RunStatus.Running,
            RunStatus.Interrupted,
            reason,
            ct).ConfigureAwait(false);

        return await CommitTerminalEnvelopeAsync(
            runtimeInstanceId,
            runId,
            nextRunSequence(),
            RunStatus.Interrupted,
            reason,
            error: null,
            value: new RunCancelledEvent(runId, sessionId),
            typeInfo: RuntimeJsonContext.Default.RunCancelledEvent,
            ct: ct).ConfigureAwait(false);
    }

    private async Task PersistContinuationResumeInputAsync(
        string sessionId,
        string runId,
        ContentBlock[]? inputOverride,
        CancellationToken ct)
    {
        var resumeText = "Meeting continuation after cooperative pause. "
            + "Absorb any pending user interrupts, then select the next participant or conclude.";
        if (inputOverride is { Length: > 0 })
        {
            var overrideText = string.Join(
                "\n",
                inputOverride.OfType<TextContentBlock>().Select(static block => block.Text));
            if (!string.IsNullOrWhiteSpace(overrideText))
            {
                resumeText = resumeText + "\n\nHost note:\n" + overrideText;
            }
        }

        ContentBlock[] content = [new TextContentBlock(resumeText)];
        var messageId = CreateStableId(
            "meeting-continuation-input-v1",
            sessionId,
            runId);
        var invocationId = CreateStableId(
            "meeting-continuation-invocation-v1",
            sessionId,
            runId);
        await PersistMeetingUserMessageAsync(
            sessionId,
            messageId,
            invocationId,
            content,
            "meeting continuation resume input",
            ct).ConfigureAwait(false);
    }

    private static void ValidateHitlResumeSnapshot(
        MeetingSnapshot snapshot,
        string runId,
        int runFirstRoundIndex,
        MeetingHitlResponse response)
    {
        if (!string.Equals(snapshot.Session.RunId, runId, StringComparison.Ordinal)
            || snapshot.Session.CurrentRound != runFirstRoundIndex
            || snapshot.CurrentRound is not { Status: "Running" } currentRound
            || currentRound.RoundIndex != runFirstRoundIndex
            || snapshot.NextScheduledInvocation is null
            || !string.Equals(
                snapshot.Session.PendingApprovalRequestId,
                response.ApprovalRequestId,
                StringComparison.Ordinal)
            || !string.Equals(snapshot.PendingApprovalStatus, "Decided", StringComparison.Ordinal)
            || snapshot.Session.Status is not ("WaitingForApproval" or "Running"))
        {
            throw new InvalidOperationException(
                $"Meeting session '{snapshot.Session.SessionId}' is not at a recoverable post-HITL checkpoint.");
        }
    }

    private static void ValidateEndConfirmationHitlResumeSnapshot(
        MeetingSnapshot snapshot,
        string runId,
        MeetingHitlResponse response)
    {
        if (!string.Equals(snapshot.Session.RunId, runId, StringComparison.Ordinal)
            || snapshot.Session.CurrentRound <= 0
            || snapshot.NextScheduledInvocation is not null
            || !IsEndConfirmationApprovalRequestId(response.ApprovalRequestId)
            || !string.Equals(
                snapshot.Session.PendingApprovalRequestId,
                response.ApprovalRequestId,
                StringComparison.Ordinal)
            || !string.Equals(snapshot.PendingApprovalStatus, "Decided", StringComparison.Ordinal)
            || snapshot.Session.Status is not ("WaitingForApproval" or "Running"))
        {
            throw new InvalidOperationException(
                $"Meeting session '{snapshot.Session.SessionId}' is not at a recoverable end-confirmation checkpoint.");
        }
    }

    private async Task ResumeFromPersistedHitlAsync(
        string sessionId,
        string runId,
        CancellationToken ct)
    {
        if (_resumedHitlResponse is null)
        {
            return;
        }

        var meetingResumed = await _meetingRepo.TryTransitionMeetingStatusAsync(
            sessionId,
            "WaitingForApproval",
            "Running",
            ct).ConfigureAwait(false);
        if (!meetingResumed)
        {
            throw new InvalidOperationException(
                $"Meeting session '{sessionId}' is no longer waiting at the persisted HITL checkpoint.");
        }

        var runStatus = await _sessionRepo.GetRunStatusAsync(runId, ct).ConfigureAwait(false);
        if (runStatus == RunStatus.WaitingForApproval)
        {
            await _sessionRepo.TransitionRunStatusAsync(
                runId,
                RunStatus.WaitingForApproval,
                RunStatus.Running,
                ct).ConfigureAwait(false);
        }
        else if (runStatus != RunStatus.Running)
        {
            throw new InvalidOperationException(
                $"Run '{runId}' is in state '{runStatus}' and cannot resume from meeting HITL.");
        }
    }

    private async Task FailMeetingAndRunAsync(
        string sessionId, string runId, string reason, CancellationToken ct)
    {
        await _meetingRepo.TryTransitionMeetingStatusAsync(
            sessionId, "Running", "Failed", ct).ConfigureAwait(false);
        await _sessionRepo.TransitionRunToTerminalAsync(
            runId, RunStatus.Running, RunStatus.Failed, reason, ct).ConfigureAwait(false);
    }

    private async Task CancelMeetingAndRunAsync(
        string sessionId,
        string runId,
        string reason,
        CancellationToken ct,
        bool transitionRun = true)
    {
        var meetingTransitioned = await _meetingRepo.TryTransitionMeetingStatusAsync(
            sessionId, "Running", "Cancelled", ct).ConfigureAwait(false);
        if (!meetingTransitioned)
        {
            await _meetingRepo.TryTransitionMeetingStatusAsync(
                sessionId, "WaitingForApproval", "Cancelled", ct).ConfigureAwait(false);
        }

        if (!transitionRun)
        {
            return;
        }

        try
        {
            await _sessionRepo.TransitionRunToTerminalAsync(
                runId, RunStatus.Running, RunStatus.Cancelled, reason, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            await _sessionRepo.TransitionRunToTerminalAsync(
                runId, RunStatus.WaitingForApproval, RunStatus.Cancelled, reason, ct).ConfigureAwait(false);
        }
    }

    private async Task PersistAssistantMessageAsync(
        string sessionId,
        AgentInvocationRequest request,
        string text,
        string messageId,
        CancellationToken ct)
    {
        var sequence = await _store.GetLastSequenceAsync(sessionId, ct).ConfigureAwait(false) + 1;
        ContentBlock[] content = [new TextContentBlock(text)];
        var jsonElement = JsonSerializer.SerializeToElement(
            content,
            RuntimeJsonContext.Default.ContentBlockArray);
        var record = new ConversationRecordV1(
            messageId,
            sequence,
            request.InvocationId,
            request.Snapshot.AgentId,
            "assistant",
            jsonElement,
            DateTimeOffset.UtcNow);
        await _store.AppendMessageAsync(sessionId, Mode.ToString(), record, ct).ConfigureAwait(false);
    }

    private const string SelectorProjectionVersion = "meeting-selector-v1";
    private const string HostProjectionVersion = "meeting-host-v1";

    private static AgentRef BuiltInSelectorAgent => new(
        "meeting.selector",
        "runtime-v1",
        "Select exactly one participantId from the candidate list and return only JSON with a participantId string.");

    private static AgentRef BuiltInHostAgent => new(
        "meeting.host",
        "runtime-v1",
        "You are the meeting host. Speak in natural language to open the floor, steer discussion, "
        + "and handle interruptions. Use meeting tools when needed: mcli_meeting_list_attachments, "
        + "mcli_check_pending_user_input, mcli_ask_user, mcli_meeting_output_summary, "
        + "mcli_meeting_confirm_end. To give the floor to one participant, end with a decision line "
        + "JSON {\"conclude\":false,\"participantId\":\"...\"}. To end after an explicit user approval "
        + "and a written summary, call mcli_meeting_confirm_end (or emit {\"conclude\":true,\"participantId\":null} "
        + "only as a soft exit signal). Do not invent participant ids.");

    private static AgentRef BuiltInSummarizerAgent => new(
        "meeting.summarizer",
        "runtime-v1",
        "Summarize only the supplied public meeting history. Preserve decisions, disagreements, and open actions.");

    private static RuntimeStructuredOutput CreateSelectorStructuredOutput()
    {
        using var document = JsonDocument.Parse("""
            {"type":"object","properties":{"participantId":{"type":"string"}},"required":["participantId"],"additionalProperties":false}
            """);
        return new RuntimeStructuredOutput(
            "meeting_selector_decision",
            document.RootElement.Clone(),
            "Select exactly one active meeting participant.");
    }

    private async Task<ProjectedInvocationRequest> CreateHostInvocationRequestAsync(
        string runId,
        string sessionId,
        NewSessionRunRequest request,
        MeetingModeOptions options,
        IReadOnlyList<MeetingParticipant> candidates,
        int roundIndex,
        MeetingContextStrategy? projectionStrategyOverride,
        string invocationId,
        CancellationToken ct)
    {
        var agent = options.HostAgent ?? BuiltInHostAgent;
        var providerId = agent.ProviderId ?? request.Selection.DefaultSelection.ProviderId;
        var modelId = agent.ModelId ?? request.Selection.DefaultSelection.ModelId;
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var context = _agentContextComposer is null
            ? AgentContextComposer.WithoutMemory(agent.SystemPrompt)
            : await _agentContextComposer.ComposeAsync(agent.SystemPrompt, ct)
                .ConfigureAwait(false);

        var systemInstructions = context.Instructions;
        if (options.Participants.Length > 1)
        {
            systemInstructions = AppendAvailableParticipantsList(systemInstructions, options.Participants);
        }

        var systemMessage = new RuntimeProviderMessage(
            RuntimeProviderRoles.System,
            [new TextContentBlock(systemInstructions)]);
        var projection = await BuildProjectedMessagesAsync(
            sessionId,
            invocationId,
            agent.AgentId,
            providerId,
            modelId,
            systemMessage,
            options,
            roundIndex,
            MeetingInvocationRole.Host,
            projectionStrategyOverride,
            ct: ct).ConfigureAwait(false);
        var skillCatalogSnapshot = (await CaptureSkillCatalogSnapshotAsync(ct).ConfigureAwait(false))
            ?.FilterBySkillIds(agent.SkillIds);
        var snapshot = new InvocationSnapshot(
            invocationId,
            agent.AgentId,
            providerId,
            modelId,
            ComputeHash(agent.SystemPrompt),
            context.GlobalMemoryHash,
            context.ProjectMemoryHash,
            request.Selection.ToolCatalogVersion,
            HostProjectionVersion,
            DateTimeOffset.UtcNow,
            ParentInvocationId: null,
            WorkStepId: null,
            WorkPlanVersion: null,
            Title: null,
            ParentStepId: null,
            Depth: 0,
            DisplayName: agent.AgentId,
            Role: MeetingInvocationRole.Host.ToString(),
            ParticipantId: null,
            RoundIndex: roundIndex,
            ContextProjection: null,
            SkillCatalogVersion: skillCatalogSnapshot?.Version,
            SkillNames: skillCatalogSnapshot?.Skills.Select(static skill => skill.Name).ToArray(),
            SkillContentHashes: skillCatalogSnapshot?.Skills.Select(static skill => skill.ContentHash).ToArray(),
            SkillSourceIds: skillCatalogSnapshot?.Skills.Select(static skill => skill.SourceId).ToArray(),
            SkillMaterializedPaths: skillCatalogSnapshot?.Skills.Select(static skill => skill.MaterializedPath).ToArray());

        var availableTools = ResolveAvailableTools(agent, providerId, "meeting-host");
        return new ProjectedInvocationRequest(
            ApplyHostSkills(
                new AgentInvocationRequest(
                    invocationId,
                    runId,
                    sessionId,
                    agent.AgentId,
                    providerId,
                    modelId,
                    [
                        systemMessage,
                        .. projection.Messages,
                        new RuntimeProviderMessage(
                            RuntimeProviderRoles.User,
                            [new TextContentBlock(
                                "Active participants:\n" + CreateCandidateJson(candidates)
                                + "\nHost this round in natural language. Use meeting tools when needed. "
                                + "When giving the floor, include a final decision JSON "
                                + "{\"conclude\":false,\"participantId\":\"<id>\"}. "
                                + "Closing sequence: mcli_meeting_output_summary → "
                                + "mcli_check_pending_user_input → mcli_ask_user (if ending) → "
                                + "mcli_meeting_confirm_end after explicit approval. "
                                + "Soft exit without tools: {\"conclude\":true,\"participantId\":null}.")])
                    ],
                    snapshot,
                    AvailableTools: availableTools,
                    StructuredOutput: null,
                    HistoryMessageCount: 1 + projection.Messages.Length),
                agent.SkillIds,
                skillCatalogSnapshot,
                agent.SkillDiscoveryMode),
            projection);
    }

    private async Task<ProjectedInvocationRequest> CreateSelectorInvocationRequestAsync(
        string runId,
        string sessionId,
        NewSessionRunRequest request,
        MeetingModeOptions options,
        IReadOnlyList<MeetingParticipant> candidates,
        int roundIndex,
        MeetingContextStrategy? projectionStrategyOverride,
        string invocationId,
        CancellationToken ct)
    {
        var agent = options.EffectiveSelectorPolicy.SelectorAgent ?? BuiltInSelectorAgent;
        var providerId = agent.ProviderId ?? request.Selection.DefaultSelection.ProviderId;
        var modelId = agent.ModelId ?? request.Selection.DefaultSelection.ModelId;
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var context = _agentContextComposer is null
            ? AgentContextComposer.WithoutMemory(agent.SystemPrompt)
            : await _agentContextComposer.ComposeAsync(agent.SystemPrompt, ct).ConfigureAwait(false);

        var systemInstructions = context.Instructions;
        if (options.Participants.Length > 1)
        {
            systemInstructions = AppendAvailableParticipantsList(systemInstructions, options.Participants);
        }

        var systemMessage = new RuntimeProviderMessage(
            RuntimeProviderRoles.System,
            [new TextContentBlock(systemInstructions)]);
        var projection = await BuildProjectedMessagesAsync(
            sessionId,
            invocationId,
            agent.AgentId,
            providerId,
            modelId,
            systemMessage,
            options,
            roundIndex,
            MeetingInvocationRole.Selector,
            projectionStrategyOverride,
            ct: ct).ConfigureAwait(false);
        var skillCatalogSnapshot = (await CaptureSkillCatalogSnapshotAsync(ct).ConfigureAwait(false))
            ?.FilterBySkillIds(agent.SkillIds);
        var snapshot = new InvocationSnapshot(
            invocationId,
            agent.AgentId,
            providerId,
            modelId,
            ComputeHash(agent.SystemPrompt),
            context.GlobalMemoryHash,
            context.ProjectMemoryHash,
            request.Selection.ToolCatalogVersion,
            SelectorProjectionVersion,
            DateTimeOffset.UtcNow,
            ParentInvocationId: null,
            WorkStepId: null,
            WorkPlanVersion: null,
            Title: null,
            ParentStepId: null,
            Depth: 0,
            DisplayName: agent.AgentId,
            Role: MeetingInvocationRole.Selector.ToString(),
            ParticipantId: null,
            RoundIndex: roundIndex,
            ContextProjection: null,
            SkillCatalogVersion: skillCatalogSnapshot?.Version,
            SkillNames: skillCatalogSnapshot?.Skills.Select(static skill => skill.Name).ToArray(),
            SkillContentHashes: skillCatalogSnapshot?.Skills.Select(static skill => skill.ContentHash).ToArray(),
            SkillSourceIds: skillCatalogSnapshot?.Skills.Select(static skill => skill.SourceId).ToArray(),
            SkillMaterializedPaths: skillCatalogSnapshot?.Skills.Select(static skill => skill.MaterializedPath).ToArray());

        return new ProjectedInvocationRequest(
            ApplyHostSkills(
                new AgentInvocationRequest(
                    invocationId,
                    runId,
                    sessionId,
                    agent.AgentId,
                    providerId,
                    modelId,
                    [
                        systemMessage,
                        .. projection.Messages,
                        new RuntimeProviderMessage(
                            RuntimeProviderRoles.User,
                            [new TextContentBlock("Candidates (choose one participantId):\n" + CreateCandidateJson(candidates))])
                    ],
                    snapshot,
                    AvailableTools: null,
                    StructuredOutput: CreateSelectorStructuredOutput(),
                    HistoryMessageCount: 1 + projection.Messages.Length),
                agent.SkillIds,
                skillCatalogSnapshot,
                agent.SkillDiscoveryMode),
            projection);
    }

    private static string CreateCandidateJson(IReadOnlyList<MeetingParticipant> candidates)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        writer.WriteStartArray();
        foreach (var candidate in candidates)
        {
            writer.WriteStartObject();
            writer.WriteString("participantId", candidate.ParticipantId);
            writer.WriteString("displayName", candidate.DisplayName);
            writer.WriteNumber("joinOrder", candidate.JoinOrder);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static MeetingParticipant? TryResolveSelectorParticipant(
        string selectorText,
        List<MeetingParticipant> candidates)
    {
        try
        {
            using var document = JsonDocument.Parse(selectorText);
            if (!document.RootElement.TryGetProperty("participantId", out var participantId)
                || participantId.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = participantId.GetString();
            return candidates.FirstOrDefault(p =>
                string.Equals(p.ParticipantId, value, StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryResolveHostDecision(
        string? hostText,
        List<MeetingParticipant> candidates,
        out MeetingParticipant? participant,
        out bool conclude)
    {
        participant = null;
        conclude = false;
        if (string.IsNullOrWhiteSpace(hostText))
        {
            return false;
        }

        if (TryParseHostDecisionJson(hostText, candidates, out participant, out conclude))
        {
            return true;
        }

        // Prefer the last JSON object embedded in natural-language host speech.
        var embedded = ExtractLastJsonObject(hostText);
        if (embedded is not null
            && TryParseHostDecisionJson(embedded, candidates, out participant, out conclude))
        {
            return true;
        }

        // Soft conclude phrases without tools (compat path).
        if (ContainsHostConcludePhrase(hostText))
        {
            conclude = true;
            return true;
        }

        // Match an explicit participantId token or unique display name.
        foreach (var candidate in candidates)
        {
            if (hostText.Contains(candidate.ParticipantId, StringComparison.Ordinal))
            {
                participant = candidate;
                conclude = false;
                return true;
            }
        }

        MeetingParticipant? byName = null;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.DisplayName))
            {
                continue;
            }

            if (!hostText.Contains(candidate.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (byName is not null
                && !string.Equals(
                    byName.ParticipantId,
                    candidate.ParticipantId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            byName = candidate;
        }

        if (byName is not null)
        {
            participant = byName;
            conclude = false;
            return true;
        }

        return false;
    }

    private static bool TryParseHostDecisionJson(
        string jsonText,
        List<MeetingParticipant> candidates,
        out MeetingParticipant? participant,
        out bool conclude)
    {
        participant = null;
        conclude = false;
        try
        {
            using var document = JsonDocument.Parse(jsonText);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (document.RootElement.TryGetProperty("conclude", out var concludeElement)
                && concludeElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                conclude = concludeElement.GetBoolean();
                if (conclude)
                {
                    return true;
                }
            }

            if (!document.RootElement.TryGetProperty("participantId", out var participantId)
                || participantId.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var value = participantId.GetString();
            participant = candidates.FirstOrDefault(candidate => string.Equals(
                candidate.ParticipantId,
                value,
                StringComparison.Ordinal));
            return participant is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ExtractLastJsonObject(string text)
    {
        var end = text.LastIndexOf('}');
        if (end < 0)
        {
            return null;
        }

        var depth = 0;
        for (var i = end; i >= 0; i--)
        {
            var ch = text[i];
            if (ch == '}')
            {
                depth++;
            }
            else if (ch == '{')
            {
                depth--;
                if (depth == 0)
                {
                    return text[i..(end + 1)];
                }
            }
        }

        return null;
    }

    private static bool ContainsHostConcludePhrase(string text)
    {
        string[] phrases =
        [
            "conclude the meeting",
            "end the meeting",
            "meeting is concluded",
            "会议结束",
            "结束会议",
            "可以结束"
        ];
        foreach (var phrase in phrases)
        {
            if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static MeetingParticipant? SelectDeterministicFallback(
        List<MeetingParticipant> candidates,
        int roundIndex)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates[(roundIndex - 1) % candidates.Count];
    }

    private static string CreateSelectorDecisionJson(string participantId, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(participantId);
        if (source is not ("selector" or "fallback"))
        {
            throw new ArgumentException("Selector decision source must be selector or fallback.", nameof(source));
        }

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("participantId", participantId);
        writer.WriteString("source", source);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string CreateHostDecisionJson(string? participantId, bool conclude)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean("conclude", conclude);
        if (participantId is null)
        {
            writer.WriteNull("participantId");
        }
        else
        {
            writer.WriteString("participantId", participantId);
        }

        writer.WriteString("source", "host");
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static MeetingInvocationScheduleInput CreateSelectorSchedule(
        string sessionId,
        string runId,
        int roundIndex,
        int selectionVersion,
        AgentRef selectorAgent) =>
        new(
            CreateRoleInvocationId(sessionId, runId, roundIndex, "selector", string.Empty, selectionVersion),
            null,
            selectorAgent.AgentId,
            "selector",
            selectionVersion);

    private static MeetingInvocationScheduleInput CreateHostSchedule(
        string sessionId,
        string runId,
        int roundIndex,
        int selectionVersion,
        AgentRef hostAgent) =>
        new(
            CreateRoleInvocationId(
                sessionId,
                runId,
                roundIndex,
                "host",
                string.Empty,
                selectionVersion),
            null,
            hostAgent.AgentId,
            "host",
            selectionVersion);

    private static MeetingInvocationScheduleInput CreateDecisionRoleSchedule(
        string sessionId,
        string runId,
        int roundIndex,
        int selectionVersion,
        MeetingModeOptions options,
        bool isHostDriven) =>
        isHostDriven
            ? CreateHostSchedule(
                sessionId,
                runId,
                roundIndex,
                selectionVersion,
                options.HostAgent ?? BuiltInHostAgent)
            : CreateSelectorSchedule(
                sessionId,
                runId,
                roundIndex,
                selectionVersion,
                options.EffectiveSelectorPolicy.SelectorAgent ?? BuiltInSelectorAgent);

    private static MeetingInvocationScheduleInput CreateSummarizerSchedule(
        string sessionId,
        string runId,
        int roundIndex,
        int selectionVersion,
        AgentRef summarizerAgent) =>
        new(
            CreateRoleInvocationId(
                sessionId,
                runId,
                roundIndex,
                "summarizer",
                string.Empty,
                selectionVersion),
            null,
            summarizerAgent.AgentId,
            "summarizer",
            selectionVersion);

    private static MeetingInvocationScheduleInput CreateParticipantRoleSchedule(
        string sessionId,
        string runId,
        int roundIndex,
        int selectionVersion,
        MeetingParticipant participant) =>
        new(
            CreateRoleInvocationId(
                sessionId,
                runId,
                roundIndex,
                "participant",
                participant.ParticipantId,
                selectionVersion),
            participant.ParticipantId,
            participant.Agent.AgentId,
            "participant",
            selectionVersion);

    private static string CreateRoleInvocationId(
        string sessionId,
        string runId,
        int roundIndex,
        string role,
        string participantId,
        int selectionVersion) =>
        CreateStableId(
            "meeting-invocation-v2",
            sessionId,
            runId,
            roundIndex.ToString(CultureInfo.InvariantCulture),
            role,
            participantId,
            selectionVersion.ToString(CultureInfo.InvariantCulture));

    private sealed record MeetingInvocationPlan(
        int Ordinal,
        MeetingParticipant Participant,
        string InvocationId);

    private sealed record ProjectedInvocationRequest(
        AgentInvocationRequest Request,
        MeetingProjectionResult Projection);

    private readonly record struct SummaryCacheMetadata(
        int RoundIndex,
        long SummarizesThroughSeq,
        string PolicyHash,
        string SummarizerAgentId,
        string SummarizerInvocationId);

    private sealed record ReconciledCanonicalText(
        ConversationRecordV1 Record,
        string Text);

    private static string AppendAvailableParticipantsList(string baseInstructions, MeetingParticipant[] participants)
    {
        if (participants.Length <= 1)
        {
            return baseInstructions;
        }

        var participantListBuilder = new StringBuilder();
        participantListBuilder.AppendLine();
        participantListBuilder.AppendLine();
        participantListBuilder.AppendLine("## Participant Selection in Meeting Mode");
        participantListBuilder.AppendLine();
        participantListBuilder.AppendLine("When responding, you must output a JSON object with the following structure:");
        participantListBuilder.AppendLine("```json");
        participantListBuilder.AppendLine("{");
        participantListBuilder.AppendLine("  \"participantId\": \"<participant-id>\",");
        participantListBuilder.AppendLine("  \"conclude\": false");
        participantListBuilder.AppendLine("}");
        participantListBuilder.AppendLine("```");
        participantListBuilder.AppendLine();
        participantListBuilder.AppendLine("Available participants (use their participantId in your response):");
        participantListBuilder.AppendLine();

        foreach (var participant in participants)
        {
            if (participant.Status == ParticipantStatus.Active)
            {
                participantListBuilder.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"- **{participant.DisplayName}** (participantId: `{participant.ParticipantId}`, agent: {participant.Agent.AgentId})");
            }
        }

        participantListBuilder.AppendLine();
        participantListBuilder.AppendLine("When the user mentions \"@ParticipantName\" or \"let {Participant} speak\", select that participant's participantId.");
        participantListBuilder.AppendLine("Set `conclude: true` to end the meeting, or `conclude: false` to continue with the selected participant.");
        participantListBuilder.AppendLine("The orchestrator will automatically invoke the selected participant for the next round.");

        return baseInstructions + participantListBuilder.ToString();
    }
}
