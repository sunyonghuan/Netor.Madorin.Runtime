# Expert 模式增强 - 激进重构执行清单

> **关联文档**：`docs/策划/Expert模式全面增强与轻量化改造方案.md`  
> **创建时间**：2025-10-05  
> **预计工期**：12-14 天  
> **执行模式**：激进重构（立即移除 Work/Meeting）

---

## 📊 整体进度概览

- [ ] **Phase 1**: 移除 Work/Meeting 模式（2-3 天）—— 0/3 天
- [ ] **Phase 2**: Expert 请求控制增强（3-4 天）—— 0/4 天
- [ ] **Phase 3**: HTTP 非流式接口（2 天）—— 0/2 天
- [ ] **Phase 4**: 小型计划功能完善（3-4 天）—— 0/4 天
- [ ] **Phase 5**: 文档与示例（2 天）—— 0/2 天

**总进度**：0/15 天（0%）

---

## Phase 1：移除 Work/Meeting 模式（2-3 天）

### Day 1：核心代码移除

#### 1.1 枚举与契约清理
- [ ] 从 `RuntimeMode` 枚举删除 `Work` 和 `Meeting`
  - 文件：`src/Madorin.AI.Runtime.Entities/RuntimeMode.cs`
  - 修改后只保留：`Expert`
  
- [ ] 删除 `WorkModeOptions` 及相关类型
  - 文件：`src/Madorin.AI.Runtime.Contracts/Selection/ModeOptions.cs`
  - 删除：`WorkModeOptions`、`WorkflowPolicy`、`WorkContextPolicy`
  
- [ ] 删除 `MeetingModeOptions` 及相关类型
  - 文件：`src/Madorin.AI.Runtime.Contracts/Selection/ModeOptions.cs`
  - 删除：`MeetingModeOptions`、`MeetingPolicy`、`MeetingContextPolicy`、`MeetingSelectorPolicyOptions`

#### 1.2 Orchestrator 删除
- [ ] 删除整个 Work 模式目录
  - 路径：`src/Madorin.AI.Runtime.Modes.Work/`
  - 包含：`WorkModeOrchestrator.cs` 及所有支持文件
  
- [ ] 删除整个 Meeting 模式目录
  - 路径：`src/Madorin.AI.Runtime.Modes.Meeting/`
  - 包含：`MeetingModeOrchestrator.cs` 及所有支持文件

#### 1.3 持久化层删除
- [ ] 删除 `SqliteWorkRepository`
  - 文件：`src/Madorin.AI.Runtime.Persistence.Sqlite/SqliteWorkRepository.cs`
  - 同时删除相关数据库表 schema
  
- [ ] 删除 `SqliteMeetingRepository`
  - 文件：类似路径
  - 同时删除相关数据库表 schema

#### 1.4 内置工具清理
- [ ] 删除 Work 模式内置工具
  - 目录：`src/Madorin.AI.Runtime.Tools.Builtin/Work/`
  - 工具：`mcli_work_set_plan`、`mcli_work_dispatch_step`、`mcli_ask_user` 等
  
- [ ] 删除 Meeting 模式内置工具
  - 目录：`src/Madorin.AI.Runtime.Tools.Builtin/Meeting/`
  - 工具：`mcli_meeting_*` 系列

- [ ] 从 `BuiltinToolRegistry` 移除注册
  - 文件：`src/Madorin.AI.Runtime.Tools.Builtin/BuiltinToolRegistry.cs`

#### 1.5 契约序列化清理
- [ ] 从 `RuntimeJsonContext` 移除相关类型
  - 文件：`src/Madorin.AI.Runtime.Contracts/Serialization/RuntimeJsonContext.cs`
  - 删除所有 `[JsonSerializable(typeof(Work*))]` 和 `[JsonSerializable(typeof(Meeting*))]`

---

### Day 2：依赖修复与测试清理

#### 2.1 Server 层修复
- [ ] 修复 `RuntimeOrchestrator` 中的模式选择逻辑
  - 文件：`src/Madorin.AI.Runtime.Server/RuntimeOrchestrator.cs`
  - 删除 Work/Meeting 分支，只保留 Expert

- [ ] 修复 Controller 中的模式路由
  - 文件：`src/Madorin.AI.Runtime.Server/Controllers/SessionsController.cs`
  - 删除 `/work` 和 `/meeting` 端点（如果有）

#### 2.2 CLI 清理
- [ ] 删除 CLI 子命令
  - 文件：`src/Madorin.AI.Runtime.Cli/Commands/WorkCommand.cs`（如果存在）
  - 文件：`src/Madorin.AI.Runtime.Cli/Commands/MeetingCommand.cs`（如果存在）
  - 只保留 `ExpertCommand.cs`

- [ ] 更新 CLI 帮助文本
  - 文件：`src/Madorin.AI.Runtime.Cli/Program.cs`
  - 移除 work/meeting 相关描述

#### 2.3 测试清理
- [ ] 删除 Work 模式单元测试
  - 目录：`tests/Madorin.AI.Runtime.Modes.Work.Tests/`
  
- [ ] 删除 Meeting 模式单元测试
  - 目录：`tests/Madorin.AI.Runtime.Modes.Meeting.Tests/`
  
- [ ] 删除集成测试中的 Work/Meeting 场景
  - 文件：搜索所有包含 `RuntimeMode.Work` 或 `RuntimeMode.Meeting` 的测试

#### 2.4 验证构建
- [ ] 清理编译错误
  ```bash
  dotnet build
  ```
  
- [ ] 清理警告（CS0618 废弃警告等）
  
- [ ] 运行剩余测试确保无回归
  ```bash
  dotnet test
  ```

---

### Day 3：文档更新

#### 3.1 删除模式文档
- [ ] 删除 Work 模式文档
  - `docs/使用文档/07-Work模式使用指南.md`
  - `docs/开发文档/17-Work模式契约.md`
  - `docs/工作&会议/Work模式重构总结报告.md`
  
- [ ] 删除 Meeting 模式文档
  - `docs/开发文档/16-Meeting模式契约.md`
  - 相关策划文档

#### 3.2 更新核心文档
- [ ] 更新 README.md
  - 移除 Work/Meeting 功能描述
  - 突出 Expert 模式的全功能控制
  
- [ ] 更新 CHANGELOG.md
  - 添加 BREAKING CHANGE 条目
  - 说明移除原因和迁移建议
  
- [ ] 创建迁移指南
  - 新建：`docs/迁移指南/从Work模式迁移到Expert.md`
  - 新建：`docs/迁移指南/从Meeting模式迁移到Expert.md`

#### 3.3 示例清理
- [ ] 删除 Work/Meeting 示例代码
  - 目录：`examples/work-mode/`
  - 目录：`examples/meeting-mode/`

---

## Phase 2：Expert 请求控制增强（3-4 天）

### Day 1：协议层扩展

#### 2.1 创建 ExpertRequestControl
- [ ] 新建文件：`src/Madorin.AI.Runtime.Contracts/Expert/ExpertRequestControl.cs`
  ```csharp
  public sealed record ExpertRequestControl
  {
      public float? Temperature { get; init; }
      public int? MaxTokens { get; init; }
      public float? TopP { get; init; }
      public int? TopK { get; init; }
      public string[]? Stop { get; init; }
      public float? FrequencyPenalty { get; init; }
      public float? PresencePenalty { get; init; }
      public string? ResponseFormat { get; init; }
      public string? ResponseSchemaJson { get; init; }
      public bool? Stream { get; init; }
      public string? ReasoningEffort { get; init; }
      public int? Seed { get; init; }
  }
  ```

#### 2.2 扩展 ExpertModeOptions
- [ ] 修改文件：`src/Madorin.AI.Runtime.Contracts/Selection/ModeOptions.cs`
  - 添加 `RequestControl` 属性
  - 更新构造函数

#### 2.3 扩展 RuntimeProviderRequest
- [ ] 修改文件：`src/Providers/Madorin.AI.Runtime.Providers.Abstractions/RuntimeProviderRequest.cs`
  - 添加新参数：`TopP`、`TopK`、`Stop`、`FrequencyPenalty`、`PresencePenalty`、`Seed`、`ReasoningEffort`

#### 2.4 注册序列化
- [ ] 更新 `RuntimeJsonContext.cs`
  ```csharp
  [JsonSerializable(typeof(ExpertRequestControl))]
  ```

---

### Day 2：Orchestrator 传参

#### 2.5 应用 RequestControl
- [ ] 修改 `ExpertModeOrchestrator.RunAsync`（约 L523）
  ```csharp
  var providerRequest = new RuntimeProviderRequest(
      invocationId,
      expert.Agent.AgentId,
      providerId,
      modelId,
      [.. providerMessages],
      Tools: tools,
      Temperature: expert.RequestControl?.Temperature,
      MaxTokens: expert.RequestControl?.MaxTokens,
      TopP: expert.RequestControl?.TopP,
      TopK: expert.RequestControl?.TopK,
      Stop: expert.RequestControl?.Stop,
      FrequencyPenalty: expert.RequestControl?.FrequencyPenalty,
      PresencePenalty: expert.RequestControl?.PresencePenalty,
      Seed: expert.RequestControl?.Seed,
      StructuredOutput: CreateStructuredOutput(expert.RequestControl),
      CancellationToken: ct,
      Skills: CreateSkillsContext(snapshot, runId, sessionId)
  );
  ```

#### 2.6 实现辅助方法
- [ ] 添加 `CreateStructuredOutput` 方法
  ```csharp
  private static RuntimeStructuredOutput? CreateStructuredOutput(ExpertRequestControl? control)
  {
      if (control?.ResponseFormat != "json")
          return null;
      
      var schema = string.IsNullOrWhiteSpace(control.ResponseSchemaJson)
          ? JsonDocument.Parse("{\"type\":\"object\"}").RootElement
          : JsonDocument.Parse(control.ResponseSchemaJson).RootElement;
      
      return new RuntimeStructuredOutput("response", schema);
  }
  ```

#### 2.7 参数验证
- [ ] 添加参数范围验证
  ```csharp
  private static void ValidateRequestControl(ExpertRequestControl control)
  {
      if (control.Temperature is < 0 or > 2)
          throw new ArgumentOutOfRangeException(nameof(control.Temperature));
      if (control.TopP is < 0 or > 1)
          throw new ArgumentOutOfRangeException(nameof(control.TopP));
      // ... 其他验证
  }
  ```

---

### Day 3：Provider 适配

#### 2.8 OpenAI Provider
- [ ] 映射新参数到 OpenAI API
  - 文件：`src/Providers/Madorin.AI.Runtime.Providers.OpenAI.Worker/OpenAIProviderAdapter.cs`
  ```csharp
  var openAiRequest = new ChatCompletionRequest
  {
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

#### 2.9 Anthropic Provider
- [ ] 映射新参数到 Claude API
  - 文件：`src/Providers/Madorin.AI.Runtime.Providers.Anthropic.Worker/AnthropicProviderAdapter.cs`
  - 注意：Claude 不支持 FrequencyPenalty、PresencePenalty、Seed
  - 记录警告日志并忽略不支持的参数

#### 2.10 Aliyun Provider
- [ ] 映射新参数到通义千问 API
  - 文件：`src/Providers/Madorin.AI.Runtime.Providers.Aliyun.Worker/AliyunProviderAdapter.cs`

---

### Day 4：CLI 参数

#### 2.11 添加 CLI 选项
- [ ] 修改 `ExpertCommand.cs`
  ```csharp
  [Option("--temperature", Description = "采样温度 (0.0-2.0)")]
  public float? Temperature { get; set; }
  
  [Option("--max-tokens", Description = "最大生成 token 数")]
  public int? MaxTokens { get; set; }
  
  [Option("--response-format", Description = "响应格式: text | json")]
  public string? ResponseFormat { get; set; }
  
  [Option("--response-schema-file", Description = "JSON Schema 文件路径")]
  public string? ResponseSchemaFile { get; set; }
  
  [Option("--no-stream", Description = "禁用流式输出")]
  public bool NoStream { get; set; }
  
  [Option("--top-p", Description = "Top-P 采样 (0.0-1.0)")]
  public float? TopP { get; set; }
  
  [Option("--top-k", Description = "Top-K 采样")]
  public int? TopK { get; set; }
  
  [Option("--frequency-penalty", Description = "频率惩罚 (-2.0 到 2.0)")]
  public float? FrequencyPenalty { get; set; }
  
  [Option("--presence-penalty", Description = "存在惩罚 (-2.0 到 2.0)")]
  public float? PresencePenalty { get; set; }
  
  [Option("--stop", Description = "停止序列（可多次指定）")]
  public string[]? Stop { get; set; }
  
  [Option("--seed", Description = "随机种子（确定性生成）")]
  public int? Seed { get; set; }
  ```

#### 2.12 构建 RequestControl
- [ ] 在命令执行时组装
  ```csharp
  var requestControl = new ExpertRequestControl
  {
      Temperature = Temperature,
      MaxTokens = MaxTokens,
      TopP = TopP,
      TopK = TopK,
      Stop = Stop,
      FrequencyPenalty = FrequencyPenalty,
      PresencePenalty = PresencePenalty,
      ResponseFormat = ResponseFormat,
      ResponseSchemaJson = ResponseSchemaFile != null 
          ? await File.ReadAllTextAsync(ResponseSchemaFile)
          : null,
      Stream = NoStream ? false : null,
      Seed = Seed
  };
  ```

#### 2.13 更新帮助文档
- [ ] 运行 `madorin-cli expert run --help` 验证输出

---

## Phase 3：HTTP 非流式接口（2 天）

### Day 1：实现同步端点

#### 3.1 新增 Controller 方法
- [ ] 修改：`src/Madorin.AI.Runtime.Server/Controllers/SessionsController.cs`
  ```csharp
  [HttpPost("{sessionId}/runs/sync")]
  public async Task<IActionResult> CreateSyncRunAsync(
      string sessionId,
      [FromBody] NewSessionRunRequest request,
      CancellationToken ct)
  {
      // 强制非流式
      var modifiedRequest = ForceNonStreaming(request);
      
      // 运行并缓冲所有事件
      var events = new List<RuntimeEventEnvelope>();
      await foreach (var envelope in _orchestrator.RunAsync(modifiedRequest, ct))
      {
          events.Add(envelope);
          if (IsTerminalEvent(envelope.MessageType))
              break;
      }
      
      // 提取最终内容
      return Ok(new
      {
          runId = ExtractRunId(events),
          status = ExtractFinalStatus(events),
          content = ExtractFinalContent(events),
          usage = ExtractUsage(events)
      });
  }
  ```

#### 3.2 实现辅助方法
- [ ] `ForceNonStreaming`
- [ ] `ExtractRunId`
- [ ] `ExtractFinalStatus`
- [ ] `ExtractFinalContent`
- [ ] `ExtractUsage`
- [ ] `IsTerminalEvent`

#### 3.3 超时控制
- [ ] 添加可配置的默认超时（300s）
- [ ] 支持客户端通过 Header 指定超时

---

### Day 2：测试与文档

#### 3.4 单元测试
- [ ] 测试同步端点返回格式
- [ ] 测试超时行为
- [ ] 测试错误场景（失败、取消）

#### 3.5 集成测试
- [ ] 对比流式与非流式结果一致性
- [ ] 测试大响应的内存占用

#### 3.6 API 文档
- [ ] 更新 OpenAPI/Swagger 定义
- [ ] 添加示例 curl 命令
  ```bash
  curl -X POST http://localhost:5000/api/v1/sessions/{sessionId}/runs/sync \
    -H "Content-Type: application/json" \
    -d '{
      "mode": "expert",
      "selection": {...},
      "initialInput": [{"type":"text","text":"Hello"}]
    }'
  ```

---

## Phase 4：小型计划功能完善（3-4 天）

### Day 1：数据结构与存储

#### 4.1 创建枚举
- [ ] 新建：`src/Madorin.AI.Runtime.Contracts/Expert/ExpertPlanEnums.cs`
  ```csharp
  public enum ExpertPlanStatus { Pending, Running, Paused, Completed, Failed, Cancelled }
  public enum ExpertStepStatus { Pending, Running, Completed, Failed, Skipped }
  ```

#### 4.2 创建数据结构
- [ ] 新建：`src/Madorin.AI.Runtime.Contracts/Expert/ExpertPlan.cs`
  - `ExpertPlan`
  - `ExpertPlanStep`
  - `CreateExpertPlanRequest`
  - `ExpertPlanStepDraft`

#### 4.3 创建事件
- [ ] 新建：`src/Madorin.AI.Runtime.Contracts/Expert/ExpertPlanEvents.cs`
  - `ExpertPlanCreatedEvent`
  - `ExpertStepStatusChangedEvent`
  - `ExpertPlanStatusChangedEvent`

#### 4.4 文件存储
- [ ] 新建：`src/Madorin.AI.Runtime.Modes.Expert/ExpertPlanFileStore.cs`
  ```csharp
  internal sealed class ExpertPlanFileStore
  {
      private readonly string _workspaceRoot;
      
      public async Task<ExpertPlan> SaveAsync(ExpertPlan plan, CancellationToken ct)
      {
          var dir = Path.Combine(_workspaceRoot, ".madorin", "plans");
          Directory.CreateDirectory(dir);
          
          var filename = $"{plan.CreatedAt:yyyyMMdd-HHmmss}-{plan.Status.ToString().ToLowerInvariant()}.json";
          var path = Path.Combine(dir, filename);
          
          var json = JsonSerializer.Serialize(plan, RuntimeJsonContext.Default.ExpertPlan);
          await File.WriteAllTextAsync(path, json, ct);
          
          return plan;
      }
      
      public async Task<ExpertPlan?> RestoreActiveAsync(CancellationToken ct)
      {
          var dir = Path.Combine(_workspaceRoot, ".madorin", "plans");
          if (!Directory.Exists(dir)) return null;
          
          var runningFiles = Directory.GetFiles(dir, "*-running.json")
              .Concat(Directory.GetFiles(dir, "*-pending.json"))
              .OrderByDescending(f => f)
              .ToArray();
          
          if (runningFiles.Length == 0) return null;
          
          var json = await File.ReadAllTextAsync(runningFiles[0], ct);
          return JsonSerializer.Deserialize(json, RuntimeJsonContext.Default.ExpertPlan);
      }
  }
  ```

#### 4.5 计划管理器
- [ ] 新建：`src/Madorin.AI.Runtime.Modes.Expert/ExpertPlanManager.cs`
  ```csharp
  internal sealed class ExpertPlanManager
  {
      private readonly ExpertPlanFileStore _fileStore;
      private readonly SqliteEventOutbox _outbox;
      
      public ExpertPlan? ActivePlan { get; private set; }
      
      public async Task<ExpertPlan> CreatePlanAsync(
          string sessionId,
          string runId,
          CreateExpertPlanRequest request,
          CancellationToken ct)
      {
          if (request.Steps.Length < 2 || request.Steps.Length > 15)
              throw new ArgumentException("Plan must have 2-15 steps.");
          
          var plan = new ExpertPlan(
              PlanId: $"plan_{DateTimeOffset.UtcNow:yyyyMMddHHmmssff}",
              SessionId: sessionId,
              Goal: request.Goal,
              Status: ExpertPlanStatus.Pending,
              Steps: request.Steps.Select((draft, i) => new ExpertPlanStep(
                  StepId: draft.StepId,
                  Title: draft.Title,
                  Description: draft.Description,
                  AgentName: draft.AgentName,
                  Status: ExpertStepStatus.Pending,
                  Progress: 0,
                  StartedAt: null,
                  CompletedAt: null,
                  Result: null,
                  ErrorMessage: null
              )).ToArray(),
              CreatedAt: DateTimeOffset.UtcNow,
              UpdatedAt: DateTimeOffset.UtcNow
          );
          
          await _fileStore.SaveAsync(plan, ct);
          ActivePlan = plan;
          
          return plan;
      }
      
      public async Task<ExpertPlan> UpdateStepStatusAsync(
          string stepId,
          string runId,
          ExpertStepStatus newStatus,
          int? progress = null,
          string? notes = null,
          CancellationToken ct)
      {
          if (ActivePlan == null)
              throw new InvalidOperationException("No active plan.");
          
          var stepIndex = Array.FindIndex(ActivePlan.Steps, s => s.StepId == stepId);
          if (stepIndex < 0)
              throw new KeyNotFoundException($"Step {stepId} not found.");
          
          var updatedSteps = ActivePlan.Steps.ToArray();
          var oldStep = updatedSteps[stepIndex];
          
          updatedSteps[stepIndex] = oldStep with
          {
              Status = newStatus,
              Progress = progress ?? oldStep.Progress,
              StartedAt = newStatus == ExpertStepStatus.Running && oldStep.StartedAt == null 
                  ? DateTimeOffset.UtcNow 
                  : oldStep.StartedAt,
              CompletedAt = newStatus == ExpertStepStatus.Completed 
                  ? DateTimeOffset.UtcNow 
                  : oldStep.CompletedAt,
              Result = notes ?? oldStep.Result
          };
          
          var updatedPlan = ActivePlan with
          {
              Steps = updatedSteps,
              Status = DeterminePlanStatus(updatedSteps),
              UpdatedAt = DateTimeOffset.UtcNow
          };
          
          await _fileStore.SaveAsync(updatedPlan, ct);
          ActivePlan = updatedPlan;
          
          return updatedPlan;
      }
      
      private static ExpertPlanStatus DeterminePlanStatus(ExpertPlanStep[] steps)
      {
          if (steps.All(s => s.Status == ExpertStepStatus.Completed))
              return ExpertPlanStatus.Completed;
          if (steps.Any(s => s.Status == ExpertStepStatus.Failed))
              return ExpertPlanStatus.Failed;
          if (steps.Any(s => s.Status == ExpertStepStatus.Running))
              return ExpertPlanStatus.Running;
          return ExpertPlanStatus.Pending;
      }
      
      public async Task<ExpertPlan?> RestoreActivePlanAsync(CancellationToken ct)
      {
          ActivePlan = await _fileStore.RestoreActiveAsync(ct);
          return ActivePlan;
      }
      
      public async Task AbandonPlanAsync(string? reason, CancellationToken ct)
      {
          if (ActivePlan == null) return;
          
          var abandoned = ActivePlan with
          {
              Status = ExpertPlanStatus.Cancelled,
              UpdatedAt = DateTimeOffset.UtcNow
          };
          
          await _fileStore.SaveAsync(abandoned, ct);
          ActivePlan = null;
      }
  }
  ```

---

### Day 2：内置工具

#### 4.6 创建工具目录
- [ ] 新建目录：`src/Madorin.AI.Runtime.Tools.Builtin/Expert/`

#### 4.7 create_plan 工具
- [ ] 新建：`BuiltinExpertCreatePlanTool.cs`
  ```csharp
  public sealed class BuiltinExpertCreatePlanTool : IBuiltinTool
  {
      public string ToolId => "mcli_expert_create_plan";
      public string Name => "create_plan";
      public string Description => "创建一个 3-15 步的执行计划（适用于复杂任务）";
      
      public async Task<ToolResult> ExecuteAsync(
          ToolInvocation invocation,
          ToolExecutionContext context,
          CancellationToken ct)
      {
          var args = JsonSerializer.Deserialize<CreatePlanArgs>(
              invocation.ArgumentsJson,
              RuntimeJsonContext.Default.CreatePlanArgs);
          
          // 从 context 中获取 ExpertPlanManager
          var planManager = context.GetService<ExpertPlanManager>();
          
          var request = new CreateExpertPlanRequest(
              args.Goal,
              args.Steps.Select(s => new ExpertPlanStepDraft(
                  StepId: $"step-{Array.IndexOf(args.Steps, s) + 1}",
                  Title: s.Title,
                  Description: s.Description,
                  AgentName: s.AgentName
              )).ToArray()
          );
          
          var plan = await planManager.CreatePlanAsync(
              invocation.SessionId,
              invocation.RunId,
              request,
              ct);
          
          var outputJson = SerializePlanResult(plan);
          return ToolResult.Success(outputJson);
      }
  }
  
  file record CreatePlanArgs(string Goal, StepDraft[] Steps);
  file record StepDraft(string Title, string? Description, string? AgentName);
  ```

#### 4.8 execute_step 工具
- [ ] 新建：`BuiltinExpertExecuteStepTool.cs`

#### 4.9 get_plan 工具
- [ ] 新建：`BuiltinExpertGetPlanTool.cs`

#### 4.10 update_step_status 工具
- [ ] 新建：`BuiltinExpertUpdateStepStatusTool.cs`

#### 4.11 abandon_plan 工具
- [ ] 新建：`BuiltinExpertAbandonPlanTool.cs`

#### 4.12 注册到 BuiltinToolRegistry
- [ ] 修改：`BuiltinToolRegistry.cs`
  ```csharp
  registry.Register(new BuiltinExpertCreatePlanTool());
  registry.Register(new BuiltinExpertExecuteStepTool());
  registry.Register(new BuiltinExpertGetPlanTool());
  registry.Register(new BuiltinExpertUpdateStepStatusTool());
  registry.Register(new BuiltinExpertAbandonPlanTool());
  ```

---

### Day 3：System Prompt 注入

#### 4.13 初始化 PlanManager
- [ ] 在 `ExpertModeOrchestrator` 构造时初始化
  ```csharp
  private readonly ExpertPlanManager _planManager;
  
  public ExpertModeOrchestrator(
      IRuntimeProviderAdapter provider,
      ConversationStore store,
      // ...
      string workspaceRoot,
      // ...
  )
  {
      _planManager = new ExpertPlanManager(workspaceRoot, _outbox);
  }
  ```

#### 4.14 修改 System Prompt 组装逻辑
- [ ] 在 `RunAsync` 中（约 L165）
  ```csharp
  var baseSystemPrompt = expert.Agent.SystemPrompt;
  var withSubAgents = AppendAvailableSubAgentsList(
      baseSystemPrompt,
      expert.Agent,
      expert.AvailableAgents);
  
  // ✅ 新增：注入计划进度
  var enhancedSystemPrompt = await PrepareSystemPromptWithPlanAsync(
      withSubAgents,
      ct);
  
  var agentContext = _agentContextComposer is null
      ? AgentContextComposer.WithoutMemory(enhancedSystemPrompt)
      : await _agentContextComposer.ComposeAsync(enhancedSystemPrompt, ct)
          .ConfigureAwait(false);
  ```

#### 4.15 实现注入方法
- [ ] 添加 `PrepareSystemPromptWithPlanAsync`
  ```csharp
  private async Task<string> PrepareSystemPromptWithPlanAsync(
      string baseSystemPrompt,
      CancellationToken ct)
  {
      // 尝试恢复活跃计划
      if (_planManager.ActivePlan == null)
      {
          await _planManager.RestoreActivePlanAsync(ct).ConfigureAwait(false);
      }
      
      if (_planManager.ActivePlan == null)
          return baseSystemPrompt;
      
      return AppendPlanProgressReminder(baseSystemPrompt, _planManager.ActivePlan);
  }
  
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
              sb.AppendLine($"   └─ {step.Result}");
          if (!string.IsNullOrEmpty(step.ErrorMessage))
              sb.AppendLine($"   └─ ⚠️ {step.ErrorMessage}");
      }
      
      sb.AppendLine();
      
      var nextStep = plan.Steps.FirstOrDefault(s => s.Status == ExpertStepStatus.Pending);
      if (nextStep != null)
      {
          sb.AppendLine("### 🎯 Next Action:");
          sb.AppendLine($"Execute **Step {nextStep.StepId}** using `mcli_expert_execute_step`:");
          sb.AppendLine("```");
          sb.AppendLine($"mcli_expert_execute_step(stepId: \"{nextStep.StepId}\")");
          sb.AppendLine("```");
          if (!string.IsNullOrEmpty(nextStep.AgentName))
          {
              sb.AppendLine($"This step should be delegated to agent: **{nextStep.AgentName}**");
          }
      }
      else if (plan.Steps.All(s => s.Status == ExpertStepStatus.Completed))
      {
          sb.AppendLine("### ✨ All Steps Completed");
          sb.AppendLine("Provide a comprehensive final summary.");
      }
      
      return baseSystemPrompt + sb.ToString();
  }
  
  private static int CalculateOverallProgress(ExpertPlanStep[] steps)
  {
      if (steps.Length == 0) return 0;
      var totalWeight = steps.Length * 100;
      var completedWeight = steps.Sum(s => 
          s.Status == ExpertStepStatus.Completed ? 100 : s.Progress);
      return (int)Math.Round(completedWeight * 100.0 / totalWeight);
  }
  ```

---

### Day 4：测试与优化

#### 4.16 单元测试
- [ ] 测试计划创建
- [ ] 测试步骤状态更新
- [ ] 测试文件持久化
- [ ] 测试计划恢复

#### 4.17 集成测试
- [ ] 端到端测试：创建 → 执行 → 完成
- [ ] 测试跨会话恢复
- [ ] 测试计划放弃

#### 4.18 性能测试
- [ ] 测试 15 步计划的加载性能
- [ ] 测试 System Prompt 注入的开销

---

## Phase 5：文档与示例（2 天）

### Day 1：使用文档

#### 5.1 核心指南
- [ ] 新建：`docs/使用文档/Expert模式完整指南.md`
  - Expert 模式概述
  - 基础用法
  - 子 Agent 委派
  - 并行作业
  - 小型计划功能

#### 5.2 参数详解
- [ ] 新建：`docs/使用文档/请求控制参数详解.md`
  - 所有 RequestControl 字段说明
  - Provider 支持矩阵
  - 最佳实践

#### 5.3 计划功能指南
- [ ] 新建：`docs/使用文档/小型计划功能使用指南.md`
  - 何时使用计划
  - 工具说明
  - 文件格式
  - 恢复与续跑

---

### Day 2：示例代码

#### 5.4 示例 1：JSON 结构化输出
- [ ] 新建：`examples/expert-json-output/`
  - schema.json
  - run.sh
  - README.md

#### 5.5 示例 2：高温度创意生成
- [ ] 新建：`examples/expert-creative/`

#### 5.6 示例 3：非流式同步请求
- [ ] 新建：`examples/expert-sync-http/`

#### 5.7 示例 4：使用计划功能
- [ ] 新建：`examples/expert-planning/`
  - 展示完整的计划创建、执行、恢复流程

---

## 🚀 发布检查清单

### 代码质量
- [ ] 所有单元测试通过
- [ ] 所有集成测试通过
- [ ] 代码覆盖率 > 80%
- [ ] 无编译警告

### 文档完整性
- [ ] README 更新
- [ ] CHANGELOG 更新（标注 BREAKING CHANGE）
- [ ] API 文档更新
- [ ] 所有新功能有使用文档

### 性能验证
- [ ] AOT 编译体积未显著增加（< 5%）
- [ ] 运行时性能无回归（< 5%）
- [ ] 内存占用未显著增加（< 10%）

### 迁移准备
- [ ] 迁移指南完成
- [ ] 提供 Work → Expert 示例
- [ ] 提供 Meeting → Expert 示例

---

## 📝 备注

### 风险缓解
1. **破坏性变更**：在 CHANGELOG 顶部用大号警告标注
2. **用户反馈**：预留 1 周收集反馈，准备快速修复
3. **回滚方案**：保留上一个稳定版本的 tag

### 后续优化
- [ ] Provider 参数支持矩阵文档
- [ ] 性能基准测试报告
- [ ] 计划模板库（常见任务预定义）

---

**执行状态**：⏳ 待开始

**预计完成**：2025-10-19（14 天后）

**当前进度**：Phase 1 - Day 1（0%）
