# Expert 模式全面增强与轻量化改造方案

## 📋 文档信息

- **创建时间**：2025-10-05
- **版本**：v1.0
- **状态**：待评审
- **目标**：将 Runtime 重新定位为「轻量级 AI 访问协调器」，专注 Expert 模式的全功能控制

---

## 1. 战略目标

### 1.1 核心定位转变

**从**：多模式编排框架（Expert + Work + Meeting）  
**到**：单一 Expert 模式 + 全功能请求控制的轻量级 CLI

**设计哲学**：
- ✅ CLI 提供**细粒度请求控制**（温度、JSON 模式、流式、HTTP）
- ✅ 上层智能体框架负责**编排逻辑**（计划、调度、多 Agent 协作）
- ✅ 避免在 CLI 层实现重型状态机（Work 计划调度、Meeting 轮询）

### 1.2 移除内容

**立即移除**（Phase 1）：
- ❌ `RuntimeMode.Work` 枚举值
- ❌ `WorkModeOptions` 及相关契约
- ❌ `WorkModeOrchestrator` 及完整实现
- ❌ `SqliteWorkRepository` 及数据库表
- ❌ `mcli_work_*` 内置工具（set_plan、dispatch_step、ask_user 等）
- ❌ Work 模式相关文档、测试、示例

**立即移除**（Phase 1）：
- ❌ `RuntimeMode.Meeting` 枚举值
- ❌ `MeetingModeOptions` 及相关契约
- ❌ `MeetingModeOrchestrator` 及完整实现
- ❌ `SqliteMeetingRepository` 及数据库表
- ❌ `mcli_meeting_*` 内置工具
- ❌ Meeting 模式相关文档、测试、示例

**保留内容**：
- ✅ Expert 模式全部代码
- ✅ `mcli_delegate_agent` 工具（子 Agent 委派）
- ✅ `mcli_agent_job_*` 工具（并行作业）
- ✅ 小型计划功能（`mcli_expert_create_plan` 等，见下文）

---

## 2. Expert 模式增强清单

### 2.1 请求级参数控制

**目标**：让上层框架可以精细控制每次 Provider 调用的参数。

#### 2.1.1 扩展 ExpertModeOptions

```csharp
// src/Madorin.AI.Runtime.Contracts/Selection/ModeOptions.cs

public sealed record ExpertModeOptions : ModeOptions
{
    public AgentRef Agent { get; init; }
    public AgentRef[]? AvailableAgents { get; init; }
    public ExpertContextPolicy? ContextPolicy { get; init; }
    
    /// <summary>新增：请求控制参数（覆盖 Agent 默认配置）</summary>
    public ExpertRequestControl? RequestControl { get; init; }
    
    [JsonConstructor]
    public ExpertModeOptions(
        AgentRef agent,
        AgentRef[]? availableAgents = null,
        ExpertContextPolicy? contextPolicy = null,
        ExpertRequestControl? requestControl = null)
    {
        Agent = agent;
        AvailableAgents = availableAgents;
        ContextPolicy = contextPolicy;
        RequestControl = requestControl;
    }
}
```

#### 2.1.2 新增 ExpertRequestControl

```csharp
// src/Madorin.AI.Runtime.Contracts/Expert/ExpertRequestControl.cs

namespace Madorin.AI.Runtime.Contracts.Expert;

/// <summary>
/// Expert 模式请求级控制参数。
/// 所有字段为 null 时使用 Provider 默认值或 Agent 配置。
/// </summary>
public sealed record ExpertRequestControl
{
    /// <summary>温度（0.0-2.0，典型值 0.7）</summary>
    public float? Temperature { get; init; }
    
    /// <summary>最大生成 token 数</summary>
    public int? MaxTokens { get; init; }
    
    /// <summary>Top-P 采样（0.0-1.0）</summary>
    public float? TopP { get; init; }
    
    /// <summary>Top-K 采样（整数）</summary>
    public int? TopK { get; init; }
    
    /// <summary>停止序列</summary>
    public string[]? Stop { get; init; }
    
    /// <summary>频率惩罚（-2.0 到 2.0）</summary>
    public float? FrequencyPenalty { get; init; }
    
    /// <summary>存在惩罚（-2.0 到 2.0）</summary>
    public float? PresencePenalty { get; init; }
    
    /// <summary>响应格式：null（默认文本）| "json" | "text"</summary>
    public string? ResponseFormat { get; init; }
    
    /// <summary>结构化输出 Schema（ResponseFormat="json" 时生效）</summary>
    public string? ResponseSchemaJson { get; init; }
    
    /// <summary>是否启用流式响应（null = 默认启用）</summary>
    public bool? Stream { get; init; }
    
    /// <summary>推理模式（预留给支持 reasoning 的模型，如 o1）</summary>
    public string? ReasoningEffort { get; init; } // "low" | "medium" | "high"
    
    /// <summary>随机种子（确定性生成，部分 Provider 支持）</summary>
    public int? Seed { get; init; }
}
```

#### 2.1.3 CLI 参数映射

```bash
# 示例 1：控制温度和 JSON 输出
madorin-cli expert run \
  --temperature 0.3 \
  --response-format json \
  --max-tokens 2000 \
  "生成项目计划的 JSON 结构"

# 示例 2：非流式 + 结构化输出
madorin-cli expert run \
  --no-stream \
  --response-format json \
  --response-schema-file ./schemas/plan.json \
  "根据 schema 生成计划"

# 示例 3：高温度 + 创意生成
madorin-cli expert run \
  --temperature 1.5 \
  --top-p 0.95 \
  --presence-penalty 0.6 \
  "写一个科幻故事"
```

#### 2.1.4 传递到 RuntimeProviderRequest

修改 `ExpertModeOrchestrator.RunAsync` 中构建请求的位置：

```csharp
// src/Madorin.AI.Runtime.Modes.Expert/ExpertModeOrchestrator.cs (约 L523)

var providerRequest = new RuntimeProviderRequest(
    invocationId,
    expert.Agent.AgentId,
    providerId,
    modelId,
    [.. providerMessages],
    Tools: tools,
    // ✅ 应用 RequestControl 参数
    Temperature: expert.RequestControl?.Temperature,
    MaxTokens: expert.RequestControl?.MaxTokens,
    StructuredOutput: expert.RequestControl?.ResponseFormat == "json" 
        ? CreateStructuredOutput(expert.RequestControl)
        : null,
    CancellationToken: ct,
    Skills: CreateSkillsContext(snapshot, runId, sessionId))
{
    HistoryMessageCount = Math.Max(0, providerMessages.Count - 1)
};

// 辅助方法
private static RuntimeStructuredOutput? CreateStructuredOutput(ExpertRequestControl control)
{
    if (control.ResponseFormat != "json")
        return null;
    
    var schema = string.IsNullOrWhiteSpace(control.ResponseSchemaJson)
        ? JsonDocument.Parse("{\"type\":\"object\"}").RootElement
        : JsonDocument.Parse(control.ResponseSchemaJson).RootElement;
    
    return new RuntimeStructuredOutput("response", schema);
}
```

#### 2.1.5 扩展 RuntimeProviderRequest（如需更多参数）

当前 `RuntimeProviderRequest` 已支持：
- ✅ `Temperature`
- ✅ `MaxTokens`
- ✅ `StructuredOutput`

**需要新增**（如果 Provider 支持）：

```csharp
// src/Providers/Madorin.AI.Runtime.Providers.Abstractions/RuntimeProviderRequest.cs

public sealed record RuntimeProviderRequest(
    // ... 现有参数 ...
    float? Temperature = null,
    int? MaxTokens = null,
    RuntimeStructuredOutput? StructuredOutput = null,
    
    // ✅ 新增参数
    float? TopP = null,
    int? TopK = null,
    string[]? Stop = null,
    float? FrequencyPenalty = null,
    float? PresencePenalty = null,
    int? Seed = null,
    string? ReasoningEffort = null, // "low" | "medium" | "high"
    
    // ... 其他参数 ...
);
```

**Provider 适配**：每个 Provider 实现（OpenAI/Anthropic/Aliyun）需映射到原生 API：

```csharp
// 示例：OpenAI Provider
var openAiRequest = new ChatCompletionRequest
{
    Model = request.ModelId,
    Messages = MapMessages(request.Messages),
    Temperature = request.Temperature,
    MaxTokens = request.MaxTokens,
    TopP = request.TopP,
    FrequencyPenalty = request.FrequencyPenalty,
    PresencePenalty = request.PresencePenalty,
    Stop = request.Stop,
    Seed = request.Seed,
    ResponseFormat = request.StructuredOutput != null 
        ? new { type = "json_object" } 
        : null
};
```

---

### 2.2 HTTP 非流式响应接口

**目标**：支持同步 HTTP 请求/响应模式，而非仅 SSE 流式。

#### 2.2.1 新增 REST 端点

```http
POST /api/v1/sessions/{sessionId}/runs/sync
Content-Type: application/json

{
  "mode": "expert",
  "selection": { ... },
  "initialInput": [ ... ],
  "requestControl": {
    "temperature": 0.7,
    "responseFormat": "json",
    "stream": false
  }
}

---

Response 200 OK (阻塞等待完成)
{
  "runId": "run_abc123",
  "status": "completed",
  "content": [
    { "type": "text", "text": "..." }
  ],
  "usage": {
    "inputTokens": 1234,
    "outputTokens": 567
  }
}
```

#### 2.2.2 实现策略

**方案 A：复用流式，内部缓冲**

```csharp
// src/Madorin.AI.Runtime.Server/Controllers/SessionsController.cs

[HttpPost("{sessionId}/runs/sync")]
public async Task<IActionResult> CreateSyncRunAsync(
    string sessionId,
    [FromBody] NewSessionRunRequest request,
    CancellationToken ct)
{
    // 1. 强制非流式
    if (request.Selection.ModeOptions is ExpertModeOptions expert)
    {
        request = request with
        {
            Selection = request.Selection with
            {
                ModeOptions = expert with
                {
                    RequestControl = expert.RequestControl with { Stream = false }
                }
            }
        };
    }
    
    // 2. 运行并缓冲所有事件
    var events = new List<RuntimeEventEnvelope>();
    await foreach (var envelope in _orchestrator.RunAsync(request, ct))
    {
        events.Add(envelope);
        
        // 遇到终止事件则停止
        if (IsTerminalEvent(envelope.MessageType))
            break;
    }
    
    // 3. 提取最终内容
    var content = ExtractFinalContent(events);
    var usage = ExtractUsage(events);
    var runId = events.FirstOrDefault()?.RunId ?? "unknown";
    var status = ExtractFinalStatus(events);
    
    return Ok(new
    {
        runId,
        status,
        content,
        usage
    });
}
```

**方案 B：Provider 层禁用流式**

修改 Provider 调用时：
- 当 `RequestControl.Stream == false` 时，调用 Provider 的非流式 API（如果支持）
- 一次性返回完整响应

---

### 2.3 小型计划功能（整合现有设计）

**目标**：在 Expert 模式中提供 3-15 步的轻量计划能力，作为 Work 模式的简化替代。

#### 2.3.1 功能定位

- **轻量级**：无复杂调度，无依赖图，只有扁平的步骤列表
- **System Prompt 注入**：创建计划后自动注入进度提示，引导模型执行
- **文件持久化**：保存到 `.madorin/plans/{datetime}-{status}.json`
- **可选使用**：模型可选择是否创建计划，适用于复杂任务

#### 2.3.2 核心工具

```json
{
  "tools": [
    {
      "toolId": "mcli_expert_create_plan",
      "name": "create_plan",
      "description": "创建一个 3-15 步的执行计划（适用于复杂任务）",
      "inputSchema": {
        "type": "object",
        "properties": {
          "goal": { "type": "string", "description": "计划目标" },
          "steps": {
            "type": "array",
            "minItems": 3,
            "maxItems": 15,
            "items": {
              "type": "object",
              "properties": {
                "title": { "type": "string" },
                "description": { "type": "string" },
                "agentName": { "type": "string", "description": "可选：负责此步骤的子 Agent" }
              },
              "required": ["title"]
            }
          }
        },
        "required": ["goal", "steps"]
      }
    },
    {
      "toolId": "mcli_expert_execute_step",
      "name": "execute_step",
      "description": "执行计划中的某个步骤",
      "inputSchema": {
        "type": "object",
        "properties": {
          "stepId": { "type": "string", "description": "步骤 ID（如 'step-1'）" }
        },
        "required": ["stepId"]
      }
    },
    {
      "toolId": "mcli_expert_get_plan",
      "name": "get_plan",
      "description": "查看当前活跃计划的状态"
    },
    {
      "toolId": "mcli_expert_update_step_status",
      "name": "update_step_status",
      "description": "手动更新步骤状态（用于跳过、重试）",
      "inputSchema": {
        "type": "object",
        "properties": {
          "stepIndex": { "type": "integer" },
          "newStatus": { "type": "string", "enum": ["pending", "running", "completed", "failed", "skipped"] },
          "notes": { "type": "string" }
        },
        "required": ["stepIndex", "newStatus"]
      }
    },
    {
      "toolId": "mcli_expert_abandon_plan",
      "name": "abandon_plan",
      "description": "放弃当前计划"
    }
  ]
}
```

#### 2.3.3 System Prompt 注入示例

当活跃计划存在时，每轮在 System Prompt 末尾注入：

```markdown
## 📋 Active Execution Plan

**Goal:** 优化数据库查询性能
**Status:** Running
**Progress:** 40%

### Steps:

✅ **Step 1**: 分析慢查询日志 (Completed)
   └─ 已识别 3 个慢查询，平均响应时间 2.5s

▶️ **Step 2**: 添加索引优化 (Running) [████░░░░░░] 40%

⬜ **Step 3**: 验证性能提升 (Pending)

⬜ **Step 4**: 更新文档 (Pending)

### 🎯 Next Action:
Continue Step 2 by calling `execute_step(stepId: "step-2")` or complete it manually.
```

#### 2.3.4 数据结构（复用现有设计）

```csharp
// src/Madorin.AI.Runtime.Contracts/Expert/ExpertPlan.cs

public sealed record ExpertPlan(
    string PlanId,
    string SessionId,
    string Goal,
    ExpertPlanStatus Status,
    ExpertPlanStep[] Steps,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

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
```

#### 2.3.5 文件存储路径

```
<workspace>/.madorin/plans/
  ├── 20251005-143022-running.json    # 当前活跃计划
  ├── 20251005-120000-completed.json  # 历史计划
  └── 20251005-110000-failed.json
```

---

## 3. 实施计划

### Phase 1：移除 Work/Meeting 模式（2-3 天）

**Day 1：代码移除**
- [ ] 删除 `RuntimeMode.Work` 和 `RuntimeMode.Meeting` 枚举
- [ ] 删除 `src/Madorin.AI.Runtime.Modes.Work/` 整个目录
- [ ] 删除 `src/Madorin.AI.Runtime.Modes.Meeting/` 整个目录
- [ ] 删除 `SqliteWorkRepository`、`SqliteMeetingRepository`
- [ ] 从 `BuiltinToolRegistry` 移除 `mcli_work_*`、`mcli_meeting_*` 工具
- [ ] 移除 Work/Meeting 相关契约类型

**Day 2：清理依赖与测试**
- [ ] 修复编译错误（移除模式选择器中的 Work/Meeting 分支）
- [ ] 删除 Work/Meeting 单元测试
- [ ] 删除 Work/Meeting 集成测试
- [ ] 更新 CLI 帮助文档（移除 work/meeting 子命令）

**Day 3：文档更新**
- [ ] 删除 `docs/使用文档/07-Work模式使用指南.md`
- [ ] 删除 `docs/开发文档/16-Meeting模式契约.md` 和 `17-Work模式契约.md`
- [ ] 更新 README.md（只保留 Expert 模式说明）
- [ ] 更新所有示例代码

### Phase 2：Expert 请求控制增强（3-4 天）

**Day 1：协议层扩展**
- [ ] 创建 `ExpertRequestControl.cs`
- [ ] 扩展 `ExpertModeOptions` 增加 `RequestControl` 字段
- [ ] 扩展 `RuntimeProviderRequest` 增加 TopP/TopK/Stop 等参数
- [ ] 注册到 `RuntimeJsonContext`

**Day 2：Orchestrator 传参**
- [ ] 修改 `ExpertModeOrchestrator.RunAsync` 应用 RequestControl
- [ ] 实现 `CreateStructuredOutput` 辅助方法
- [ ] 添加参数验证逻辑（范围检查）

**Day 3：Provider 适配**
- [ ] OpenAI Provider 映射新参数到原生 API
- [ ] Anthropic Provider 映射新参数
- [ ] Aliyun Provider 映射新参数
- [ ] 其他 Provider 适配

**Day 4：CLI 参数**
- [ ] 添加 `--temperature`、`--max-tokens`、`--response-format` 等选项
- [ ] 添加 `--response-schema-file` 选项（读取 JSON Schema）
- [ ] 更新 CLI 帮助和示例

### Phase 3：HTTP 非流式接口（2 天）

**Day 1：实现同步端点**
- [ ] 新增 `POST /sessions/{sessionId}/runs/sync` 端点
- [ ] 实现事件缓冲与内容提取逻辑
- [ ] 添加超时控制（默认 300s）

**Day 2：测试与文档**
- [ ] 单元测试：验证同步响应格式
- [ ] 集成测试：对比流式与非流式结果一致性
- [ ] API 文档更新

### Phase 4：小型计划功能完善（3-4 天）

**Day 1：数据结构与存储**
- [ ] 创建 `ExpertPlan.cs`、`ExpertPlanEnums.cs`
- [ ] 实现 `ExpertPlanFileStore.cs`（文件持久化）
- [ ] 实现 `ExpertPlanManager.cs`（创建、更新、恢复）

**Day 2：内置工具**
- [ ] 实现 `BuiltinExpertCreatePlanTool.cs`
- [ ] 实现 `BuiltinExpertExecuteStepTool.cs`
- [ ] 实现 `BuiltinExpertGetPlanTool.cs`
- [ ] 实现 `BuiltinExpertUpdateStepStatusTool.cs`
- [ ] 实现 `BuiltinExpertAbandonPlanTool.cs`

**Day 3：System Prompt 注入**
- [ ] 修改 `ExpertModeOrchestrator` 在每轮注入计划进度
- [ ] 实现进度条渲染逻辑
- [ ] 实现 Next Action 提示生成

**Day 4：测试与优化**
- [ ] 单元测试：计划创建、步骤更新、文件存储
- [ ] 集成测试：端到端计划执行流程
- [ ] 性能测试：大计划（15 步）的加载性能

### Phase 5：文档与示例（2 天）

**Day 1：使用文档**
- [ ] 编写《Expert 模式完整指南》
- [ ] 编写《请求控制参数详解》
- [ ] 编写《小型计划功能使用指南》

**Day 2：示例代码**
- [ ] 示例 1：JSON 结构化输出
- [ ] 示例 2：高温度创意生成
- [ ] 示例 3：非流式同步请求
- [ ] 示例 4：使用计划功能的复杂任务

---

## 4. 技术细节补充

### 4.1 流式控制实现

**流式模式（默认）**：
```csharp
// 保持现有行为，SSE 逐块返回
await foreach (var envelope in orchestrator.RunAsync(request, ct))
{
    yield return envelope;
}
```

**非流式模式**：
```csharp
// 方案 A：内部仍然流式，但缓冲所有事件
var allEvents = new List<RuntimeEventEnvelope>();
await foreach (var envelope in orchestrator.RunAsync(request, ct))
{
    allEvents.Add(envelope);
    if (IsTerminal(envelope)) break;
}
return BuildSyncResponse(allEvents);

// 方案 B：Provider 层直接调用非流式 API（如果支持）
var response = await provider.CompleteNonStreamingAsync(request, ct);
return MapToRuntimeEvents(response);
```

### 4.2 JSON Schema 验证

**步骤 1：接收 Schema**
```csharp
// CLI 参数
--response-schema-file ./schemas/project-plan.json

// 读取并传递
var schemaJson = File.ReadAllText(schemaFilePath);
var requestControl = new ExpertRequestControl
{
    ResponseFormat = "json",
    ResponseSchemaJson = schemaJson
};
```

**步骤 2：传递给 Provider**
```csharp
var structuredOutput = new RuntimeStructuredOutput(
    "response",
    JsonDocument.Parse(schemaJson).RootElement,
    "Project plan structure");

var providerRequest = new RuntimeProviderRequest(
    // ...
    StructuredOutput: structuredOutput
);
```

**步骤 3：Provider 映射**
```csharp
// OpenAI
new ChatCompletionRequest
{
    ResponseFormat = new 
    { 
        type = "json_schema", 
        json_schema = new 
        { 
            name = "response", 
            schema = schemaJson 
        } 
    }
}

// Anthropic（截至 2025-01，Claude 不支持严格 schema）
// 退化为在 system prompt 中描述 schema
```

### 4.3 计划持久化格式

**文件名规则**：`{创建时间}-{状态}.json`

**示例**：
```json
{
  "planId": "plan_2025100514302212",
  "sessionId": "sess_abc123",
  "goal": "优化数据库查询性能",
  "status": "Running",
  "steps": [
    {
      "stepId": "step-1",
      "title": "分析慢查询日志",
      "description": "使用 pg_stat_statements 识别慢查询",
      "agentName": null,
      "status": "Completed",
      "progress": 100,
      "startedAt": "2025-10-05T14:30:25Z",
      "completedAt": "2025-10-05T14:32:10Z",
      "result": "识别到 3 个慢查询，平均响应时间 2.5s",
      "errorMessage": null
    },
    {
      "stepId": "step-2",
      "title": "添加索引优化",
      "description": "为高频查询字段添加 B-Tree 索引",
      "agentName": "database-expert",
      "status": "Running",
      "progress": 40,
      "startedAt": "2025-10-05T14:32:15Z",
      "completedAt": null,
      "result": null,
      "errorMessage": null
    }
  ],
  "createdAt": "2025-10-05T14:30:22Z",
  "updatedAt": "2025-10-05T14:35:00Z"
}
```

**状态转换规则**：
- 创建时 → `pending`
- 开始执行第一步 → `running`
- 所有步骤 `completed` → `completed`
- 任意步骤 `failed` 且无后续执行 → `failed`
- 调用 `abandon_plan` → `cancelled`

### 4.4 System Prompt 注入时机

**注入位置**：`ExpertModeOrchestrator.RunAsync` 中构建 System Prompt 时

```csharp
// 原有逻辑
var baseSystemPrompt = expert.Agent.SystemPrompt;
var withSubAgents = AppendAvailableSubAgentsList(baseSystemPrompt, expert.Agent, expert.AvailableAgents);

// ✅ 新增：注入计划进度
var enhancedSystemPrompt = _planManager?.ActivePlan != null
    ? AppendPlanProgressReminder(withSubAgents, _planManager.ActivePlan)
    : withSubAgents;

var agentContext = _agentContextComposer is null
    ? AgentContextComposer.WithoutMemory(enhancedSystemPrompt)
    : await _agentContextComposer.ComposeAsync(enhancedSystemPrompt, ct).ConfigureAwait(false);
```

**生成逻辑**：
```csharp
private static string AppendPlanProgressReminder(string basePrompt, ExpertPlan plan)
{
    var sb = new StringBuilder();
    sb.AppendLine();
    sb.AppendLine("## 📋 Active Execution Plan");
    sb.AppendLine($"**Goal:** {plan.Goal}");
    sb.AppendLine($"**Status:** {plan.Status}");
    sb.AppendLine($"**Progress:** {CalculateOverallProgress(plan.Steps)}%");
    sb.AppendLine();
    
    foreach (var step in plan.Steps)
    {
        var icon = step.Status switch
        {
            ExpertStepStatus.Completed => "✅",
            ExpertStepStatus.Running => "▶️",
            ExpertStepStatus.Failed => "❌",
            _ => "⬜"
        };
        sb.AppendLine($"{icon} **{step.StepId}**: {step.Title} ({step.Status})");
        if (!string.IsNullOrEmpty(step.Result))
            sb.AppendLine($"   └─ {step.Result}");
    }
    
    var nextStep = plan.Steps.FirstOrDefault(s => s.Status == ExpertStepStatus.Pending);
    if (nextStep != null)
    {
        sb.AppendLine();
        sb.AppendLine("### 🎯 Next Action:");
        sb.AppendLine($"Execute **{nextStep.StepId}** by calling `execute_step(stepId: \"{nextStep.StepId}\")`");
    }
    
    return basePrompt + sb.ToString();
}
```

---

## 5. 风险与缓解

### 5.1 破坏性变更

**风险**：移除 Work/Meeting 模式会破坏现有用户。

**缓解**：
- ✅ 在 CHANGELOG 中标注 BREAKING CHANGE
- ✅ 提供迁移指南（如何用 Expert + 外部编排替代）
- ✅ 考虑保留废弃版本 1-2 个月（标记为 deprecated）

### 5.2 Provider 兼容性

**风险**：部分 Provider 不支持所有参数（如 TopK、Seed）。

**缓解**：
- ✅ 在 `ProviderCapabilities` 中声明支持的参数
- ✅ Adapter 层忽略不支持的参数，记录警告日志
- ✅ 文档中注明各 Provider 的参数支持矩阵

### 5.3 计划功能过度设计

**风险**：小型计划功能可能被滥用，导致复杂度回升。

**缓解**：
- ✅ 硬性限制 3-15 步，拒绝更大计划
- ✅ 不支持依赖图、并行步骤、条件分支
- ✅ 文档中明确说明：复杂编排应在上层框架实现

---

## 6. 成功指标

### 6.1 代码指标

- [ ] **代码减少**：相比重构前减少 30%+ 代码量（移除 Work/Meeting）
- [ ] **文件减少**：删除 50+ 个文件（含测试、文档）
- [ ] **编译时间**：AOT 编译时间减少 20%

### 6.2 功能指标

- [ ] **参数控制**：支持 10+ 请求级参数（温度、TopP、Stop 等）
- [ ] **响应模式**：同时支持流式（SSE）和非流式（HTTP JSON）
- [ ] **计划功能**：支持 3-15 步计划创建、执行、恢复

### 6.3 文档指标

- [ ] **新手指南**：10 分钟内完成第一个 Expert 请求
- [ ] **参数文档**：覆盖所有 `RequestControl` 参数的详细说明
- [ ] **示例库**：提供 5+ 个真实场景的完整示例

---

## 7. 后续演进

### 7.1 短期（1-2 个月）

- [ ] 支持更多 Provider（Gemini、Mistral、Cohere）
- [ ] 优化非流式响应的性能（减少内存占用）
- [ ] 增加计划模板库（常见任务的预定义计划）

### 7.2 中期（3-6 个月）

- [ ] 支持 Batch API（一次提交多个请求）
- [ ] 支持 Function Calling 的细粒度控制（并行度、超时）
- [ ] 增加 Metrics 暴露（Prometheus 格式）

### 7.3 长期（6-12 个月）

- [ ] 支持多模态输入（图片、音频）
- [ ] 支持 Reasoning 模型的特殊参数（如 o1 的 thinking budget）
- [ ] 提供 Web UI（可选，用于调试和监控）

---

## 8. 附录

### 8.1 完整 CLI 参数列表

```bash
madorin-cli expert run [OPTIONS] <prompt>

OPTIONS:
  # 基础参数
  --session-id <ID>              会话 ID（默认：随机生成）
  --model <NAME>                 模型 ID（覆盖 Agent 配置）
  --provider <NAME>              Provider ID
  
  # 请求控制
  --temperature <FLOAT>          温度 0.0-2.0（默认：1.0）
  --max-tokens <INT>             最大生成 token
  --top-p <FLOAT>                Top-P 采样 0.0-1.0
  --top-k <INT>                  Top-K 采样
  --frequency-penalty <FLOAT>    频率惩罚 -2.0 到 2.0
  --presence-penalty <FLOAT>     存在惩罚 -2.0 到 2.0
  --stop <STRING>                停止序列（可多次指定）
  --seed <INT>                   随机种子（确定性生成）
  
  # 响应格式
  --response-format <text|json>  响应格式（默认：text）
  --response-schema-file <PATH>  JSON Schema 文件路径
  --no-stream                    禁用流式输出（阻塞等待完成）
  
  # 子 Agent
  --available-agents <FILE>      子 Agent 配置 JSON 文件
  
  # 输出控制
  --output-format <text|json>    输出格式（默认：text）
  --verbose                      详细日志
  --quiet                        安静模式（只输出结果）
```

### 8.2 RequestControl 完整字段映射表

| 字段 | 类型 | 范围 | OpenAI | Anthropic | Aliyun | 说明 |
|------|------|------|--------|-----------|--------|------|
| `Temperature` | float? | 0.0-2.0 | ✅ | ✅ | ✅ | 采样温度 |
| `MaxTokens` | int? | 1-∞ | ✅ | ✅ | ✅ | 最大生成 token |
| `TopP` | float? | 0.0-1.0 | ✅ | ✅ | ✅ | 核采样 |
| `TopK` | int? | 1-∞ | ❌ | ✅ | ✅ | Top-K 采样 |
| `Stop` | string[]? | - | ✅ | ✅ | ✅ | 停止序列 |
| `FrequencyPenalty` | float? | -2.0-2.0 | ✅ | ❌ | ❌ | 频率惩罚 |
| `PresencePenalty` | float? | -2.0-2.0 | ✅ | ❌ | ❌ | 存在惩罚 |
| `ResponseFormat` | string? | text/json | ✅ | ⚠️ | ✅ | 响应格式 |
| `ResponseSchemaJson` | string? | - | ✅ | ❌ | ⚠️ | 严格 Schema |
| `Stream` | bool? | - | ✅ | ✅ | ✅ | 流式开关 |
| `ReasoningEffort` | string? | low/medium/high | ⚠️ | ❌ | ❌ | o1 专用 |
| `Seed` | int? | - | ✅ | ❌ | ❌ | 确定性生成 |

**图例**：
- ✅ 完全支持
- ⚠️ 部分支持或需特殊处理
- ❌ 不支持（Adapter 忽略此参数）

---

## 9. 总结

本方案通过**移除 Work/Meeting 模式**，将 Runtime 重新定位为**轻量级 AI 访问协调器**，专注于提供 Expert 模式的**全功能请求控制**。同时保留**小型计划功能**作为简化替代，使上层智能体框架可以基于 Expert 模式实现复杂编排。

**核心价值**：
1. ✅ 大幅简化代码库（减少 30%+ 代码）
2. ✅ 提供细粒度请求控制（温度、JSON 模式、流式开关）
3. ✅ 支持多种响应模式（流式 SSE + 非流式 HTTP）
4. ✅ 保留轻量计划能力（3-15 步，无重型状态机）

**实施周期**：12-14 天（5 个 Phase）

**风险等级**：中等（主要是破坏性变更，需做好迁移准备）

---

**审批状态**：⏳ 待评审

**下一步**：评审通过后立即开始 Phase 1（移除 Work/Meeting 模式）
