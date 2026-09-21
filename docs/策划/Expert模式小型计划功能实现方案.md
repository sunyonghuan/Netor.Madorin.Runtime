# Expert 模式小型计划功能实现方案

## 1. 设计目标

为 Expert 模式提供轻量级的任务计划能力：
- **文件持久化**：计划保存到 `<工作目录>/.madorin/plans/{datetime}-{status}.json`
- **进度追踪**：每步状态枚举（pending/running/completed/failed/skipped）+ 进度百分比
- **System Prompt 注入**：创建计划后每轮自动注入进度提示，引导模型按计划执行
- **轻量约束**：3-15 步扁平列表，无依赖图，可选使用
- **续跑支持**：重启会话时恢复活跃计划

## 2. 架构设计

### 2.1 核心组件

```
src/Madorin.AI.Runtime.Contracts/Expert/
  ├── ExpertPlanEnums.cs          # 状态枚举
  ├── ExpertPlan.cs               # 计划数据结构
  └── ExpertPlanEvents.cs         # 计划事件

src/Madorin.AI.Runtime.Modes.Expert/
  ├── ExpertModeOrchestrator.cs   # 主编排器（已存在，需修改）
  ├── ExpertPlanManager.cs        # 计划管理器（新增）
  └── ExpertPlanFileStore.cs      # 文件持久化（新增）

src/Madorin.AI.Runtime.Tools.Builtin/Expert/
  ├── BuiltinExpertCreatePlanTool.cs       # 创建计划工具
  ├── BuiltinExpertExecuteStepTool.cs      # 执行步骤工具
  ├── BuiltinExpertUpdateStepStatusTool.cs # 更新状态工具
  └── BuiltinExpertGetPlanTool.cs          # 查看计划工具
```

### 2.2 数据流

```
用户: "帮我优化数据库性能"
  ↓
Expert Agent 分析任务复杂度
  ↓
调用 mcli_expert_create_plan (3-5步计划)
  ↓
ExpertPlanManager.CreatePlanAsync()
  ├─ 验证计划结构（2-15步）
  ├─ 生成 planId
  ├─ 保存到 .madorin/plans/20260921-143022-pending.json
  └─ 设置 _activePlan
  ↓
下一轮 Provider 调用
  ├─ System Prompt 注入进度提示
  ├─ 文件更新为 20260921-143022-running.json
  └─ 提示 "Next Action: Execute Step 1"
  ↓
模型调用 mcli_expert_execute_step(stepId: "1")
  ├─ 标记 Step 1 为 running
  ├─ 调用 InvokeSubAgentAsync()
  ├─ 自动标记 Step 1 为 completed
  └─ 更新文件
  ↓
重复执行直到所有步骤完成
  ↓
文件重命名为 20260921-143022-completed.json
```

## 3. 详细实现步骤

### Phase 1: 数据结构与枚举定义（0.5 天）

#### 3.1 创建 ExpertPlanEnums.cs

```csharp
// src/Madorin.AI.Runtime.Contracts/Expert/ExpertPlanEnums.cs
namespace Madorin.AI.Runtime.Contracts.Expert;

/// <summary>Expert 模式计划的整体状态</summary>
public enum ExpertPlanStatus
{
    Pending,      // 已创建未开始
    Running,      // 执行中
    Paused,       // 暂停（预留）
    Completed,    // 已完成
    Failed,       // 失败
    Cancelled     // 取消
}

/// <summary>Expert 模式计划步骤状态</summary>
public enum ExpertStepStatus
{
    Pending,      // 待执行
    Running,      // 执行中
    Completed,    // 已完成
    Failed,       // 失败
    Skipped       // 跳过
}
```

#### 3.2 创建 ExpertPlan.cs

```csharp
// src/Madorin.AI.Runtime.Contracts/Expert/ExpertPlan.cs
namespace Madorin.AI.Runtime.Contracts.Expert;

/// <summary>Expert 模式轻量计划（持久化到文件）</summary>
public sealed record ExpertPlan(
    string PlanId,
    string SessionId,
    string Goal,
    ExpertPlanStatus Status,
    ExpertPlanStep[] Steps,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>计划中的一个步骤</summary>
public sealed record ExpertPlanStep(
    string StepId,
    string Title,
    string? Description,
    string? AgentName,
    ExpertStepStatus Status,
    int Progress,              // 0-100
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Result,
    string? ErrorMessage);

/// <summary>创建计划的请求参数</summary>
public sealed record CreateExpertPlanRequest(
    string Goal,
    ExpertPlanStepDraft[] Steps);

/// <summary>步骤草稿（创建时使用）</summary>
public sealed record ExpertPlanStepDraft(
    string StepId,
    string Title,
    string? Description = null,
    string? AgentName = null);
```

#### 3.3 创建 ExpertPlanEvents.cs

```csharp
// src/Madorin.AI.Runtime.Contracts/Expert/ExpertPlanEvents.cs
namespace Madorin.AI.Runtime.Contracts.Expert;

/// <summary>计划已创建事件</summary>
public sealed record ExpertPlanCreatedEvent(
    string PlanId,
    string SessionId,
    string Goal,
    int StepCount);

/// <summary>步骤状态更新事件</summary>
public sealed record ExpertStepStatusChangedEvent(
    string PlanId,
    string StepId,
    ExpertStepStatus OldStatus,
    ExpertStepStatus NewStatus,
    int Progress);

/// <summary>计划状态更新事件</summary>
public sealed record ExpertPlanStatusChangedEvent(
    string PlanId,
    ExpertPlanStatus OldStatus,
    ExpertPlanStatus NewStatus);
```

#### 3.4 注册到 RuntimeJsonContext

```csharp
// src/Madorin.AI.Runtime.Contracts/Serialization/RuntimeJsonContext.cs
// 在现有的 [JsonSerializable] 属性列表中添加：

[JsonSerializable(typeof(ExpertPlan))]
[JsonSerializable(typeof(ExpertPlanStep))]
[JsonSerializable(typeof(ExpertPlanStatus))]
[JsonSerializable(typeof(ExpertStepStatus))]
[JsonSerializable(typeof(CreateExpertPlanRequest))]
[JsonSerializable(typeof(ExpertPlanStepDraft))]
[JsonSerializable(typeof(ExpertPlanCreatedEvent))]
[JsonSerializable(typeof(ExpertStepStatusChangedEvent))]
[JsonSerializable(typeof(ExpertPlanStatusChangedEvent))]
```

---

### Phase 2: 文件持久化层（1 天）

#### 3.5 创建 ExpertPlanFileStore.cs

```csharp
// src/Madorin.AI.Runtime.Modes.Expert/ExpertPlanFileStore.cs
using System.Text.Json;
using Madorin.AI.Runtime.Contracts.Expert;
using Madorin.AI.Runtime.Contracts.Serialization;

namespace Madorin.AI.Runtime.Modes.Expert;

/// <summary>Expert 计划文件持久化（保存到 .madorin/plans/）</summary>
internal sealed class ExpertPlanFileStore
{
    private readonly string _plansDirectory;

    public ExpertPlanFileStore(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _plansDirectory = Path.Combine(workspaceRoot, ".madorin", "plans");
        Directory.CreateDirectory(_plansDirectory);
    }

    /// <summary>保存计划到文件（根据状态生成文件名）</summary>
    public async Task SaveAsync(ExpertPlan plan, CancellationToken ct = default)
    {
        var fileName = GenerateFileName(plan.CreatedAt, plan.Status);
        var filePath = Path.Combine(_plansDirectory, fileName);
        
        // 删除同一 planId 的旧状态文件
        await DeleteOldStatusFilesAsync(plan.PlanId, plan.CreatedAt, ct).ConfigureAwait(false);
        
        var json = JsonSerializer.Serialize(plan, RuntimeJsonContext.Default.ExpertPlan);
        await File.WriteAllTextAsync(filePath, json, ct).ConfigureAwait(false);
        
        // 更新 active.json 软链接（指向最新文件）
        await UpdateActiveSymlinkAsync(fileName, ct).ConfigureAwait(false);
    }

    /// <summary>加载最新的活跃计划</summary>
    public async Task<ExpertPlan?> LoadActiveAsync(CancellationToken ct = default)
    {
        var activeFile = Path.Combine(_plansDirectory, "active.json");
        if (File.Exists(activeFile))
        {
            try
            {
                var json = await File.ReadAllTextAsync(activeFile, ct).ConfigureAwait(false);
                var plan = JsonSerializer.Deserialize(json, RuntimeJsonContext.Default.ExpertPlan);
                
                // 只恢复未完成的计划
                if (plan is not null && plan.Status is ExpertPlanStatus.Pending or ExpertPlanStatus.Running)
                {
                    return plan;
                }
            }
            catch (JsonException)
            {
                // 文件损坏，忽略
            }
        }
        
        // 回退：查找最新的 running 或 pending 文件
        return await LoadLatestUnfinishedAsync(ct).ConfigureAwait(false);
    }

    /// <summary>查找最新的未完成计划</summary>
    private async Task<ExpertPlan?> LoadLatestUnfinishedAsync(CancellationToken ct)
    {
        var files = Directory.GetFiles(_plansDirectory, "*-running.json")
            .Concat(Directory.GetFiles(_plansDirectory, "*-pending.json"))
            .OrderByDescending(f => f)
            .ToArray();
        
        foreach (var file in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                var plan = JsonSerializer.Deserialize(json, RuntimeJsonContext.Default.ExpertPlan);
                if (plan is not null)
                {
                    return plan;
                }
            }
            catch (JsonException)
            {
                continue;
            }
        }
        
        return null;
    }

    /// <summary>生成文件名：yyyyMMdd-HHmmss-{status}.json</summary>
    private static string GenerateFileName(DateTimeOffset createdAt, ExpertPlanStatus status)
    {
        var timestamp = createdAt.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        var statusStr = status.ToString().ToLowerInvariant();
        return $"{timestamp}-{statusStr}.json";
    }

    /// <summary>删除同一计划的旧状态文件</summary>
    private async Task DeleteOldStatusFilesAsync(
        string planId,
        DateTimeOffset createdAt,
        CancellationToken ct)
    {
        var timestamp = createdAt.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        var pattern = $"{timestamp}-*.json";
        var oldFiles = Directory.GetFiles(_plansDirectory, pattern);
        
        foreach (var oldFile in oldFiles)
        {
            try
            {
                File.Delete(oldFile);
            }
            catch (IOException)
            {
                // 文件被占用，忽略
            }
        }
        
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>更新 active.json（直接复制内容，Windows 不支持软链接）</summary>
    private async Task UpdateActiveSymlinkAsync(string targetFileName, CancellationToken ct)
    {
        var activeFile = Path.Combine(_plansDirectory, "active.json");
        var targetFile = Path.Combine(_plansDirectory, targetFileName);
        
        if (File.Exists(targetFile))
        {
            var content = await File.ReadAllTextAsync(targetFile, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(activeFile, content, ct).ConfigureAwait(false);
        }
    }

    /// <summary>清理历史文件（保留最近 N 个已完成的计划）</summary>
    public void CleanupOldPlans(int keepCount = 10)
    {
        var completedFiles = Directory.GetFiles(_plansDirectory, "*-completed.json")
            .Concat(Directory.GetFiles(_plansDirectory, "*-failed.json"))
            .Concat(Directory.GetFiles(_plansDirectory, "*-cancelled.json"))
            .OrderByDescending(f => f)
            .Skip(keepCount)
            .ToArray();
        
        foreach (var file in completedFiles)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // 忽略
            }
        }
    }
}
```

---

### Phase 3: 计划管理器（1 天）

#### 3.6 创建 ExpertPlanManager.cs

```csharp
// src/Madorin.AI.Runtime.Modes.Expert/ExpertPlanManager.cs
using Madorin.AI.Runtime.Contracts.Expert;

namespace Madorin.AI.Runtime.Modes.Expert;

/// <summary>Expert 计划管理器（内存状态 + 文件持久化）</summary>
internal sealed class ExpertPlanManager
{
    private readonly ExpertPlanFileStore _fileStore;
    private ExpertPlan? _activePlan;

    public ExpertPlanManager(string workspaceRoot)
    {
        _fileStore = new ExpertPlanFileStore(workspaceRoot);
    }

    /// <summary>当前活跃的计划</summary>
    public ExpertPlan? ActivePlan => _activePlan;

    /// <summary>创建新计划</summary>
    public async Task<ExpertPlan> CreatePlanAsync(
        string sessionId,
        CreateExpertPlanRequest request,
        CancellationToken ct = default)
    {
        // 验证
        if (request.Steps.Length < 2 || request.Steps.Length > 15)
        {
            throw new ArgumentException("Plan must have 2-15 steps.", nameof(request));
        }

        var now = DateTimeOffset.UtcNow;
        var planId = $"plan-{now:yyyyMMdd-HHmmss}";

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
            ErrorMessage: null)).ToArray();

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

        return plan;
    }

    /// <summary>更新步骤状态</summary>
    public async Task<ExpertPlan> UpdateStepStatusAsync(
        string stepId,
        ExpertStepStatus newStatus,
        int? progress = null,
        string? notes = null,
        CancellationToken ct = default)
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
        var now = DateTimeOffset.UtcNow;
        
        var newStep = oldStep with
        {
            Status = newStatus,
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

        var newSteps = _activePlan.Steps.ToArray();
        newSteps[stepIndex] = newStep;

        // 检查整体计划状态
        var newPlanStatus = CalculatePlanStatus(newSteps);

        _activePlan = _activePlan with
        {
            Steps = newSteps,
            Status = newPlanStatus,
            UpdatedAt = now
        };

        await _fileStore.SaveAsync(_activePlan, ct).ConfigureAwait(false);

        return _activePlan;
    }

    /// <summary>恢复活跃计划（续跑时调用）</summary>
    public async Task<ExpertPlan?> RestoreActivePlanAsync(CancellationToken ct = default)
    {
        _activePlan = await _fileStore.LoadActiveAsync(ct).ConfigureAwait(false);
        return _activePlan;
    }

    /// <summary>放弃当前计划</summary>
    public async Task AbandonPlanAsync(CancellationToken ct = default)
    {
        if (_activePlan is not null)
        {
            _activePlan = _activePlan with
            {
                Status = ExpertPlanStatus.Cancelled,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _fileStore.SaveAsync(_activePlan, ct).ConfigureAwait(false);
            _activePlan = null;
        }
    }

    /// <summary>计算整体计划状态</summary>
    private static ExpertPlanStatus CalculatePlanStatus(ExpertPlanStep[] steps)
    {
        if (steps.All(s => s.Status == ExpertStepStatus.Completed))
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

    /// <summary>清理历史文件</summary>
    public void CleanupOldPlans(int keepCount = 10)
    {
        _fileStore.CleanupOldPlans(keepCount);
    }
}
```

---

### Phase 4: 修改 ExpertModeOrchestrator（1.5 天）

#### 3.7 在 ExpertModeOrchestrator 中集成计划管理器

```csharp
// src/Madorin.AI.Runtime.Modes.Expert/ExpertModeOrchestrator.cs

// 在类的字段部分添加：
private ExpertPlanManager? _planManager;

// 在构造函数参数中添加 workspaceRoot（如果还没有）：
public sealed class ExpertModeOrchestrator(
    IRuntimeProviderAdapter provider,
    ConversationStore store,
    SqliteSessionRepository sessionRepo,
    SqliteEventOutbox outbox,
    string? workspaceRoot = null,  // 新增
    AgentContextComposer? agentContextComposer = null,
    // ... 其他参数
)
{
    private readonly string? _workspaceRoot = workspaceRoot;
    private ExpertPlanManager? _planManager;
    
    // ... 现有字段
}

// 在 RunAsync 方法开始处初始化计划管理器：
public async IAsyncEnumerable<RuntimeEventEnvelope> RunAsync(
    string runId,
    string sessionId,
    NewSessionRunRequest request,
    string runtimeInstanceId,
    [EnumeratorCancellation] CancellationToken ct = default)
{
    // ... 现有验证代码
    
    // 初始化计划管理器
    if (_workspaceRoot is not null)
    {
        _planManager ??= new ExpertPlanManager(_workspaceRoot);
        
        // 续跑时恢复计划
        var restoredPlan = await _planManager.RestoreActivePlanAsync(ct).ConfigureAwait(false);
        if (restoredPlan is not null)
        {
            // TODO: 发出计划恢复事件
        }
    }
    
    // ... 继续现有流程
}

// 修改 System Prompt 注入逻辑（在准备 Provider 请求时）：
private string PrepareSystemPromptWithPlan(string baseSystemPrompt)
{
    if (_planManager?.ActivePlan is null)
    {
        return baseSystemPrompt;
    }
    
    return AppendPlanProgressReminder(baseSystemPrompt, _planManager.ActivePlan);
}

// 添加 System Prompt 注入方法：
private static string AppendPlanProgressReminder(
    string baseSystemPrompt,
    ExpertPlan plan)
{
    var sb = new StringBuilder();
    sb.AppendLine();
    sb.AppendLine();
    sb.AppendLine("## 📋 Active Execution Plan");
    sb.AppendLine();
    sb.AppendLine($"**Goal:** {plan.Goal}");
    sb.AppendLine($"**Status:** {plan.Status}");
    sb.AppendLine($"**Progress:** {CalculateOverallProgress(plan.Steps)}%");
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
        
        sb.AppendLine($"{icon} **Step {step.StepId}**: {step.Title} ({step.Status}){progressBar}");
        
        if (!string.IsNullOrEmpty(step.Result))
        {
            sb.AppendLine($"   └─ {step.Result}");
        }
        if (!string.IsNullOrEmpty(step.ErrorMessage))
        {
            sb.AppendLine($"   └─ ⚠️ {step.ErrorMessage}");
        }
    }
    
    sb.AppendLine();
    
    // 关键：明确指示下一步
    var nextStep = plan.Steps.FirstOrDefault(s => s.Status == ExpertStepStatus.Pending);
    if (nextStep is not null)
    {
        sb.AppendLine("### 🎯 Next Action:");
        sb.AppendLine($"Execute **Step {nextStep.StepId}** using `mcli_expert_execute_step`:");
        sb.AppendLine($"```");
        sb.AppendLine($"mcli_expert_execute_step(stepId: \"{nextStep.StepId}\", instruction: \"...\")");
        sb.AppendLine($"```");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(nextStep.AgentName))
        {
            sb.AppendLine($"This step will be delegated to agent: **{nextStep.AgentName}**");
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
        sb.AppendLine($"Step {runningStep.StepId} is currently running.");
        sb.AppendLine($"Update progress using `mcli_expert_update_step_status` if you have intermediate updates.");
        sb.AppendLine();
    }
    else if (plan.Steps.Any(s => s.Status == ExpertStepStatus.Failed))
    {
        sb.AppendLine("### ⚠️ Plan Has Failed Steps");
        sb.AppendLine("Review the errors above and decide whether to:");
        sb.AppendLine("- Retry the failed step with `mcli_expert_execute_step`");
        sb.AppendLine("- Skip it with `mcli_expert_update_step_status(status: 'skipped')`");
        sb.AppendLine("- Abandon the plan with `mcli_expert_abandon_plan`");
        sb.AppendLine();
    }
    
    return baseSystemPrompt + sb.ToString();
}

private static int CalculateOverallProgress(ExpertPlanStep[] steps)
{
    if (steps.Length == 0) return 0;
    return (int)(steps.Average(s => s.Progress));
}

// 在 RunAsync 的主循环中，每轮 Provider 调用前注入计划提示：
// 找到准备 agentContext 的地方，修改为：
var baseSystemPrompt = expert.Agent.SystemPrompt;
var enhancedSystemPrompt = AppendAvailableSubAgentsList(
    baseSystemPrompt,
    expert.Agent,
    expert.AvailableAgents);

// 新增：注入计划进度提示
enhancedSystemPrompt = PrepareSystemPromptWithPlan(enhancedSystemPrompt);

var agentContext = _agentContextComposer is null
    ? AgentContextComposer.WithoutMemory(enhancedSystemPrompt)
    : await _agentContextComposer.ComposeAsync(enhancedSystemPrompt, ct)
        .ConfigureAwait(false);
```

---

### Phase 5: 实现工具（2 天）

#### 3.8 创建计划工具 - BuiltinExpertCreatePlanTool.cs

```csharp
// src/Madorin.AI.Runtime.Tools.Builtin/Expert/BuiltinExpertCreatePlanTool.cs
using System.Text.Json;
using Madorin.AI.Runtime.Contracts.Expert;
using Madorin.AI.Runtime.Modes.Expert;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin.Expert;

internal sealed class BuiltinExpertCreatePlanTool : IToolExecutor
{
    public const string ToolId = "mcli_expert_create_plan";
    
    private readonly ExpertPlanManager _planManager;

    public BuiltinExpertCreatePlanTool(ExpertPlanManager planManager)
    {
        _planManager = planManager ?? throw new ArgumentNullException(nameof(planManager));
    }

    public async Task<ToolExecutionResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        try
        {
            // 解析参数
            var request = JsonSerializer.Deserialize<CreateExpertPlanRequest>(
                invocation.ArgumentsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            
            if (request is null)
            {
                return ToolExecutionResult.Error("Invalid request format.");
            }

            // 验证步骤数量
            if (request.Steps.Length < 2)
            {
                return ToolExecutionResult.Error(
                    "Plan must have at least 2 steps. For single-step tasks, execute directly without creating a plan.");
            }
            
            if (request.Steps.Length > 15)
            {
                return ToolExecutionResult.Error(
                    "Plan cannot exceed 15 steps. For complex tasks with more steps, consider using Work mode instead.");
            }

            // 验证步骤 ID 唯一性
            var duplicateIds = request.Steps
                .GroupBy(s => s.StepId)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToArray();
            
            if (duplicateIds.Length > 0)
            {
                return ToolExecutionResult.Error(
                    $"Duplicate step IDs found: {string.Join(", ", duplicateIds)}. Each step must have a unique ID.");
            }

            // 创建计划
            var plan = await _planManager.CreatePlanAsync(
                invocation.SessionId,
                request,
                ct).ConfigureAwait(false);

            // 返回结果
            var result = new
            {
                success = true,
                planId = plan.PlanId,
                message = $"Plan created with {plan.Steps.Length} steps. You can now execute steps using mcli_expert_execute_step.",
                steps = plan.Steps.Select(s => new
                {
                    stepId = s.StepId,
                    title = s.Title,
                    status = s.Status.ToString().ToLowerInvariant(),
                    agentName = s.AgentName ?? "default"
                }).ToArray()
            };

            return ToolExecutionResult.Success(JsonSerializer.Serialize(result));
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Error($"Failed to create plan: {ex.Message}");
        }
    }
}
```

#### 3.9 执行步骤工具 - BuiltinExpertExecuteStepTool.cs

```csharp
// src/Madorin.AI.Runtime.Tools.Builtin/Expert/BuiltinExpertExecuteStepTool.cs
using System.Text.Json;
using Madorin.AI.Runtime.Contracts.Expert;
using Madorin.AI.Runtime.Modes.Expert;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin.Expert;

internal sealed class BuiltinExpertExecuteStepTool : IToolExecutor
{
    public const string ToolId = "mcli_expert_execute_step";
    
    private readonly ExpertPlanManager _planManager;
    private readonly Func<string, string, string, Task<(string result, bool success)>> _subAgentExecutor;

    public BuiltinExpertExecuteStepTool(
        ExpertPlanManager planManager,
        Func<string, string, string, Task<(string result, bool success)>> subAgentExecutor)
    {
        _planManager = planManager ?? throw new ArgumentNullException(nameof(planManager));
        _subAgentExecutor = subAgentExecutor ?? throw new ArgumentNullException(nameof(subAgentExecutor));
    }

    public async Task<ToolExecutionResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        try
        {
            if (_planManager.ActivePlan is null)
            {
                return ToolExecutionResult.Error(
                    "No active plan. Create a plan first using mcli_expert_create_plan.");
            }

            // 解析参数
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            var root = doc.RootElement;
            
            if (!root.TryGetProperty("stepId", out var stepIdElement))
            {
                return ToolExecutionResult.Error("Missing required parameter: stepId");
            }
            
            var stepId = stepIdElement.GetString();
            if (string.IsNullOrWhiteSpace(stepId))
            {
                return ToolExecutionResult.Error("stepId cannot be empty.");
            }

            if (!root.TryGetProperty("instruction", out var instructionElement))
            {
                return ToolExecutionResult.Error("Missing required parameter: instruction");
            }
            
            var instruction = instructionElement.GetString();
            if (string.IsNullOrWhiteSpace(instruction))
            {
                return ToolExecutionResult.Error("instruction cannot be empty.");
            }

            // 查找步骤
            var step = _planManager.ActivePlan.Steps.FirstOrDefault(s => s.StepId == stepId);
            if (step is null)
            {
                return ToolExecutionResult.Error($"Step '{stepId}' not found in plan.");
            }

            // 检查步骤状态
            if (step.Status is ExpertStepStatus.Completed)
            {
                return ToolExecutionResult.Error(
                    $"Step '{stepId}' is already completed. Result: {step.Result}");
            }

            // 标记为 Running
            await _planManager.UpdateStepStatusAsync(
                stepId,
                ExpertStepStatus.Running,
                progress: 0,
                notes: "Execution started",
                ct).ConfigureAwait(false);

            // 执行步骤（调用子 Agent）
            var agentName = step.AgentName ?? "default";
            var (result, success) = await _subAgentExecutor(agentName, instruction, stepId)
                .ConfigureAwait(false);

            // 更新步骤状态
            var finalStatus = success ? ExpertStepStatus.Completed : ExpertStepStatus.Failed;
            await _planManager.UpdateStepStatusAsync(
                stepId,
                finalStatus,
                progress: success ? 100 : step.Progress,
                notes: result,
                ct).ConfigureAwait(false);

            // 返回结果
            var response = new
            {
                success,
                stepId,
                status = finalStatus.ToString().ToLowerInvariant(),
                result = success ? result : null,
                error = success ? null : result,
                nextStep = _planManager.GetNextPendingStep()?.StepId
            };

            return ToolExecutionResult.Success(JsonSerializer.Serialize(response));
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Error($"Failed to execute step: {ex.Message}");
        }
    }
}
```

#### 3.10 更新状态工具 - BuiltinExpertUpdateStepStatusTool.cs

```csharp
// src/Madorin.AI.Runtime.Tools.Builtin/Expert/BuiltinExpertUpdateStepStatusTool.cs
using System.Text.Json;
using Madorin.AI.Runtime.Contracts.Expert;
using Madorin.AI.Runtime.Modes.Expert;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin.Expert;

internal sealed class BuiltinExpertUpdateStepStatusTool : IToolExecutor
{
    public const string ToolId = "mcli_expert_update_step_status";
    
    private readonly ExpertPlanManager _planManager;

    public BuiltinExpertUpdateStepStatusTool(ExpertPlanManager planManager)
    {
        _planManager = planManager ?? throw new ArgumentNullException(nameof(planManager));
    }

    public async Task<ToolExecutionResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        try
        {
            if (_planManager.ActivePlan is null)
            {
                return ToolExecutionResult.Error("No active plan.");
            }

            // 解析参数
            using var doc = JsonDocument.Parse(invocation.ArgumentsJson);
            var root = doc.RootElement;
            
            if (!root.TryGetProperty("stepId", out var stepIdElement))
            {
                return ToolExecutionResult.Error("Missing required parameter: stepId");
            }
