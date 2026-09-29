using System.Runtime.CompilerServices;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Contracts.Work;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Modes.Work;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Services.Skills;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Modes.Tests;

[TestClass]
public sealed class WorkModeOrchestratorTests
{
    private static readonly string[] ExpectedAgentIds = ["manager", "worker"];
    private static readonly string[] ExpectedManagerOnlyAgentIds = ["manager"];
    private static readonly string[] ExpectedManagerThenWorkerAgentIds = ["manager", "worker"];
    private static readonly string[] ExpectedDiscussionThenExecutionAgentIds = ["manager", "manager", "worker"];
    private static readonly string[] ExpectedTwoManagerAgentIds = ["manager", "manager"];
    private static readonly string[] ExpectedRetryAgentIds = ["manager", "worker", "worker"];
    private static readonly string[] ExpectedCredentialRetryAgentIds = ["manager", "worker", "worker"];
    private static readonly string[] ExpectedReplayDeltas = ["result:worker"];
    private static readonly string?[] ExpectedCanonicalKinds = ["work.plan", "work.step"];
    private const string DuplicateGoalPlanJson =
        "{\"planVersion\":\"1\",\"goal\":\"Ship\",\"steps\":["
        + "{\"stepId\":\"step-a\",\"goal\":\"Repeat target\",\"targetAgentId\":\"worker\"},"
        + "{\"stepId\":\"step-b\",\"goal\":\"Repeat target\",\"targetAgentId\":\"worker\",\"dependsOn\":[\"step-a\"]}]}";

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private readonly TestContext _testContext;

    public WorkModeOrchestratorTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    public async Task RunAsync_SingleStep_RunsManagerThenChildAndEmitsLifecycleEvents()
    {
        var result = await RunAsync(repetitionCount: 1);
        var events = Assert.ContainsSingle(result.EventBatches);

        Assert.AreEqual(MessageTypes.RunAccepted, events[0].MessageType);
        Assert.AreEqual(MessageTypes.RunCompleted, events[^1].MessageType);
        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        CollectionAssert.AreEqual(ExpectedAgentIds, result.InvokedAgentIds);
        Assert.HasCount(2, result.CapturedRequests);
        Assert.HasCount(2, result.InvocationSnapshots);
        var managerMessages = result.CapturedRequests[0].Messages;
        var managerInput = string.Join(
            "\n",
            managerMessages.Skip(1).Select(static message =>
                ((TextContentBlock)message.Content[0]).Text));
        StringAssert.Contains(managerInput, "Prepare the release notes.");
        StringAssert.Contains(managerInput, "agents:");
        StringAssert.Contains(managerInput, "worker");
        StringAssert.Contains(managerInput, "maxSteps:");
        Assert.IsFalse(managerInput.Contains("Create a structured WorkPlanDraft", StringComparison.Ordinal));
        Assert.IsFalse(managerInput.Contains("WorkflowPolicy:", StringComparison.Ordinal));
        Assert.AreEqual(1, CountOccurrences(managerInput, "worker"));
        foreach (var providerRequest in result.CapturedRequests)
        {
            var instructions = ((TextContentBlock)providerRequest.Messages[0].Content[0]).Text;
            StringAssert.Contains(instructions, "Shared global rule.");
            StringAssert.Contains(instructions, "Shared project rule.");
            Assert.IsFalse(instructions.Contains("Agent Delegation", StringComparison.Ordinal));
            Assert.IsLessThan(
                instructions.IndexOf("Shared project rule.", StringComparison.Ordinal),
                instructions.IndexOf("Shared global rule.", StringComparison.Ordinal));
        }

        var startedAgentIds = events
            .Where(static item => item.MessageType == MessageTypes.InvocationStarted)
            .Select(static item => item.Payload.Deserialize(
                RuntimeJsonContext.Default.InvocationStartedEvent)
                ?? throw new InvalidDataException("The invocation.started payload was empty."))
            .ToArray();
        CollectionAssert.AreEqual(
            ExpectedAgentIds,
            startedAgentIds.Select(static item => item.Snapshot.AgentId).ToArray());
        Assert.IsNull(startedAgentIds[0].Snapshot.ParentInvocationId);
        Assert.AreEqual(
            startedAgentIds[0].InvocationId,
            startedAgentIds[1].Snapshot.ParentInvocationId);
        Assert.AreEqual("run-1:step-1", startedAgentIds[1].Snapshot.WorkStepId);
        Assert.AreEqual("1", startedAgentIds[1].Snapshot.WorkPlanVersion);
        foreach (var started in startedAgentIds)
        {
            Assert.IsTrue(result.InvocationSnapshots.TryGetValue(started.InvocationId, out var snapshot));
            Assert.AreEqual(
                started.Snapshot with { ContextProjection = null },
                snapshot with { ContextProjection = null });
            if (started.Snapshot.ContextProjection is { } expectedProjection)
            {
                var actualProjection = snapshot.ContextProjection;
                Assert.IsNotNull(actualProjection);
                Assert.AreEqual(
                    expectedProjection with { RetainedItems = [] },
                    actualProjection with { RetainedItems = [] });
                CollectionAssert.AreEqual(
                    expectedProjection.RetainedItems,
                    actualProjection.RetainedItems);
            }
        }

        Assert.HasCount(3, result.CanonicalHistory);
        Assert.AreEqual("user", result.CanonicalHistory[0].Role);
        var canonical = result.CanonicalHistory
            .Where(static message => message.SummaryMetadata is not null)
            .ToArray();
        CollectionAssert.AreEqual(
            ExpectedCanonicalKinds,
            canonical
                .Select(static message => message.SummaryMetadata?.GetProperty("kind").GetString())
                .ToArray());
        var step = Assert.ContainsSingle(result.WorkSteps);
        Assert.AreEqual(canonical[0].MessageId, result.PlanRevisions[0].PlanMessageId);
        Assert.AreEqual(canonical[1].MessageId, step.StepMessageId);
        Assert.IsNotNull(step.CheckpointJson);
        StringAssert.Contains(step.CheckpointJson, startedAgentIds[1].InvocationId);
    }

    [TestMethod]
    public async Task RunAsync_CatalogRefreshBetweenManagerAndChild_CapturesPerInvocationVersions()
    {
        var result = await RunAsync(repetitionCount: 1, refreshSkillsBetweenInvocations: true);

        Assert.HasCount(2, result.CapturedRequests);
        Assert.IsTrue(result.CapturedRequests.All(static item => item.Skills is not null));
        Assert.AreNotEqual(
            result.CapturedRequests[0].Skills!.CatalogVersion,
            result.CapturedRequests[1].Skills!.CatalogVersion);
        foreach (var request in result.CapturedRequests)
        {
            Assert.IsTrue(result.InvocationSnapshots.TryGetValue(request.InvocationId, out var snapshot));
            Assert.AreEqual(request.Skills!.CatalogVersion, snapshot.SkillCatalogVersion);
            CollectionAssert.AreEqual(request.Skills.SkillNames, snapshot.SkillNames);
        }
    }

    [TestMethod]
    public async Task RunAsync_CompletedStepOnSecondCall_ReusesPersistedResult()
    {
        var result = await RunAsync(repetitionCount: 2);

        Assert.HasCount(2, result.EventBatches);
        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        CollectionAssert.AreEqual(ExpectedAgentIds, result.InvokedAgentIds);
        Assert.IsNotNull(result.CompletedStepResultJson);
        StringAssert.Contains(result.CompletedStepResultJson, "result:worker");

        var replayedDeltas = result.EventBatches[1]
            .Where(static item => item.MessageType == MessageTypes.TextDelta)
            .Select(static item => item.Payload.Deserialize(RuntimeJsonContext.Default.TextDeltaEvent)
                ?? throw new InvalidDataException("The output.delta payload was empty."))
            .Select(static item => item.Delta)
            .ToArray();
        CollectionAssert.AreEqual(ExpectedReplayDeltas, replayedDeltas);
    }

    [TestMethod]
    public async Task RunAsync_WorkerFailureWithContinuePolicy_DoesNotMarkStepCompleted()
    {
        var result = await RunAsync(
            repetitionCount: 1,
            failurePolicy: WorkStepFailurePolicy.Continue,
            failWorker: true);
        var events = Assert.ContainsSingle(result.EventBatches);
        var step = Assert.ContainsSingle(result.WorkSteps);

        Assert.AreEqual(MessageTypes.RunCompleted, events[^1].MessageType);
        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        Assert.AreEqual(WorkStepLifecycleStatus.Failed, step.Status);
        Assert.IsNull(result.CompletedStepResultJson);
        Assert.AreEqual(1, events.Count(static item => item.MessageType == MessageTypes.InvocationFailed));
    }

    [TestMethod]
    public async Task RunAsync_WorkerFailureWithRetryPolicy_RetriesWithNewInvocationAndCompletesStep()
    {
        var result = await RunAsync(
            repetitionCount: 1,
            failurePolicy: WorkStepFailurePolicy.Retry,
            workerFailuresBeforeSuccess: 1,
            maxRetriesPerStep: 1,
            retryBackoffMilliseconds: 0);
        var events = Assert.ContainsSingle(result.EventBatches);
        var step = Assert.ContainsSingle(result.WorkSteps);
        var workerRequests = result.CapturedRequests
            .Where(static item => item.AgentId == "worker")
            .ToArray();

        Assert.AreEqual(MessageTypes.RunCompleted, events[^1].MessageType);
        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        Assert.AreEqual(WorkStepLifecycleStatus.Completed, step.Status);
        Assert.AreEqual(2, step.AttemptCount);
        Assert.HasCount(2, workerRequests);
        Assert.AreNotEqual(workerRequests[0].InvocationId, workerRequests[1].InvocationId);
        Assert.IsNotNull(result.CompletedStepResultJson);
        StringAssert.Contains(result.CompletedStepResultJson, "result:worker");
        CollectionAssert.AreEqual(ExpectedRetryAgentIds, result.InvokedAgentIds);
        Assert.AreEqual(1, events.Count(static item => item.MessageType == MessageTypes.InvocationFailed));
    }

    [TestMethod]
    public async Task RunAsync_WorkerAuthenticationFailure_WaitsAndRetriesExactlyOnce()
    {
        var result = await RunAsync(
            repetitionCount: 1,
            requireWorkerCredentialRefresh: true);
        var events = Assert.ContainsSingle(result.EventBatches);
        var statusChanges = events
            .Where(static item => item.MessageType == MessageTypes.RunStatusChanged)
            .Select(static item => item.Payload.Deserialize(
                RuntimeJsonContext.Default.RunStatusChangedEvent)
                ?? throw new InvalidDataException("The run.statusChanged payload was empty."))
            .ToArray();
        var workerRequests = result.CapturedRequests
            .Where(static request => string.Equals(request.AgentId, "worker", StringComparison.Ordinal))
            .ToArray();
        var refreshRequest = Assert.ContainsSingle(result.CredentialRefreshRequests);
        var waiting = Assert.ContainsSingle(result.CredentialWaitStates);

        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        CollectionAssert.AreEqual(ExpectedCredentialRetryAgentIds, result.InvokedAgentIds);
        CollectionAssert.AreEqual(
            new[]
            {
                RunStatus.WaitingForCredentials.ToString(),
                RunStatus.Running.ToString()
            },
            statusChanges.Select(static status => status.Status).ToArray());
        Assert.HasCount(2, workerRequests);
        Assert.AreEqual(0, workerRequests[0].AttemptNumber);
        Assert.AreEqual(1, workerRequests[1].AttemptNumber);
        Assert.AreEqual(workerRequests[0].InvocationId, workerRequests[1].InvocationId);
        Assert.AreNotEqual(workerRequests[0].InternalRequestId, workerRequests[1].InternalRequestId);
        Assert.AreEqual("run-1", refreshRequest.RunId);
        Assert.AreEqual("fake", refreshRequest.ProviderId);
        Assert.AreEqual(WorkSessionStatus.WaitingForCredentials, waiting.Status);
        Assert.IsNotNull(waiting.CurrentStep);
        Assert.AreEqual(WorkStepLifecycleStatus.WaitingForCredentials, waiting.CurrentStep.Status);
        Assert.AreEqual(WorkStepLifecycleStatus.Completed, Assert.ContainsSingle(result.WorkSteps).Status);
    }

    [TestMethod]
    public async Task RunAsync_CredentialRefreshTimeoutWithContinuePolicy_ResumesRunAndFailsStep()
    {
        var result = await RunAsync(
            repetitionCount: 1,
            failurePolicy: WorkStepFailurePolicy.Continue,
            requireWorkerCredentialRefresh: true,
            credentialRefreshSucceeds: false);
        var events = Assert.ContainsSingle(result.EventBatches);
        var statusChanges = events
            .Where(static item => item.MessageType == MessageTypes.RunStatusChanged)
            .Select(static item => item.Payload.Deserialize(
                RuntimeJsonContext.Default.RunStatusChangedEvent)
                ?? throw new InvalidDataException("The run.statusChanged payload was empty."))
            .ToArray();

        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        Assert.AreEqual(WorkStepLifecycleStatus.Failed, Assert.ContainsSingle(result.WorkSteps).Status);
        Assert.HasCount(1, result.CredentialRefreshRequests);
        CollectionAssert.AreEqual(
            new[]
            {
                RunStatus.WaitingForCredentials.ToString(),
                RunStatus.Running.ToString()
            },
            statusChanges.Select(static status => status.Status).ToArray());
        Assert.AreEqual(1, events.Count(static item => item.MessageType == MessageTypes.InvocationFailed));
    }

    [TestMethod]
    public async Task RunAsync_WorkerFailureWithAskHost_PersistsWaitAndRetriesAfterApproval()
    {
        var result = await RunAsync(
            repetitionCount: 1,
            failurePolicy: WorkStepFailurePolicy.AskHost,
            workerFailuresBeforeSuccess: 1,
            workflowApprovalDecision: ToolAuthorizationDecision.Granted);
        var step = Assert.ContainsSingle(result.WorkSteps);
        var approval = Assert.ContainsSingle(result.WorkflowApprovalRequests);

        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        Assert.AreEqual(WorkStepLifecycleStatus.Completed, step.Status);
        Assert.AreEqual(2, step.AttemptCount);
        Assert.AreEqual("run-1:step-1", approval.WorkStepId);
        Assert.AreEqual("1", approval.PlanVersion);
        Assert.AreEqual("manager", approval.ParentAgentId);
        StringAssert.StartsWith(approval.ApprovalRequestId, "work-approval-");
        Assert.IsNull(step.ErrorMessage);
    }

    [TestMethod]
    public async Task RunAsync_StrictLoopDetection_PausesRepeatedGoalUntilHostApproves()
    {
        var result = await RunAsync(
            repetitionCount: 1,
            workflowApprovalDecision: ToolAuthorizationDecision.Granted,
            managerOutput: DuplicateGoalPlanJson,
            loopDetection: WorkLoopDetection.Strict,
            requireHumanConfirmationOnLoop: true);

        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        Assert.HasCount(2, result.WorkSteps);
        Assert.IsTrue(result.WorkSteps.All(static step =>
            step.Status is WorkStepLifecycleStatus.Completed));
        var approval = Assert.ContainsSingle(result.WorkflowApprovalRequests);
        Assert.AreEqual("step-b", approval.WorkStepId);
        StringAssert.Contains(approval.Reason, "repeats the same target");
    }

    [TestMethod]
    public async Task RunAsync_PendingStepToolIntent_BlocksStepCompletion()
    {
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await RunAsync(
                repetitionCount: 1,
                createPendingWorkStepToolIntent: true));

        StringAssert.Contains(
            ex.Message,
            "cannot be completed because 1 tool intent(s) are not terminal");
        StringAssert.Contains(ex.Message, "call-pending-step-1:Pending");
    }

    [TestMethod]
    public async Task RunAsync_LongPlanProjection_PersistsDecisionAndEmitsAdjustment()
    {
        var managerOutput = JsonSerializer.Serialize(
            new WorkPlanDraft(
                "1",
                new string('g', 5_000),
                [new WorkPlanStepDraft(
                    "step-long",
                    new string('s', 8_000),
                    "worker")]),
            RuntimeJsonContext.Default.WorkPlanDraft);
        var result = await RunAsync(
            repetitionCount: 1,
            managerOutput: managerOutput,
            contextPolicy: WorkContextPolicy.Default with { MaxTokens = 700 });
        var events = Assert.ContainsSingle(result.EventBatches);
        var adjustmentEnvelope = Assert.ContainsSingle(events.Where(static envelope =>
            envelope.MessageType == MessageTypes.ContextProjectionAdjusted));
        var adjustment = adjustmentEnvelope.Payload.Deserialize(
            RuntimeJsonContext.Default.ContextProjectionAdjustedEvent)
            ?? throw new InvalidDataException("The projection adjustment payload was empty.");
        var workerRequest = Assert.ContainsSingle(result.CapturedRequests.Where(static item =>
            item.AgentId == "worker"));
        var projection = workerRequest.Messages.Length > 0
            ? result.InvocationSnapshots[workerRequest.InvocationId].ContextProjection
            : null;

        Assert.IsNotNull(projection);
        Assert.IsLessThanOrEqualTo(projection.TokenLimit, projection.EstimatedTokens);
        Assert.AreEqual(projection.Strategy, adjustment.Strategy);
        Assert.AreEqual(projection.EstimatedTokens, adjustment.EstimatedTokens);
        Assert.AreEqual(projection.SummarizedUnitCount, adjustment.SummarizedUnitCount);
        CollectionAssert.AreEqual(projection.RetainedItems, adjustment.RetainedItems);
        CollectionAssert.Contains(projection.RetainedItems, "plan:1");
        CollectionAssert.Contains(projection.RetainedItems, "step:step-long");
    }

    [TestMethod]
    public async Task WorkPlanUpdated_EmitsCorrectEnvelope()
    {
        var result = await RunAsync(repetitionCount: 1);
        var events = Assert.ContainsSingle(result.EventBatches);

        var planEvent = events.FirstOrDefault(e => e.MessageType == MessageTypes.WorkPlanUpdated);
        Assert.IsNotNull(planEvent, "work.plan.updated event should be emitted");

        // 验证信封七字段
        Assert.IsGreaterThan(0L, planEvent.Gsn, "GSN should be positive");
        Assert.AreEqual("run-1", planEvent.RunId);
        Assert.IsGreaterThan(0L, planEvent.RunSequence, "RunSequence should be positive");
        Assert.IsNotNull(planEvent.RuntimeInstanceId);
        Assert.AreEqual(MessageTypes.WorkPlanUpdated, planEvent.MessageType);
        Assert.IsGreaterThan(DateTimeOffset.MinValue, planEvent.Timestamp, "Timestamp should be set");

        // 验证 payload 结构
        var payload = planEvent.Payload;
        Assert.IsTrue(payload.TryGetProperty("workPlanVersion", out var version));
        Assert.AreEqual(JsonValueKind.Number, version.ValueKind);
        Assert.IsTrue(payload.TryGetProperty("goal", out var goal));
        Assert.AreEqual(JsonValueKind.String, goal.ValueKind);
        Assert.IsTrue(payload.TryGetProperty("steps", out var steps));
        Assert.IsGreaterThan(0, steps.GetArrayLength(), "Steps array should not be empty");

        // 验证步骤字段
        var firstStep = steps[0];
        Assert.IsTrue(firstStep.TryGetProperty("stepId", out _));
        Assert.IsTrue(firstStep.TryGetProperty("title", out _));
        Assert.IsTrue(firstStep.TryGetProperty("goal", out _));
        Assert.IsTrue(firstStep.TryGetProperty("targetAgentId", out _));
        // parentStepId 为 null 时会被省略（JsonIgnoreCondition.WhenWritingNull）
        Assert.IsTrue(firstStep.TryGetProperty("depth", out _));
        Assert.IsTrue(firstStep.TryGetProperty("dependsOn", out _));
        Assert.IsTrue(firstStep.TryGetProperty("status", out _));
    }

    [TestMethod]
    public async Task WorkPlanUpdated_WritesToOutbox()
    {
        var result = await RunAsync(repetitionCount: 1);
        var events = Assert.ContainsSingle(result.EventBatches);

        var planEvent = events.FirstOrDefault(e => e.MessageType == MessageTypes.WorkPlanUpdated);
        Assert.IsNotNull(planEvent);

        // 验证 GSN 递增（work.plan.updated 应该在 invocation.completed 之后）
        var completedEvent = events.Last(e => e.MessageType == MessageTypes.InvocationCompleted
            && e.RunSequence < planEvent.RunSequence);
        Assert.IsGreaterThan(completedEvent.Gsn, planEvent.Gsn, "work.plan.updated GSN should be after manager invocation.completed");
    }

    [TestMethod]
    public async Task WorkPlanUpdated_IncludesAllStepFields()
    {
        var result = await RunAsync(repetitionCount: 1);
        var events = Assert.ContainsSingle(result.EventBatches);

        var planEvent = events.FirstOrDefault(e => e.MessageType == MessageTypes.WorkPlanUpdated);
        Assert.IsNotNull(planEvent);

        var payload = planEvent.Payload;
        Assert.IsTrue(payload.TryGetProperty("steps", out var steps));
        var firstStep = steps[0];

        // 验证 Title（任务书要求 Title 为空时用 Goal）
        Assert.IsTrue(firstStep.TryGetProperty("title", out var title));
        Assert.IsNotNull(title.GetString());

        // 验证 Depth（parentStepId 为 null 时会被 JSON 序列化省略）
        Assert.IsTrue(firstStep.TryGetProperty("depth", out var depth));
        Assert.AreEqual(JsonValueKind.Number, depth.ValueKind);

        // 验证 Status 是 Pending
        Assert.IsTrue(firstStep.TryGetProperty("status", out var status));
        Assert.AreEqual("Pending", status.GetString());
    }

    [TestMethod]
    public async Task DiscussionOnlyManagerOutput_DoesNotPublishOrExecutePlan()
    {
        var result = await RunAsync(repetitionCount: 1, managerOutput: "我们先讨论范围，还没有可执行计划。");
        var events = Assert.ContainsSingle(result.EventBatches);

        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        Assert.IsEmpty(
            events.Where(static item => item.MessageType == MessageTypes.WorkPlanUpdated).ToArray(),
            "Discussion turns must not emit work.plan.updated");
        Assert.IsEmpty(result.WorkSteps);
        Assert.IsEmpty(result.PlanRevisions);
        CollectionAssert.AreEqual(ExpectedManagerOnlyAgentIds, result.InvokedAgentIds);
        Assert.AreEqual(MessageTypes.RunCompleted, events[^1].MessageType);
    }

    [TestMethod]
    public async Task InvalidStructuredPlan_DoesNotFallbackToExecutableStep()
    {
        const string invalidPlanJson =
            "{\"planVersion\":\"1\",\"goal\":\"Ship\",\"steps\":["
            + "{\"stepId\":\"step-a\",\"goal\":\"Do work\",\"targetAgentId\":\"missing-agent\"}]}";

        var result = await RunAsync(repetitionCount: 1, managerOutput: invalidPlanJson);
        var events = Assert.ContainsSingle(result.EventBatches);

        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        Assert.IsEmpty(
            events.Where(static item => item.MessageType == MessageTypes.WorkPlanUpdated).ToArray());
        Assert.IsEmpty(result.WorkSteps);
        CollectionAssert.AreEqual(ExpectedManagerOnlyAgentIds, result.InvokedAgentIds);
    }

    [TestMethod]
    public async Task SecondTurnRepublishingVersionOne_AutoHealsToNextRevision()
    {
        const string firstPlanJson =
            "{\"planVersion\":\"1\",\"goal\":\"Ship v1\",\"steps\":["
            + "{\"stepId\":\"step-a\",\"goal\":\"Prepare notes\",\"targetAgentId\":\"worker\"}]}";
        const string secondPlanJson =
            "{\"planVersion\":\"1\",\"goal\":\"Ship v2\",\"steps\":["
            + "{\"stepId\":\"step-b\",\"goal\":\"Rewrite notes\",\"targetAgentId\":\"worker\"}]}";

        var result = await RunMultiTurnAsync(
            [
                new WorkTurnScript("run-1", firstPlanJson, "Prepare the release notes."),
                new WorkTurnScript("run-2", secondPlanJson, "Revise the release notes.")
            ]);

        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        Assert.HasCount(2, result.PlanRevisions);
        Assert.AreEqual("1", result.PlanRevisions[0].PlanVersion);
        Assert.AreEqual("2", result.PlanRevisions[1].PlanVersion);
        Assert.AreEqual("1", result.PlanRevisions[1].PreviousPlanVersion);

        var secondBatch = result.EventBatches[1];
        var planEvent = secondBatch.First(static item => item.MessageType == MessageTypes.WorkPlanUpdated);
        Assert.IsTrue(planEvent.Payload.TryGetProperty("workPlanVersion", out var version));
        Assert.AreEqual(2, version.GetInt32());
        Assert.IsTrue(planEvent.Payload.TryGetProperty("goal", out var goal));
        Assert.AreEqual("Ship v2", goal.GetString());
    }

    [TestMethod]
    public async Task RequirePlanConfirmation_DiscussionPlan_IsNotPublished()
    {
        const string planJson =
            "{\"planVersion\":\"1\",\"goal\":\"Ship\",\"steps\":["
            + "{\"stepId\":\"step-a\",\"goal\":\"Prepare notes\",\"targetAgentId\":\"worker\"}]}";

        var result = await RunMultiTurnAsync(
            [new WorkTurnScript("run-1", planJson, "请先制定计划，先不要执行。")],
            requirePlanConfirmation: true);

        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        Assert.AreEqual(WorkSessionStatus.Planning, result.WorkSessionStatus);
        Assert.IsEmpty(result.PlanRevisions);
        Assert.IsEmpty(result.WorkSteps);
        CollectionAssert.AreEqual(ExpectedManagerOnlyAgentIds, result.InvokedAgentIds);
        Assert.IsFalse(
            result.EventBatches[0].Any(static item => item.MessageType == MessageTypes.WorkPlanUpdated));
    }

    [TestMethod]
    public async Task RequirePlanConfirmation_ApprovalTurn_PublishesAndExecutes()
    {
        const string planJson =
            "{\"planVersion\":\"1\",\"goal\":\"Ship\",\"steps\":["
            + "{\"stepId\":\"step-a\",\"goal\":\"Prepare notes\",\"targetAgentId\":\"worker\"}]}";

        var result = await RunMultiTurnAsync(
            [
                new WorkTurnScript("run-1", planJson, "请先制定计划，先不要执行。"),
                new WorkTurnScript("run-2", planJson, "确认执行")
            ],
            requirePlanConfirmation: true);

        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        Assert.AreEqual(WorkSessionStatus.Completed, result.WorkSessionStatus);
        Assert.HasCount(1, result.PlanRevisions);
        Assert.HasCount(1, result.WorkSteps);
        Assert.AreEqual(WorkStepLifecycleStatus.Completed, result.WorkSteps[0].Status);
        CollectionAssert.AreEqual(ExpectedDiscussionThenExecutionAgentIds, result.InvokedAgentIds);
        Assert.IsTrue(
            result.EventBatches[1].Any(static item => item.MessageType == MessageTypes.WorkPlanUpdated));
    }

    [TestMethod]
    public async Task RequirePlanConfirmation_StartPhrase_PublishesAndExecutes()
    {
        const string planJson =
            "{\"planVersion\":\"1\",\"goal\":\"Ship\",\"steps\":["
            + "{\"stepId\":\"step-a\",\"goal\":\"Prepare notes\",\"targetAgentId\":\"worker\"}]}";

        var result = await RunMultiTurnAsync(
            [new WorkTurnScript("run-1", planJson, "开始执行")],
            requirePlanConfirmation: true);

        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        Assert.AreEqual(WorkSessionStatus.Completed, result.WorkSessionStatus);
        Assert.AreEqual(WorkStepLifecycleStatus.Completed, result.WorkSteps[0].Status);
        CollectionAssert.AreEqual(ExpectedManagerThenWorkerAgentIds, result.InvokedAgentIds);
    }

    [TestMethod]
    public async Task RequirePlanConfirmation_FurtherDiscussion_StillDoesNotPublish()
    {
        const string planJson =
            "{\"planVersion\":\"1\",\"goal\":\"Ship\",\"steps\":["
            + "{\"stepId\":\"step-a\",\"goal\":\"Prepare notes\",\"targetAgentId\":\"worker\"}]}";

        var result = await RunMultiTurnAsync(
            [
                new WorkTurnScript("run-1", planJson, "请先制定计划，先不要执行。"),
                new WorkTurnScript("run-2", "还想再讨论一下范围，先不要执行。", "还想再讨论一下范围，先不要执行。")
            ],
            requirePlanConfirmation: true);

        Assert.AreEqual(WorkSessionStatus.Planning, result.WorkSessionStatus);
        Assert.IsEmpty(result.PlanRevisions);
        Assert.IsEmpty(result.WorkSteps);
        CollectionAssert.AreEqual(ExpectedTwoManagerAgentIds, result.InvokedAgentIds);
    }

    [TestMethod]
    public async Task RunAsync_IndependentReadySteps_ExecuteConcurrentWaveWithoutTools()
    {
        const string planJson =
            "{\"planVersion\":\"1\",\"goal\":\"Ship\",\"steps\":["
            + "{\"stepId\":\"step-a\",\"goal\":\"Prepare A\",\"targetAgentId\":\"worker\"},"
            + "{\"stepId\":\"step-b\",\"goal\":\"Prepare B\",\"targetAgentId\":\"worker\"}]}";

        var result = await RunAsync(
            repetitionCount: 1,
            managerOutput: planJson,
            maxConcurrentSteps: 2,
            workerDelay: TimeSpan.FromMilliseconds(80));

        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        Assert.HasCount(2, result.WorkSteps);
        Assert.IsTrue(result.WorkSteps.All(static step =>
            step.Status == WorkStepLifecycleStatus.Completed));
        Assert.IsNotNull(result.WorkerStartTimestampsUtc);
        Assert.HasCount(2, result.WorkerStartTimestampsUtc);
        var ordered = result.WorkerStartTimestampsUtc.OrderBy(static value => value).ToArray();
        var overlap = ordered[1] - ordered[0];
        Assert.IsTrue(
            overlap < TimeSpan.FromMilliseconds(80),
            $"Independent ready steps should start concurrently; start delta was {overlap.TotalMilliseconds}ms.");
    }

    [TestMethod]
    public async Task RunAsync_ContinuePolicy_MarksDependencyBlockedStepsSkipped()
    {
        const string planJson =
            "{\"planVersion\":\"1\",\"goal\":\"Ship\",\"steps\":["
            + "{\"stepId\":\"step-a\",\"goal\":\"Primary\",\"targetAgentId\":\"worker\"},"
            + "{\"stepId\":\"step-b\",\"goal\":\"Depends\",\"targetAgentId\":\"worker\",\"dependsOn\":[\"step-a\"]}]}";

        var result = await RunAsync(
            repetitionCount: 1,
            failurePolicy: WorkStepFailurePolicy.Continue,
            failWorker: true,
            managerOutput: planJson);

        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        Assert.HasCount(2, result.WorkSteps);
        var primary = result.WorkSteps.Single(static step => step.StepId == "step-a");
        var dependent = result.WorkSteps.Single(static step => step.StepId == "step-b");
        Assert.AreEqual(WorkStepLifecycleStatus.Failed, primary.Status);
        Assert.AreEqual(WorkStepLifecycleStatus.Skipped, dependent.Status);
        Assert.IsTrue(
            result.EventBatches[0].Any(static item => item.MessageType == MessageTypes.WorkStepSkipped));
    }

    [TestMethod]
    public async Task Work_ForwardsReasoningDelta()
    {
        var result = await RunAsync(repetitionCount: 1, emitReasoningDelta: true);
        var events = Assert.ContainsSingle(result.EventBatches);

        var reasoningEvents = events
            .Where(static item => item.MessageType == MessageTypes.ReasoningDelta)
            .ToArray();

        Assert.IsNotEmpty(reasoningEvents, "Should forward reasoning.delta events");
        var firstReasoning = reasoningEvents[0];
        Assert.IsTrue(firstReasoning.Payload.TryGetProperty("delta", out var delta));
        Assert.AreEqual("thinking...", delta.GetString());
        Assert.IsTrue(firstReasoning.Payload.TryGetProperty("invocationId", out var invocationId));
        Assert.IsFalse(string.IsNullOrEmpty(invocationId.GetString()));
        Assert.IsTrue(
            events.Any(static item => item.MessageType == MessageTypes.ReasoningCompleted),
            "思考增量之后必须发出 reasoning.completed。");
    }

    [TestMethod]
    public async Task InvalidPlan_KeepsPlanningAndHidesValidatorTextFromAssistant()
    {
        const string planJson =
            "{\"planVersion\":\"1\",\"goal\":\"Ship\",\"steps\":["
            + "{\"goal\":\"Do the work\",\"targetAgentId\":\"worker\"}]}";
        var result = await RunMultiTurnAsync(
            [new WorkTurnScript("run-1", planJson, "请先制定计划，先不要执行。")]);

        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        Assert.AreEqual(WorkSessionStatus.Planning, result.WorkSessionStatus);
        Assert.IsEmpty(result.PlanRevisions);
        Assert.IsFalse(result.InvokedAgentIds.Contains("worker"));
        Assert.IsTrue(result.CanonicalHistory.Any(static record =>
            record.Role == "user" && record.Content.GetRawText().Contains("请先制定计划", StringComparison.Ordinal)));
        Assert.IsFalse(result.CanonicalHistory.Any(static record =>
            record.Role == "assistant"
            && record.Content.GetRawText().Contains("Plan was not published", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task RequirePlanConfirmation_ApprovalAndPlan_ExecutesInThatTurn()
    {
        const string planJson =
            "{\"planVersion\":\"1\",\"goal\":\"Ship\",\"steps\":["
            + "{\"stepId\":\"step-a\",\"goal\":\"Prepare notes\",\"targetAgentId\":\"worker\"}]}";
        var result = await RunMultiTurnAsync(
            [new WorkTurnScript("run-1", planJson, "确认执行")],
            requirePlanConfirmation: true);

        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        Assert.AreEqual(WorkSessionStatus.Completed, result.WorkSessionStatus);
        Assert.HasCount(1, result.PlanRevisions);
        Assert.AreEqual(WorkStepLifecycleStatus.Completed, result.WorkSteps[0].Status);
        CollectionAssert.AreEqual(ExpectedManagerThenWorkerAgentIds, result.InvokedAgentIds);
    }

    [TestMethod]
    public async Task UnapprovedRevision_DoesNotPublish()
    {
        const string planJson =
            "{\"planVersion\":\"1\",\"goal\":\"Ship\",\"steps\":["
            + "{\"stepId\":\"step-a\",\"goal\":\"Prepare notes\",\"targetAgentId\":\"worker\"}]}";
        const string revisedJson =
            "{\"planVersion\":\"2\",\"previousPlanVersion\":\"1\",\"goal\":\"Ship v2\",\"steps\":["
            + "{\"stepId\":\"step-a\",\"goal\":\"Prepare notes again\",\"targetAgentId\":\"worker\"}]}";
        var result = await RunMultiTurnAsync(
            [
                new WorkTurnScript("run-1", planJson, "请先制定计划，先不要执行。"),
                new WorkTurnScript("run-2", revisedJson, "再看一下范围")
            ],
            requirePlanConfirmation: true);

        Assert.AreEqual(WorkSessionStatus.Planning, result.WorkSessionStatus);
        Assert.IsEmpty(result.PlanRevisions);
        Assert.IsFalse(result.InvokedAgentIds.Contains("worker"));
    }

    [TestMethod]
    public async Task Plan_StepThatCitesPriorStepWithoutDependsOn_IsRejected()
    {
        const string planJson =
            "{\"planVersion\":\"1\",\"goal\":\"Ship\",\"steps\":["
            + "{\"stepId\":\"step-1\",\"goal\":\"Write the file\",\"targetAgentId\":\"worker\"},"
            + "{\"stepId\":\"step-2\",\"goal\":\"验收步骤 1 的结果\",\"targetAgentId\":\"worker\"}]}";
        var result = await RunMultiTurnAsync(
            [new WorkTurnScript("run-1", planJson, "请先制定计划，先不要执行。")]);

        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        Assert.IsEmpty(result.PlanRevisions);
        Assert.IsFalse(result.InvokedAgentIds.Contains("worker"));
        Assert.IsTrue(result.CanonicalHistory.Any(static record =>
            record.Role == "tool"
            && record.Content.GetRawText().Contains("does not depend on it", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Work_ForwardsToolCallDelta()
    {
        var result = await RunAsync(repetitionCount: 1, emitToolCallDelta: true);
        var events = Assert.ContainsSingle(result.EventBatches);

        var toolCallEvents = events
            .Where(static item => item.MessageType == MessageTypes.ToolCallDelta)
            .ToArray();

        Assert.IsNotEmpty(toolCallEvents, "Should forward tool.call.delta events");
        var firstToolCall = toolCallEvents[0];
        Assert.IsTrue(firstToolCall.Payload.TryGetProperty("delta", out var delta));
        Assert.AreEqual("{\"arg\":", delta.GetString());
        Assert.IsTrue(firstToolCall.Payload.TryGetProperty("invocationId", out var invocationId));
        Assert.IsFalse(string.IsNullOrEmpty(invocationId.GetString()));
        Assert.IsTrue(firstToolCall.Payload.TryGetProperty("callId", out var callId));
        Assert.AreEqual("call-1", callId.GetString());
    }

    [TestMethod]
    public async Task Work_ForwardsUsageUpdated()
    {
        var result = await RunAsync(repetitionCount: 1, emitUsageUpdated: true);
        var events = Assert.ContainsSingle(result.EventBatches);

        var usageEvents = events
            .Where(static item => item.MessageType == MessageTypes.UsageUpdated)
            .ToArray();

        Assert.IsNotEmpty(usageEvents, "Should forward usage.updated events");
        var firstUsage = usageEvents[0];
        Assert.IsTrue(firstUsage.Payload.TryGetProperty("inputTokens", out var inputTokens));
        Assert.AreEqual(100, inputTokens.GetInt32());
        Assert.IsTrue(firstUsage.Payload.TryGetProperty("outputTokens", out var outputTokens));
        Assert.AreEqual(50, outputTokens.GetInt32());
        Assert.IsTrue(firstUsage.Payload.TryGetProperty("invocationId", out var invocationId));
        Assert.IsFalse(string.IsNullOrEmpty(invocationId.GetString()));
    }

    [TestMethod]
    public async Task InvocationStarted_Work_IncludesSemanticFields()
    {
        var result = await RunAsync(repetitionCount: 1);
        var events = Assert.ContainsSingle(result.EventBatches);

        var stepInvocations = events
            .Where(static item => item.MessageType == MessageTypes.InvocationStarted)
            .Select(static item => item.Payload.Deserialize(
                RuntimeJsonContext.Default.InvocationStartedEvent)
                ?? throw new InvalidDataException("The invocation.started payload was empty."))
            .Where(static started => !string.IsNullOrEmpty(started.Snapshot.WorkStepId))
            .ToArray();

        Assert.IsNotEmpty(stepInvocations, "Should have at least one step invocation");
        var stepInvocation = stepInvocations[0];
        var snap = stepInvocation.Snapshot;

        Assert.IsNotNull(snap.Title, "Title should be populated for work step");
        Assert.AreEqual(0, snap.Depth, "Depth should be 0 for top-level step");
        Assert.AreEqual("worker", snap.DisplayName, "DisplayName should match agentId");
        Assert.IsNull(snap.Role, "Role should be null for work mode");
        Assert.IsNull(snap.ParticipantId, "ParticipantId should be null for work mode");
        Assert.IsNull(snap.RoundIndex, "RoundIndex should be null for work mode");
    }

    [TestMethod]
    public async Task InvocationStarted_GM_DoesNotIncludeStepFields()
    {
        var result = await RunAsync(repetitionCount: 1);
        var events = Assert.ContainsSingle(result.EventBatches);

        var gmInvocations = events
            .Where(static item => item.MessageType == MessageTypes.InvocationStarted)
            .Select(static item => item.Payload.Deserialize(
                RuntimeJsonContext.Default.InvocationStartedEvent)
                ?? throw new InvalidDataException("The invocation.started payload was empty."))
            .Where(static started => string.IsNullOrEmpty(started.Snapshot.WorkStepId))
            .ToArray();

        Assert.IsNotEmpty(gmInvocations, "Should have GM invocation");
        var gmInvocation = gmInvocations[0];
        var snap = gmInvocation.Snapshot;

        Assert.IsNull(snap.Title, "Title should be null for GM invocation");
        Assert.IsNull(snap.ParentStepId, "ParentStepId should be null for GM invocation");
        Assert.AreEqual(0, snap.Depth, "Depth should be 0 for GM invocation");
    }

    [TestMethod]
    public async Task InvocationStarted_PayloadStructure_SnapshotNotFlattened()
    {
        var result = await RunAsync(repetitionCount: 1);
        var events = Assert.ContainsSingle(result.EventBatches);

        var invocationStartedEvents = events
            .Where(static item => item.MessageType == MessageTypes.InvocationStarted)
            .ToArray();

        Assert.IsNotEmpty(invocationStartedEvents, "Should have invocation.started events");

        foreach (var envelope in invocationStartedEvents)
        {
            var payload = envelope.Payload;
            Assert.IsTrue(payload.TryGetProperty("invocationId", out _), "Payload should have invocationId at root");
            Assert.IsTrue(payload.TryGetProperty("snapshot", out var snapshotElement), "Payload should have snapshot object");
            Assert.AreEqual(JsonValueKind.Object, snapshotElement.ValueKind, "snapshot should be an object");

            Assert.IsTrue(snapshotElement.TryGetProperty("invocationId", out _), "snapshot should contain invocationId");
            Assert.IsTrue(snapshotElement.TryGetProperty("agentId", out _), "snapshot should contain agentId");

            Assert.IsFalse(payload.TryGetProperty("title", out _), "title should NOT be at payload root");
            Assert.IsFalse(payload.TryGetProperty("displayName", out _), "displayName should NOT be at payload root");
        }
    }

    [TestMethod]
    public async Task EphemeralSpecialist_UsesStepPrompt_NotSettings()
    {
        const string planJson =
            "{\"planVersion\":\"1\",\"goal\":\"Ship\",\"steps\":["
            + "{\"stepId\":\"step-a\",\"goal\":\"核对证书\",\"targetAgentId\":\"ephemeral:step-a\","
            + "\"ephemeralName\":\"证书专员\",\"ephemeralSystemPrompt\":\"只负责核对证书域名。\"}]}";

        var result = await RunAsync(repetitionCount: 1, managerOutput: planJson);

        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        var specialist = result.CapturedRequests.Single(request => request.AgentId == "ephemeral:step-a");
        var system = ((TextContentBlock)specialist.Messages[0].Content[0]).Text;
        StringAssert.Contains(system, "只负责核对证书域名。");
        Assert.AreEqual("证书专员", result.InvocationSnapshots[specialist.InvocationId].DisplayName);
    }

    private async Task<WorkRunResult> RunAsync(
        int repetitionCount,
        WorkStepFailurePolicy failurePolicy = WorkStepFailurePolicy.Stop,
        bool failWorker = false,
        int workerFailuresBeforeSuccess = 0,
        int? maxRetriesPerStep = null,
        int? retryBackoffMilliseconds = null,
        bool createPendingWorkStepToolIntent = false,
        ToolAuthorizationDecision? workflowApprovalDecision = null,
        string? managerOutput = null,
        WorkContextPolicy? contextPolicy = null,
        bool requireWorkerCredentialRefresh = false,
        bool credentialRefreshSucceeds = true,
        bool refreshSkillsBetweenInvocations = false,
        bool emitReasoningDelta = false,
        bool emitToolCallDelta = false,
        bool emitUsageUpdated = false,
        WorkLoopDetection? loopDetection = null,
        bool? requireHumanConfirmationOnLoop = null,
        int? maxConcurrentSteps = null,
        TimeSpan? workerDelay = null)
    {
        var ct = _testContext.CancellationToken;
        var dataDirectory = Path.Combine(
            _testContext.TestRunDirectory ?? Path.GetTempPath(),
            $"work-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        SkillCatalogService? skillCatalog = null;

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
            using var toolStateStore = new SqliteToolIntentRepository(connection);
            var provider = new FakeProviderAdapter(
                failWorker,
                workerFailuresBeforeSuccess,
                managerOutput,
                requireWorkerCredentialRefresh,
                emitReasoningDelta,
                emitToolCallDelta,
                emitUsageUpdated,
                workerDelay);
            var memoryFiles = new MemoryFileService(
                Path.Join(dataDirectory, "home"),
                Path.Join(dataDirectory, "workspace"));
            await memoryFiles.ReplaceAsync(
                MemoryScope.Global,
                "# Memory\n\n- Shared global rule.\n",
                ct);
            await memoryFiles.ReplaceAsync(
                MemoryScope.Project,
                "# Memory\n\n- Shared project rule.\n",
                ct);
            var sessionRepo = new SqliteSessionRepository(connection);
            var workRepo = new SqliteWorkRepository(connection);
            Func<CancellationToken, Task<SkillCatalogSnapshot?>>? captureSkills = null;
            if (refreshSkillsBetweenInvocations)
            {
                var skillsWorkspace = Path.Combine(dataDirectory, "skills-workspace");
                var skillDirectory = Path.Combine(
                    skillsWorkspace,
                    ".madorin",
                    "skills",
                    "work-skill");
                Directory.CreateDirectory(skillDirectory);
                var skillFile = Path.Combine(skillDirectory, "SKILL.md");
                await File.WriteAllTextAsync(
                    skillFile,
                    "---\nname: work-skill\ndescription: Work Skill\n---\nversion one\n",
                    ct);
                var catalog = new SkillCatalogService(
                    skillsWorkspace,
                    Path.Combine(dataDirectory, "skills-data"),
                    "work-runtime",
                    sourceScope: SkillCatalogSourceScope.Workspace);
                skillCatalog = catalog;
                await catalog.InitializeAsync(ct);
                var skillCaptureCount = 0;
                captureSkills = async token =>
                {
                    if (Interlocked.Increment(ref skillCaptureCount) == 2)
                    {
                        await File.WriteAllTextAsync(
                            skillFile,
                            "---\nname: work-skill\ndescription: Work Skill\n---\nversion two\n",
                            token);
                        await catalog.RefreshAsync(token);
                    }

                    return await catalog.GetSnapshotForInvocationAsync(token);
                };
            }

            var workflowApprovalRequests = new List<ApprovalRequest>();
            var credentialRefreshRequests = new List<CredentialsRefreshRequestedEvent>();
            var credentialWaitStates = new List<WorkResumeState>();
            var store = new ConversationStore(dataDirectory);
            var orchestrator = new WorkModeOrchestrator(
                _ => provider,
                store,
                sessionRepo,
                outbox,
                new AgentContextComposer(memoryFiles),
                toolStateStore: toolStateStore,
                workRepo: workRepo,
                connection: connection,
                workflowApprovalHandler: workflowApprovalDecision is null
                    ? null
                    : (approval, _) =>
                    {
                        workflowApprovalRequests.Add(approval);
                        return Task.FromResult(new ApprovalResponse(
                            approval.CorrelationId,
                            approval.ApprovalRequestId,
                            workflowApprovalDecision.Value));
                    },
                credentialRefreshHandler: requireWorkerCredentialRefresh
                    ? async (refresh, refreshCt) =>
                    {
                        credentialRefreshRequests.Add(refresh);
                        credentialWaitStates.Add(
                            await workRepo.GetResumeStateAsync("session-1", refreshCt)
                                ?? throw new InvalidDataException(
                                    "The Work credential-wait state was not persisted."));
                        if (credentialRefreshSucceeds)
                        {
                            await provider.UpdateCredentialsAsync(
                                new CredentialsUpdateParameters(
                                    refresh.RunId,
                                    refresh.ProviderId,
                                    "refreshed-secret"),
                                refreshCt);
                        }

                        return credentialRefreshSucceeds;
                    }
                    : null,
                skillCatalogSnapshotProvider: captureSkills);
            var request = CreateRequest(
                failurePolicy,
                maxRetriesPerStep,
                retryBackoffMilliseconds,
                contextPolicy,
                loopDetection,
                requireHumanConfirmationOnLoop,
                maxConcurrentSteps: maxConcurrentSteps);
            await EnsureSessionRunAsync(connection, "session-1", "run-1", ct);
            if (createPendingWorkStepToolIntent)
            {
                await CreatePendingWorkStepToolIntentAsync(toolStateStore, ct);
            }

            var eventBatches = new List<IReadOnlyList<RuntimeEventEnvelope>>();

            for (var index = 0; index < repetitionCount; index++)
            {
                var events = new List<RuntimeEventEnvelope>();
                await foreach (var envelope in orchestrator.RunAsync(
                    "run-1",
                    "session-1",
                    request,
                    "runtime-1",
                    ct))
                {
                    events.Add(envelope);
                }

                eventBatches.Add(events);
            }

            var invocationSnapshots = new Dictionary<string, InvocationSnapshot>(StringComparer.Ordinal);
            foreach (var envelope in eventBatches.SelectMany(static item => item)
                .Where(static item => item.MessageType == MessageTypes.InvocationStarted))
            {
                var started = envelope.Payload.Deserialize(
                    RuntimeJsonContext.Default.InvocationStartedEvent)
                    ?? throw new InvalidDataException("The invocation.started payload was empty.");
                var snapshotJson = await sessionRepo.GetInvocationSnapshotJsonAsync(
                    started.InvocationId,
                    ct).ConfigureAwait(false)
                    ?? throw new InvalidDataException(
                        $"Invocation snapshot '{started.InvocationId}' was not persisted.");
                var snapshot = JsonSerializer.Deserialize(
                    snapshotJson,
                    RuntimeJsonContext.Default.InvocationSnapshot)
                    ?? throw new InvalidDataException(
                        $"Invocation snapshot '{started.InvocationId}' was empty.");
                invocationSnapshots[started.InvocationId] = snapshot;
            }

            var completed = await new SqliteSessionRepository(connection)
                .GetCompletedWorkStepResultAsync("run-1:step-1", "1", ComputeHash(request.InitialInput), ct);
            var runStatus = await new SqliteSessionRepository(connection)
                .GetRunStatusAsync("run-1", ct);
            var workSteps = await new SqliteWorkRepository(connection)
                .ListStepsAsync("session-1", "1", ct);
            var planRevisions = await new SqliteWorkRepository(connection)
                .ListPlanRevisionsAsync("session-1", ct);
            var canonicalHistory = await store.ReadAllAsync("session-1", ct);
            return new WorkRunResult(
                provider.InvokedAgentIds.ToArray(),
                provider.CapturedRequests.ToArray(),
                eventBatches,
                completed,
                runStatus,
                workSteps,
                invocationSnapshots,
                canonicalHistory,
                planRevisions,
                workflowApprovalRequests,
                credentialRefreshRequests,
                credentialWaitStates,
                WorkSessionStatus: null,
                provider.WorkerStartTimestampsUtc.ToArray());
        }
        finally
        {
            if (skillCatalog is not null)
            {
                await skillCatalog.DisposeAsync();
            }

            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    private static async Task CreatePendingWorkStepToolIntentAsync(
        SqliteToolIntentRepository toolStateStore,
        CancellationToken ct)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var created = await toolStateStore.TryCreateIntentAsync(
            new ToolIntentState(
                "call-pending-step-1",
                "invocation-tool-1",
                "run-1",
                "session-1",
                "worker",
                ParentAgentId: "manager",
                "host.files.write",
                "tools-v1",
                "ARGS-HASH-1",
                ToolIntentStatus.Pending,
                GrantId: null,
                ApprovalRequestId: null,
                ResultJson: null,
                ResultHash: null,
                ResultBlob: null,
                ErrorCode: null,
                ErrorMessage: null,
                IsResultVisible: true,
                createdAt,
                SentAt: null,
                CompletedAt: null,
                WorkStepId: "run-1:step-1",
                PlanVersion: "1"),
            ct).ConfigureAwait(false);
        if (!created)
        {
            throw new InvalidOperationException("The pending tool intent test fixture was not created.");
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
    private static NewSessionRunRequest CreateRequest(
        WorkStepFailurePolicy failurePolicy = WorkStepFailurePolicy.Stop,
        int? maxRetriesPerStep = null,
        int? retryBackoffMilliseconds = null,
        WorkContextPolicy? contextPolicy = null,
        WorkLoopDetection? loopDetection = null,
        bool? requireHumanConfirmationOnLoop = null,
        bool requirePlanConfirmation = false,
        int? maxConcurrentSteps = null)
    {
        var manager = new AgentRef(
            "manager",
            "v1",
            "Create a one-step plan.",
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
            FailurePolicy = failurePolicy,
            RequirePlanConfirmation = requirePlanConfirmation
        };
        if (maxRetriesPerStep is not null || retryBackoffMilliseconds is not null)
        {
            policy = policy with
            {
                MaxRetriesPerStep = maxRetriesPerStep ?? policy.MaxRetriesPerStep,
                RetryBackoffMilliseconds = retryBackoffMilliseconds ?? policy.RetryBackoffMilliseconds
            };
        }

        if (maxConcurrentSteps is not null)
        {
            policy = policy with { MaxConcurrentSteps = maxConcurrentSteps.Value };
        }

        if (loopDetection is not null || requireHumanConfirmationOnLoop is not null)
        {
            policy = policy with
            {
                LoopDetection = loopDetection ?? policy.LoopDetection,
                RequireHumanConfirmationOnLoop =
                    requireHumanConfirmationOnLoop ?? policy.RequireHumanConfirmationOnLoop
            };
        }

        return new NewSessionRunRequest(
            "session-key",
            "run-key",
            RuntimeMode.Work,
            new NextTurnSelection(
                1,
                RuntimeMode.Work,
                new DefaultSelection("fake", "test-model"),
                new WorkModeOptions(
                    manager,
                    [worker],
                    policy,
                    contextPolicy),
                ToolCatalogVersion: "tools-v1"),
            [new TextContentBlock("Prepare the release notes.")]);
    }

    private sealed class FakeProviderAdapter :
        IRuntimeProviderAdapter,
        IRuntimeProviderCredentialUpdater
    {
        private readonly List<string> _invokedAgentIds = [];
        private readonly List<RuntimeProviderRequest> _capturedRequests = [];
        private readonly int _workerFailuresBeforeSuccess;
        private readonly string? _managerOutput;
        private readonly bool _requireWorkerCredentialRefresh;
        private readonly bool _emitReasoningDelta;
        private readonly bool _emitToolCallDelta;
        private readonly bool _emitUsageUpdated;
        private readonly TimeSpan? _workerDelay;
        private readonly List<DateTimeOffset> _workerStartTimestampsUtc = [];
        private int _workerFailureCount;

        public FakeProviderAdapter(
            bool failWorker = false,
            int workerFailuresBeforeSuccess = 0,
            string? managerOutput = null,
            bool requireWorkerCredentialRefresh = false,
            bool emitReasoningDelta = false,
            bool emitToolCallDelta = false,
            bool emitUsageUpdated = false,
            TimeSpan? workerDelay = null)
        {
            _workerFailuresBeforeSuccess = failWorker
                ? int.MaxValue
                : workerFailuresBeforeSuccess;
            _managerOutput = managerOutput;
            _requireWorkerCredentialRefresh = requireWorkerCredentialRefresh;
            _emitReasoningDelta = emitReasoningDelta;
            _emitToolCallDelta = emitToolCallDelta;
            _emitUsageUpdated = emitUsageUpdated;
            _workerDelay = workerDelay;
        }

        public IReadOnlyList<DateTimeOffset> WorkerStartTimestampsUtc => _workerStartTimestampsUtc;

        public string ProviderId => "fake";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } = [new("test-model", "Test Model")];

        public IReadOnlyList<string> InvokedAgentIds => _invokedAgentIds;

        public IReadOnlyList<RuntimeProviderRequest> CapturedRequests => _capturedRequests;

        public string? Credential { get; private set; }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _invokedAgentIds.Add(request.AgentId);
            _capturedRequests.Add(request);
            if (string.Equals(request.AgentId, "worker", StringComparison.Ordinal))
            {
                lock (_workerStartTimestampsUtc)
                {
                    _workerStartTimestampsUtc.Add(DateTimeOffset.UtcNow);
                }

                if (_workerDelay is { } delay && delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, ct);
                }
            }

            await Task.Yield();
            if (_requireWorkerCredentialRefresh
                && string.Equals(request.AgentId, "worker", StringComparison.Ordinal)
                && !string.Equals(Credential, "refreshed-secret", StringComparison.Ordinal))
            {
                yield return new InvocationFailedProviderEvent(
                    request.InvocationId,
                    new RuntimeError(
                        RuntimeErrorCodes.AuthenticationFailed,
                        "authentication",
                        "The fake credential was rejected.",
                        IsRetryable: false,
                        ProviderDetails: null,
                        DiagnosticId: "diagnostic-worker-401"));
                yield break;
            }

            if (string.Equals(request.AgentId, "worker", StringComparison.Ordinal)
                && _workerFailureCount < _workerFailuresBeforeSuccess)
            {
                _workerFailureCount++;
                yield return new InvocationFailedProviderEvent(
                    request.InvocationId,
                    new RuntimeError(
                        "WorkerFailed",
                        "Provider",
                        "The worker failed.",
                        IsRetryable: false,
                        ProviderDetails: null,
                        DiagnosticId: "diagnostic-worker-failed"));
                yield break;
            }

            if (_emitReasoningDelta)
            {
                yield return new ReasoningDeltaProviderEvent(
                    request.InvocationId,
                    "thinking...");
            }

            if (_emitToolCallDelta)
            {
                yield return new ToolCallDeltaProviderEvent(
                    request.InvocationId,
                    "call-1",
                    "test_tool",
                    "{\"arg\":");
            }

            yield return new TextDeltaProviderEvent(
                request.InvocationId,
                string.Equals(request.AgentId, "manager", StringComparison.Ordinal)
                    ? _managerOutput ?? DefaultManagerPlanJson
                    : $"result:{request.AgentId}");

            if (_emitUsageUpdated)
            {
                yield return new UsageUpdatedProviderEvent(
                    request.InvocationId,
                    InputTokens: 100,
                    OutputTokens: 50);
            }

            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task UpdateCredentialsAsync(
            CredentialsUpdateParameters parameters,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Credential = parameters.Credential;
            return Task.CompletedTask;
        }
    }

    private const string DefaultManagerPlanJson =
        "{\"planVersion\":\"1\",\"goal\":\"Prepare the release notes.\",\"steps\":["
        + "{\"stepId\":\"run-1:step-1\",\"goal\":\"Prepare the release notes.\",\"targetAgentId\":\"worker\",\"title\":\"Execute\"}]}";

    private async Task<WorkRunResult> RunMultiTurnAsync(
        IReadOnlyList<WorkTurnScript> turns,
        bool requirePlanConfirmation = false)
    {
        ArgumentNullException.ThrowIfNull(turns);
        Assert.IsGreaterThan(0, turns.Count);

        var ct = _testContext.CancellationToken;
        var dataDirectory = Path.Combine(
            _testContext.TestRunDirectory ?? Path.GetTempPath(),
            $"work-multi-{Guid.NewGuid():N}");
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
            using var toolStateStore = new SqliteToolIntentRepository(connection);
            var sessionRepo = new SqliteSessionRepository(connection);
            var workRepo = new SqliteWorkRepository(connection);
            var store = new ConversationStore(dataDirectory);
            var memoryFiles = new MemoryFileService(
                Path.Join(dataDirectory, "home"),
                Path.Join(dataDirectory, "workspace"));
            await memoryFiles.ReplaceAsync(
                MemoryScope.Global,
                "# Memory\n\n- Shared global rule.\n",
                ct);
            await memoryFiles.ReplaceAsync(
                MemoryScope.Project,
                "# Memory\n\n- Shared project rule.\n",
                ct);

            var eventBatches = new List<IReadOnlyList<RuntimeEventEnvelope>>();
            var invokedAgentIds = new List<string>();
            var capturedRequests = new List<RuntimeProviderRequest>();
            RunStatus lastRunStatus = RunStatus.Running;

            foreach (var turn in turns)
            {
                var provider = new FakeProviderAdapter(managerOutput: turn.ManagerOutput);
                var orchestrator = new WorkModeOrchestrator(
                    _ => provider,
                    store,
                    sessionRepo,
                    outbox,
                    new AgentContextComposer(memoryFiles),
                    toolStateStore: toolStateStore,
                    workRepo: workRepo,
                    connection: connection);
                await EnsureSessionRunAsync(connection, "session-1", turn.RunId, ct);
                var request = CreateRequest(
                    WorkStepFailurePolicy.Stop,
                    maxRetriesPerStep: null,
                    retryBackoffMilliseconds: null,
                    contextPolicy: null,
                    requirePlanConfirmation: requirePlanConfirmation) with
                {
                    InitialInput = [new TextContentBlock(turn.UserText)]
                };

                var events = new List<RuntimeEventEnvelope>();
                await foreach (var envelope in orchestrator.RunAsync(
                    turn.RunId,
                    "session-1",
                    request,
                    "runtime-1",
                    ct))
                {
                    events.Add(envelope);
                }

                eventBatches.Add(events);
                invokedAgentIds.AddRange(provider.InvokedAgentIds);
                capturedRequests.AddRange(provider.CapturedRequests);
                lastRunStatus = await sessionRepo.GetRunStatusAsync(turn.RunId, ct);
            }

            var workSteps = await workRepo.ListStepsAsync("session-1", ct: ct);
            var planRevisions = await workRepo.ListPlanRevisionsAsync("session-1", ct);
            var canonicalHistory = await store.ReadAllAsync("session-1", ct);
            var resume = await workRepo.GetResumeStateAsync("session-1", ct);
            return new WorkRunResult(
                invokedAgentIds.ToArray(),
                capturedRequests.ToArray(),
                eventBatches,
                CompletedStepResultJson: null,
                lastRunStatus,
                workSteps,
                new Dictionary<string, InvocationSnapshot>(StringComparer.Ordinal),
                canonicalHistory,
                planRevisions,
                [],
                [],
                [],
                resume?.Status);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    private static string ComputeHash(ContentBlock[] input)
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            input,
            RuntimeJsonContext.Default.ContentBlockArray);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed record WorkTurnScript(string RunId, string? ManagerOutput, string UserText);

    private sealed record WorkRunResult(
        string[] InvokedAgentIds,
        RuntimeProviderRequest[] CapturedRequests,
        IReadOnlyList<IReadOnlyList<RuntimeEventEnvelope>> EventBatches,
        string? CompletedStepResultJson,
        RunStatus RunStatus,
        IReadOnlyList<WorkStepSnapshot> WorkSteps,
        IReadOnlyDictionary<string, InvocationSnapshot> InvocationSnapshots,
        IReadOnlyList<ConversationRecordV1> CanonicalHistory,
        IReadOnlyList<WorkPlanRevisionSnapshot> PlanRevisions,
        IReadOnlyList<ApprovalRequest> WorkflowApprovalRequests,
        IReadOnlyList<CredentialsRefreshRequestedEvent> CredentialRefreshRequests,
        IReadOnlyList<WorkResumeState> CredentialWaitStates,
        WorkSessionStatus? WorkSessionStatus = null,
        IReadOnlyList<DateTimeOffset>? WorkerStartTimestampsUtc = null);
}
