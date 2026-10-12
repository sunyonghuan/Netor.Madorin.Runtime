using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Expert;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Persistence.Abstractions;

namespace Madorin.AI.Runtime.Modes.Expert;

/// <summary>Expert 计划管理器（内存状态 + 文件持久化）</summary>
internal sealed class ExpertPlanManager
{
    private readonly ExpertPlanFileStore _fileStore;
    private readonly IEventOutbox? _eventOutbox;
    private ExpertPlan? _activePlan;

    public ExpertPlanManager(string workspaceRoot, IEventOutbox? eventOutbox = null)
    {
        _fileStore = new ExpertPlanFileStore(workspaceRoot);
        _eventOutbox = eventOutbox;
    }

    /// <summary>当前活跃的计划</summary>
    public ExpertPlan? ActivePlan => _activePlan;

    /// <summary>创建新计划</summary>
    public async Task<ExpertPlan> CreatePlanAsync(
        string sessionId,
        string runId,
        CreateExpertPlanRequest request,
        CancellationToken ct = default)
    {
        // 验证
        if (request.Steps.Length < 2 || request.Steps.Length > 15)
        {
            throw new ArgumentException("Plan must have 2-15 steps.", nameof(request));
        }

        var now = DateTimeOffset.UtcNow;
        var planId = $"plan-{Guid.NewGuid():N}";

        var steps = request.Steps.Select(draft => new ExpertPlanStep(
            draft.StepId,
            draft.Title,
            draft.Description,
            draft.AgentName,
            ExpertStepStatus.Pending,
            Progress: 0,
            StartedAt: null,
            CompletedAt: null,
            Result: null,
            ErrorMessage: null,
            ExecutionPolicy: draft.ExecutionPolicy,
            ParallelTasks: draft.ParallelTasks,
            CompletionMode: draft.CompletionMode,
            ExpectedOutput: draft.ExpectedOutput)).ToArray();

        var plan = new ExpertPlan(
            planId,
            sessionId,
            request.Goal,
            ExpertPlanStatus.Pending,
            steps,
            now,
            now);

        await _fileStore.SaveAsync(plan, ct).ConfigureAwait(false);
        _activePlan = plan;

        // 发送事件
        await EmitPlanCreatedEventAsync(runId, plan, ct).ConfigureAwait(false);

        return plan;
    }

    /// <summary>更新步骤状态</summary>
    public async Task<ExpertPlan> UpdateStepStatusAsync(
        string stepId,
        string runId,
        ExpertStepStatus newStatus,
        int? progress = null,
        string? notes = null,
        CancellationToken ct = default) =>
        await UpdateStepStatusWithEvidenceAsync(stepId, runId, newStatus, progress, notes, null, ct)
            .ConfigureAwait(false);

    public async Task<ExpertPlan> UpdateStepStatusWithEvidenceAsync(
        string stepId, string runId, ExpertStepStatus newStatus, int? progress,
        string? notes, ExpertToolEvidence[]? evidence, CancellationToken ct = default)
    {
        if (_activePlan is null)
        {
            throw new InvalidOperationException("No active plan.");
        }

        var stepIndex = Array.FindIndex(_activePlan.Steps, s => s.StepId == stepId);
        if (stepIndex < 0)
        {
            throw new ArgumentException($"Step '{stepId}' not found in plan.", nameof(stepId));
        }

        var oldStep = _activePlan.Steps[stepIndex];
        if (oldStep.Status is ExpertStepStatus.Completed or ExpertStepStatus.Failed or ExpertStepStatus.Skipped)
        {
            throw new InvalidOperationException($"Step '{stepId}' is already terminal ({oldStep.Status}).");
        }

        var oldStatus = oldStep.Status;
        var now = DateTimeOffset.UtcNow;

        var newStep = oldStep with
        {
            Status = newStatus,
            Evidence = evidence ?? oldStep.Evidence,
            Progress = progress ?? oldStep.Progress,
            StartedAt = oldStep.StartedAt ?? (newStatus == ExpertStepStatus.Running ? now : null),
            CompletedAt = newStatus is ExpertStepStatus.Completed or ExpertStepStatus.Failed or ExpertStepStatus.Skipped
                ? now
                : oldStep.CompletedAt,
            Result = newStatus == ExpertStepStatus.Completed && notes is not null
                ? notes
                : oldStep.Result,
            ErrorMessage = newStatus == ExpertStepStatus.Failed && notes is not null
                ? notes
                : oldStep.ErrorMessage
        };

        if (newStatus == ExpertStepStatus.Pending)
        {
            newStep = newStep with
            {
                Evidence = null,
                Progress = 0,
                StartedAt = null,
                CompletedAt = null,
                Result = null,
                ErrorMessage = null
            };
        }

        var newSteps = _activePlan.Steps.ToArray();
        newSteps[stepIndex] = newStep;

        // 检查整体计划状态
        var oldPlanStatus = _activePlan.Status;
        var newPlanStatus = CalculatePlanStatus(newSteps);

        var updatedPlan = _activePlan with
        {
            Steps = newSteps,
            Status = newPlanStatus,
            UpdatedAt = now
        };

        await _fileStore.SaveAsync(updatedPlan, ct).ConfigureAwait(false);
        _activePlan = updatedPlan;

        // 发送事件
        if (oldStatus != newStatus)
        {
            await EmitStepStatusChangedEventAsync(runId, _activePlan.PlanId, oldStep.StepId, oldStatus, newStatus, newStep.Progress, ct).ConfigureAwait(false);
        }
        if (oldPlanStatus != newPlanStatus)
        {
            await EmitPlanStatusChangedEventAsync(runId, _activePlan.PlanId, oldPlanStatus, newPlanStatus, ct).ConfigureAwait(false);
        }

        return _activePlan;
    }

    public async Task RecordToolEvidenceAsync(ExpertToolEvidence[] evidence, CancellationToken ct)
    {
        var plan = _activePlan;
        if (plan is null || evidence.Length == 0)
        {
            return;
        }
        var index = Array.FindIndex(plan.Steps, step => step.Status == ExpertStepStatus.Running
            && (step.ExecutionPolicy is null
                || string.Equals(step.ExecutionPolicy, "PrimaryAgent", StringComparison.OrdinalIgnoreCase)));
        if (index < 0)
        {
            return;
        }
        var steps = plan.Steps.ToArray();
        steps[index] = steps[index] with { Evidence = (steps[index].Evidence ?? [])
            .Concat(evidence).DistinctBy(static item => item.CallId).ToArray() };
        var updated = plan with { Steps = steps, UpdatedAt = DateTimeOffset.UtcNow };
        await _fileStore.SaveAsync(updated, ct).ConfigureAwait(false);
        _activePlan = updated;
    }

    /// <summary>按索引更新步骤状态（用于 update_step_status 工具）</summary>
    public async Task<ExpertPlan> UpdateStepStatusByIndexAsync(
        int stepIndex,
        string runId,
        ExpertStepStatus newStatus,
        string? notes = null,
        CancellationToken ct = default)
    {
        if (_activePlan is null)
        {
            throw new InvalidOperationException("No active plan.");
        }

        if (stepIndex < 0 || stepIndex >= _activePlan.Steps.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(stepIndex), $"Step index {stepIndex} is out of range (0-{_activePlan.Steps.Length - 1}).");
        }

        var stepId = _activePlan.Steps[stepIndex].StepId;
        return await UpdateStepStatusAsync(stepId, runId, newStatus, progress: null, notes, ct).ConfigureAwait(false);
    }

    /// <summary>恢复活跃计划（续跑时调用）</summary>
    public async Task<ExpertPlan?> RestoreActivePlanAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _activePlan = await _fileStore.LoadActiveAsync(sessionId, ct).ConfigureAwait(false);
        return _activePlan;
    }

    /// <summary>计算整体计划状态</summary>
    private static ExpertPlanStatus CalculatePlanStatus(ExpertPlanStep[] steps)
    {
        if (steps.All(s => s.Status is ExpertStepStatus.Completed or ExpertStepStatus.Skipped))
        {
            return ExpertPlanStatus.Completed;
        }

        if (steps.Any(s => s.Status == ExpertStepStatus.Failed))
        {
            return ExpertPlanStatus.Failed;
        }

        if (steps.Any(s => s.Status == ExpertStepStatus.Running))
        {
            return ExpertPlanStatus.Running;
        }

        return ExpertPlanStatus.Pending;
    }

    /// <summary>获取下一个待执行的步骤</summary>
    public ExpertPlanStep? GetNextPendingStep()
    {
        return _activePlan?.Steps.FirstOrDefault(s => s.Status == ExpertStepStatus.Pending);
    }

    /// <summary>放弃当前计划</summary>
    public async Task AbandonPlanAsync(string? reason = null, CancellationToken ct = default)
    {
        if (_activePlan is null)
        {
            throw new InvalidOperationException("No active plan to abandon.");
        }

        var updatedSteps = _activePlan.Steps
            .Select(s => s.Status == ExpertStepStatus.Pending || s.Status == ExpertStepStatus.Running
                ? s with { Status = ExpertStepStatus.Skipped }
                : s)
            .ToArray();

        var now = DateTimeOffset.UtcNow;
        var updatedPlan = _activePlan with
        {
            Status = ExpertPlanStatus.Cancelled,
            Steps = updatedSteps,
            UpdatedAt = now
        };

        await _fileStore.SaveAsync(updatedPlan, ct).ConfigureAwait(false);
        _activePlan = null;
    }

    /// <summary>清理历史文件</summary>
    public void CleanupOldPlans(int keepCount = 10)
    {
        _fileStore.CleanupOldPlans(keepCount);
    }

    private async Task EmitPlanCreatedEventAsync(string runId, ExpertPlan plan, CancellationToken ct)
    {
        if (_eventOutbox is null)
        {
            return;
        }

        var eventPayload = new ExpertPlanCreatedEvent(
            plan.PlanId,
            plan.SessionId,
            plan.Goal,
            plan.Steps.Length);

        var payloadJson = JsonSerializer.Serialize(eventPayload, RuntimeJsonContext.Default.ExpertPlanCreatedEvent);
        var runSequence = await _eventOutbox.GetNextRunSequenceAsync(runId, ct).ConfigureAwait(false);
        await _eventOutbox.AppendAsync(runId, runSequence, MessageTypes.ExpertPlanCreated, payloadJson, ct).ConfigureAwait(false);
    }

    private async Task EmitStepStatusChangedEventAsync(
        string runId,
        string planId,
        string stepId,
        ExpertStepStatus oldStatus,
        ExpertStepStatus newStatus,
        int progress,
        CancellationToken ct)
    {
        if (_eventOutbox is null)
        {
            return;
        }

        var eventPayload = new ExpertStepStatusChangedEvent(
            planId,
            stepId,
            oldStatus,
            newStatus,
            progress);

        var payloadJson = JsonSerializer.Serialize(eventPayload, RuntimeJsonContext.Default.ExpertStepStatusChangedEvent);
        var runSequence = await _eventOutbox.GetNextRunSequenceAsync(runId, ct).ConfigureAwait(false);
        await _eventOutbox.AppendAsync(runId, runSequence, MessageTypes.ExpertStepStatusChanged, payloadJson, ct).ConfigureAwait(false);
    }

    private async Task EmitPlanStatusChangedEventAsync(
        string runId,
        string planId,
        ExpertPlanStatus oldStatus,
        ExpertPlanStatus newStatus,
        CancellationToken ct)
    {
        if (_eventOutbox is null)
        {
            return;
        }

        var eventPayload = new ExpertPlanStatusChangedEvent(
            planId,
            oldStatus,
            newStatus);

        var payloadJson = JsonSerializer.Serialize(eventPayload, RuntimeJsonContext.Default.ExpertPlanStatusChangedEvent);
        var runSequence = await _eventOutbox.GetNextRunSequenceAsync(runId, ct).ConfigureAwait(false);
        await _eventOutbox.AppendAsync(runId, runSequence, MessageTypes.ExpertPlanStatusChanged, payloadJson, ct).ConfigureAwait(false);
    }
}

