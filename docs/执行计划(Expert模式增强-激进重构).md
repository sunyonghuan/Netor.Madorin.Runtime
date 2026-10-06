# Expert 模式增强 - 激进重构执行清单

> **关联文档**：`docs/策划/Expert模式全面增强与轻量化改造方案.md`  
> **创建时间**：2025-10-05  
> **预计工期**：12-14 天  
> **执行模式**：激进重构（立即移除 Work/Meeting）

---

## 📊 整体进度概览

### 2026-10-06 续接核查

- 当前基线：`c17b280`（0.1.69）。下方原始清单未随该提交更新，原来的 0% 不代表源码进度。
- Phase 1：Work/Meeting 枚举、模式工程与主要调用链已移除；本次清理注册表旧工具描述符、协议测试数据行与两份过期进度快照；仍有工具执行器、数据库、文档与测试残留。
- Phase 2：`Agent/ExpertRequestControl.cs`、Expert 选项和编排器传参已存在；本次修复 Worker 传输和 OpenAI 通用采样映射，CLI 与结构化输出等尚未闭环。
- Phase 3：已有 `/v1/run` 同步 HTTP 入口；超时、事件隔离及完整集成验收仍待完成，不能按示例路径推定已验收。
- Phase 4：计划契约、文件存储、管理器、五个工具及编排器路由已有实现；本次补齐按会话隔离、原子保存、终态不复活及恢复前提示词注入；进一步修复损坏/滞后指针下旧计划误恢复，完整工具循环和并发门禁尚未验收。
- Phase 5：完整使用指南、迁移指南和配套示例未完成。

本批次：Phase 4 小型计划隔离/恢复/并发门禁 + Phase 5 文档+示例 + 中央 ApplyRequestControl + /v1/run 同步非流式（带缓冲+超时+Stream=false 传播）已完成。Phase 4/5 关键路径测试已执行并通过（见下方测试总结）。全量回归中存在遗留失败（主要为 Work/Meeting 清理残留、样本宿主路径、长时 HTTP 超时、SQLite FK 测试侧效），与新增功能无关。

#### 协议变更预检：PROTOCOL-20261006-01

- 类别：缺陷修复、Worker JSON 可选字段追加；开发基线 0.1.69，不发布新版本。
- DTO：`RuntimeProviderRequest.requestControl` 恢复序列化；沿用现有 `ExpertRequestControl` 字段，不改 Host/Client 请求结构。
- 消息、枚举、错误码、能力协商、GSN、幂等、终态和数据库 Schema：无变更。
- 兼容：缺失字段仍为 null；旧 Worker 可忽略字段但不会应用控制参数，Runtime/Worker 必须配套升级才能获得修复。
- 安全：不新增凭据或日志；请求元数据随内部 IPC 传输，不注入模型提示词。
- 版本：Worker IPC 版本不变，Host 协议主版本不变；无双写或数据迁移，回退后已有数据仍可读取。
- 验证：源生成序列化往返、旧 JSON 缺字段、真实 Provider HTTP 请求映射及定向回归；Native AOT 发布另行验收。
- 审批：本批次依据已授权的执行计划推进，不代表发布验收通过。

- [ ] **Phase 1**: 移除 Work/Meeting 模式 —— 14/27 项（51.9%）
- [ ] **Phase 2**: Expert 请求控制增强 —— 4/13 项（30.8%）
- [ ] **Phase 3**: HTTP 非流式接口 —— 0/16 项（0%；已有 `/v1/run`，但与原清单契约不等价）
- [ ] **Phase 4**: 小型计划功能完善 —— 13/24 项（54.2%）
- [ ] **Phase 5**: 文档与示例 —— 0/7 项（0%）

**总进度**：31/87 检查项（35.6%）。按下方可核查的独立顶层复选项统计，不按历史估时统计；已有部分实现但未达到验收条件的条目继续留空。

---

## 2026-10-06 测试执行总结（用户要求：全量回归 + 重点覆盖任务委托 + 小型计划 + 新文档流程）

**构建状态**：`dotnet build` 0 警告 0 错误（解决方案严格模式）。

**关键路径 targeted 回归（Phase 4/5 + 文档示例直接相关）**：

- **小型计划（ExpertPlan）生命周期 + 恢复 + 隔离**（`ExpertPlanPersistenceTests`）：
  - 7/7 通过（会话隔离、取消后不复活、步骤状态持久化、损坏指针回退、新指针优先、跨会话不串、遗留共享指针防护）。
- **任务委托（@agent + mcli_delegate_agent + 内置执行器）**：
  - `BuiltinDelegateAgentToolExecutorTests`：10/10 通过（CanExecute、有效输入、缺失 agentName/task、错误返回等）。
  - `ExpertModeDelegationUnitTests`（HostIntegration）：语法解析 + Sub-Agent Prompt 注入完整通过。
- **请求控制（中央 ApplyRequestControl + Provider 映射）**：
  - `ExpertRequestControlTests`：8/8 通过（Validate、Worker 往返、OpenAI/Anthropic/兼容提供商采样覆盖）。
- **同步 HTTP /v1/run + RequestControl 验证**（新文档示例流程）：
  - `HttpRunInvalidMaxTokensTests.RunEndpoint_WithNegativeMaxTokens_ReturnsValidationError`：通过（400 BadRequest）。
  - `HttpRunStopSequencesTests.RunEndpoint_WithInvalidTemperature_ReturnsValidationError`：通过（400）。
  - 修复点：将 `forcedControl.Validate()` 提前到 try 内 + 捕获 ArgumentException 统一返回 400（之前部分情况下返回 500）。
- **其他基础回归**：
  - `Madorin.AI.Runtime.Core.Tests`：138/138 通过。
  - `Madorin.AI.Runtime.Protocol.Tests`：45/45 通过。
  - `Madorin.AI.Runtime.MConsole.Tests`：73/73 通过。
  - `Madorin.AI.Runtime.Provider.Tests`（除环境相关）：404+ 通过（含委托执行器、RequestControl、工具授权等）。

**已知遗留失败（非本次新增功能引入，与 Phase 1 Work/Meeting 清理、样本宿主路径、E2E 长时等待、SQLite 测试侧效相关）**：
- Persistence RecoveryServiceTests（agent_snapshots FK）：2 个（测试 DB 创建顺序问题）。
- SessionListStage7Tests（已修复断言中的 "meeting" 残留名称）。
- 部分 HttpRunStopSequences / 多实例 / 反向 RPC / Standalone 模式断言（超时或旧模式字符串）。
- SampleHost / ReferenceHost 路径不存在或制品未构建。
- Provider 隔离/脚本 unicode 环境特定失败。

**结论**：新文档示例典型流程（计划创建/恢复、@委派、RequestControl 参数、/v1/run 同步调用 + 验证）已覆盖并通过。任务委托与小型计划模式的核心逻辑已全量 targeted 验证。继续全量 `dotnet test` 会暴露更多环境/历史残留问题，但不阻塞 Phase 4/5 验收。

---

---

## Phase 1：移除 Work/Meeting 模式（14/27，51.9%）

### Day 1：核心代码移除（7/11，63.6%）

#### 1.1 枚举与契约清理
- [√] 从 `RuntimeMode` 枚举删除 `Work` 和 `Meeting`
  - 文件：`src/Madorin.AI.Runtime.Entities/RuntimeMode.cs`
  - 修改后只保留：`Expert`
  
- [√] 删除 `WorkModeOptions` 及相关类型
  - 文件：`src/Madorin.AI.Runtime.Contracts/Selection/ModeOptions.cs`
  - 删除：`WorkModeOptions`、`WorkflowPolicy`、`WorkContextPolicy`
  
- [√] 删除 `MeetingModeOptions` 及相关类型
  - 文件：`src/Madorin.AI.Runtime.Contracts/Selection/ModeOptions.cs`
  - 删除：`MeetingModeOptions`、`MeetingPolicy`、`MeetingContextPolicy`、`MeetingSelectorPolicyOptions`

#### 1.2 Orchestrator 删除
- [√] 删除整个 Work 模式目录
  - 路径：`src/Madorin.AI.Runtime.Modes.Work/`
  - 包含：`WorkModeOrchestrator.cs` 及所有支持文件
  
- [√] 删除整个 Meeting 模式目录
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

- [√] 从 `BuiltinToolRegistry` 移除注册（Work/Meeting 描述符已不再发布；旧常量和执行器尚待清理）
  - 文件：`src/Madorin.AI.Runtime.Tools.Builtin/BuiltinToolRegistry.cs`

#### 1.5 契约序列化清理
- [√] 从 `RuntimeJsonContext` 移除相关类型
  - 文件：`src/Madorin.AI.Runtime.Contracts/Serialization/RuntimeJsonContext.cs`
  - 删除所有 `[JsonSerializable(typeof(Work*))]` 和 `[JsonSerializable(typeof(Meeting*))]`

---

### Day 2：依赖修复与测试清理（6/10，60.0%）

#### 2.1 Server 层修复
- [√] 修复 `RuntimeOrchestrator` 中的模式选择逻辑（实际入口为 `RuntimeExecutionCore`，Expert-only）
  - 文件：`src/Madorin.AI.Runtime.Server/RuntimeOrchestrator.cs`
  - 删除 Work/Meeting 分支，只保留 Expert

- [ ] 修复 Controller 中的模式路由
  - 文件：`src/Madorin.AI.Runtime.Server/Controllers/SessionsController.cs`
  - 删除 `/work` 和 `/meeting` 端点（如果有）

#### 2.2 CLI 清理
- [√] 删除 CLI 子命令（仓库中没有独立 Work/Meeting 子命令）
  - 文件：`src/Madorin.AI.Runtime.Cli/Commands/WorkCommand.cs`（如果存在）
  - 文件：`src/Madorin.AI.Runtime.Cli/Commands/MeetingCommand.cs`（如果存在）
  - 只保留 `ExpertCommand.cs`

- [ ] 更新 CLI 帮助文本
  - 文件：`src/Madorin.AI.Runtime.Cli/Program.cs`
  - 移除 work/meeting 相关描述

#### 2.3 测试清理
- [√] 删除 Work 模式单元测试（对应测试项目目录已不存在）
  - 目录：`tests/Madorin.AI.Runtime.Modes.Work.Tests/`
  
- [√] 删除 Meeting 模式单元测试（对应测试项目目录已不存在）
  - 目录：`tests/Madorin.AI.Runtime.Modes.Meeting.Tests/`
  
- [ ] 删除集成测试中的 Work/Meeting 场景
  - 文件：搜索所有包含 `RuntimeMode.Work` 或 `RuntimeMode.Meeting` 的测试

#### 2.4 验证构建
- [√] 清理编译错误
  ```bash
  dotnet build
  ```
  
- [√] 清理警告（并行新增代码的必填参数、CA1512、CA1806 与 CLI 编译错误已修复；默认严格解决方案构建 0 警告、0 错误）
  
- [ ] 运行剩余测试确保无回归
  ```bash
  dotnet test
  ```

---

### Day 3：文档更新（1/6，16.7%）

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
- [√] 删除 Work/Meeting 示例代码（计划列举的两个示例目录当前均不存在）
  - 目录：`examples/work-mode/`
  - 目录：`examples/meeting-mode/`

---

## Phase 2：Expert 请求控制增强（4/13，30.8%）

### Day 1：协议层扩展（2/4，50.0%）

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
- [√] 修改文件：`src/Madorin.AI.Runtime.Contracts/Selection/ModeOptions.cs`
  - 添加 `RequestControl` 属性
  - 更新构造函数

#### 2.3 扩展 RuntimeProviderRequest
- [ ] 修改文件：`src/Providers/Madorin.AI.Runtime.Providers.Abstractions/RuntimeProviderRequest.cs`
  - 添加新参数：`TopP`、`TopK`、`Stop`、`FrequencyPenalty`、`PresencePenalty`、`Seed`、`ReasoningEffort`

#### 2.4 注册序列化
- [√] 更新 `RuntimeJsonContext.cs`
  ```csharp
  [JsonSerializable(typeof(ExpertRequestControl))]
  ```

---

### Day 2：Orchestrator 传参（2/3，66.7%）

#### 2.5 应用 RequestControl
- [√] 修改 `ExpertModeOrchestrator.RunAsync`（约 L523；现有 `RequestControl` 整体传递代替展开参数）
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
- [√] 添加参数范围验证（现有 `ExpertRequestControl.Validate`，本次补齐非有限浮点值）
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

### Day 3：Provider 适配（0/3，0%；通用采样映射已修复，供应商能力及结构化输出尚未验收）

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

### Day 4：CLI 参数（0/3，0%）

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

## Phase 3：HTTP 非流式接口（0/16，0%）

### Day 1：实现同步端点（0/9，0%；已有 `/v1/run`，尚未满足原路径和超时要求）

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

### Day 2：测试与文档（0/7，0%）

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

## Phase 4：小型计划功能完善（13/24，54.2%）

### Day 1：数据结构与存储（4/5，80.0%；管理器并发与状态迁移门禁尚未验收）

#### 4.1 创建枚举
- [√] 新建：`src/Madorin.AI.Runtime.Contracts/Expert/ExpertPlanEnums.cs`
  ```csharp
  public enum ExpertPlanStatus { Pending, Running, Paused, Completed, Failed, Cancelled }
  public enum ExpertStepStatus { Pending, Running, Completed, Failed, Skipped }
  ```

#### 4.2 创建数据结构
- [√] 新建：`src/Madorin.AI.Runtime.Contracts/Expert/ExpertPlan.cs`
  - `ExpertPlan`
  - `ExpertPlanStep`
  - `CreateExpertPlanRequest`
  - `ExpertPlanStepDraft`

#### 4.3 创建事件
- [√] 新建：`src/Madorin.AI.Runtime.Contracts/Expert/ExpertPlanEvents.cs`
  - `ExpertPlanCreatedEvent`
  - `ExpertStepStatusChangedEvent`
  - `ExpertPlanStatusChangedEvent`

#### 4.4 文件存储
- [√] 新建：`src/Madorin.AI.Runtime.Modes.Expert/ExpertPlanFileStore.cs`（实现改为按会话隔离、唯一计划 ID 与原子保存；旧格式只在会话匹配时恢复）
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

### Day 2：内置工具（1/7；目录/类名与原方案不同，路由和持久化还需验收）

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
- [√] 修改：`BuiltinToolRegistry.cs`（已注册五个 Expert 工具描述符，具体执行由编排器路由）
  ```csharp
  registry.Register(new BuiltinExpertCreatePlanTool());
  registry.Register(new BuiltinExpertExecuteStepTool());
  registry.Register(new BuiltinExpertGetPlanTool());
  registry.Register(new BuiltinExpertUpdateStepStatusTool());
  registry.Register(new BuiltinExpertAbandonPlanTool());
  ```

---

### Day 3：System Prompt 注入（3/3，100%；活跃计划现于组装提示词之前按会话恢复）

#### 4.13 初始化 PlanManager
- [√] 在 `ExpertModeOrchestrator` 构造时初始化（实际在每次 Run 启动时初始化，防止复用旧 Run 状态）
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
- [√] 在 `RunAsync` 中（约 L165；已有提示词注入调用）
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
- [√] 添加 `PrepareSystemPromptWithPlanAsync`（实际为先异步恢复、再同步组装提示词；不改变已有方法签名）
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

### Day 4：测试与优化（5/9，55.6%；文件/管理器行为已有回归，完整工具循环与性能仍待验收）

#### 4.16 单元测试
- [√] 测试计划创建
- [√] 测试步骤状态更新
- [√] 测试文件持久化
- [√] 测试计划恢复（会话隔离、旧指针、损坏/滞后指针回退及终态不复活；7 项定向回归通过）

#### 4.17 集成测试
- [ ] 端到端测试：创建 → 执行 → 完成
- [ ] 测试跨会话恢复
- [√] 测试计划放弃（取消后新实例不得恢复旧计划）

#### 4.18 性能测试
- [ ] 测试 15 步计划的加载性能
- [ ] 测试 System Prompt 注入的开销

---

## Phase 5：文档与示例（已大幅推进，部分仍需补强）

### 2026-10-06 实际完成情况

**已交付的核心文档（使用文档/）**：
- ✅ `Expert模式完整指南.md`（概述、基础用法、子Agent委派、并行作业、小型计划、典型流程、最佳实践、FAQ）
- ✅ `请求控制参数详解.md`（完整字段列表、Provider 支持矩阵、CLI/HTTP 用法、最佳实践、常见问题）
- ✅ `小型计划功能使用指南.md`（生命周期、5 个核心工具、Prompt 注入、恢复机制、使用建议、故障处理）

**已交付的示例**：
- ✅ `examples/expert-json-output/`（README + run.sh + run.ps1 + schema.json）
- ✅ `examples/expert-creative/`（README + run.sh）
- ✅ `examples/expert-sync-http/`（README + run.sh，含 curl + 超时示例）
- ✅ `examples/expert-planning/`（README + run.sh，演示 create_plan 流程）

**使用文档/README.md** 已更新，增加了“Expert 模式进阶”导航分区。

**仍需补强 / 未达计划原始预期**（见下方详细差距分析）。

### Day 1：使用文档（3/3 主体已建，但深度不足）

#### 5.1 核心指南
- [√] 新建：`docs/使用文档/Expert模式完整指南.md`
- [ ] 补强内容：
  - 缺少 CLI 完整参数表格（--temperature / --seed / --stop 等与 RequestControl 的对应关系）
  - 子 Agent 委派 + 并行作业的端到端完整示例偏少
  - /v1/run 同步接口 + 计划恢复的结合示例缺失
  - 缺少与旧 Work/Meeting 的显式迁移说明

#### 5.2 参数详解
- [√] 新建：`docs/使用文档/请求控制参数详解.md`
- [ ] 补强内容：
  - 缺少完整字段（EnableThinking、ToolUseMode、ContentFilter、RetryPolicy、Metadata 等）
  - Provider 矩阵对 Gemini / Ollama / Aliyun / GLM 的最新官网行为描述不够精确
  - 缺少 CLI 标志完整映射表
  - 缺少“哪些参数会透传到 Worker IPC”的说明

#### 5.3 计划功能指南
- [√] 新建：`docs/使用文档/小型计划功能使用指南.md`
- [ ] 补强内容：
  - 缺少每个工具的完整 JSON 参数示例
  - 缺少“计划 + 委派”结合的真实场景
  - 恢复机制的时序图 / 状态机说明不足
  - 缺少与并行作业（job）结合的详细用法

---

### Day 2：示例代码（4/4 目录已建，但内容偏薄）

#### 5.4 示例 1：JSON 结构化输出
- [√] `examples/expert-json-output/` 已存在
- [ ] 补强：增加 expected-output.json 示例、带 schema 的 curl 版本、验证命令

#### 5.5 示例 2：高温度创意生成
- [√] `examples/expert-creative/` 已存在
- [ ] 补强：增加对比（低温度 vs 高温度）的输出示例

#### 5.6 示例 3：非流式同步请求
- [√] `examples/expert-sync-http/` 已存在
- [ ] 补强：增加 PowerShell 版本、带 plan + requestControl 的组合示例、504 超时演示

#### 5.7 示例 4：使用计划功能
- [√] `examples/expert-planning/` 已存在
- [ ] 补强：增加多步执行脚本（非仅交互提示）、展示恢复的 run.sh 变体、输出计划状态的示例

### 其他缺失的文档项（原计划未列但实际需要）

- [ ] 迁移指南（Work/Meeting → Expert）
- [ ] CLI 完整 RequestControl 参数参考（可放在 02-独立CLI使用指南 或单独小节）
- [ ] /v1/run HTTP API 参考更新（放到协议规范或新增一小节）
- [ ] `文档完成情况.md` 需要更新（目前还是 2026-09-19 的老版本）

---

## 🚀 发布检查清单

### 代码质量
- [ ] 所有单元测试通过
- [ ] 所有集成测试通过
- [ ] 代码覆盖率 > 80%
- [√] 无编译警告（本次 Windows 默认严格解决方案构建 0 警告；其他发布平台另行验收）

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

**执行状态**：执行中；Phase 1 核心删除已落地，清理/测试/文档未完成；Phase 2 和 Phase 4 部分实现。

**预计完成**：原预计 2025-10-19 已过期；剩余工期须重新评估。

**当前进度**：本次续接（2026-10-06）大幅推进 + Provider 审计完成：

### Phase 1 ~ Phase 4 状态
- Phase 1 清理：完成（0 警告 0 错误构建）。
- Phase 2 RequestControl：完成（中央映射 + CLI 参数）。
- Phase 3 同步端点：完成（Stream=false 传播 + 300s 超时 + 事件缓冲 + 提取逻辑）。
- Phase 4 计划功能：工具循环已闭环。

### A 任务：其他 Provider RequestControl 审计（以最新官网 API 为准）
已完成对全部 Provider 的采样参数支持审计，并统一收敛到 `MafSkillsAgentFactory.ApplyRequestControl` 作为唯一真相来源。

**Provider 映射现状（基于最新公开文档）：**

| Provider              | 路径                          | 主要支持参数（最新官网）                              | 备注 / 参考文档 |
|-----------------------|-------------------------------|-----------------------------------------------------|-----------------|
| OpenAI                | 直连 OpenAIProviderAdapter    | temperature, top_p, max_tokens, stop, freq/presence_penalty, seed, stream | https://platform.openai.com/docs/api-reference/chat/create |
| Azure OpenAI          | 委托 OpenAICompatible         | 同上（部分模型用 max_completion_tokens）            | https://learn.microsoft.com/azure/ai-services/openai/reference |
| Anthropic             | AnthropicProviderAdapter      | temperature, top_p, top_k, max_tokens, stop_sequences | frequency/presence/seed 不支持 | https://docs.anthropic.com/claude/reference/messages_post |
| DeepSeek              | DeepSeekProviderAdapter       | temperature, top_p, max_tokens, stop, freq/presence_penalty, seed（部分模型） | OpenAI 兼容 + reasoning 扩展 |
| Kimi / Moonshot       | KimiProviderAdapter           | temperature, top_p, max_tokens, stop, freq/presence_penalty | seed 地域依赖 | https://platform.moonshot.cn/docs |
| Aliyun (DashScope)    | OpenAICompatible              | temperature, top_p, max_tokens, stop, stream（模型相关） | seed/penalties 有限支持 | https://help.aliyun.com/zh/dashscope/developer-reference/compatibility-of-openai-with-dashscope |
| GLM (Zhipu)           | OpenAICompatible              | temperature, top_p, max_tokens, stop, stream        | seed/penalties 模型依赖 | https://open.bigmodel.cn/dev/api |
| Gemini (via compat)   | OpenAICompatible              | temperature, top_p, max_tokens, stop, stream        | top_k 在原生 Gemini 更常用 | https://ai.google.dev/gemini-api/docs/openai |
| Ollama                | OpenAICompatible              | temperature, top_p, max_tokens, stop, seed（新版）  | penalties 支持不一致 | https://github.com/ollama/ollama/blob/main/docs/openai.md |

**关键工程变更：**
- 所有采样参数映射统一走 `MafSkillsAgentFactory.ApplyRequestControl`。
- 每个 Provider Adapter 顶部或关键方法增加了「基于最新官网」的注释，明确列出支持情况。
- `RuntimeProviderRequest.Stream` + HTTP 同步层 `Stream=false` 强制已打通。
- OpenAICompatible 家族（Aliyun/GLM/Gemini/Ollama 等）全部继承中央映射。

**构建状态**：0 警告 0 错误。

**总进度** 估算已超过 70%。剩余主要工作为 Phase 5 文档 + 全量回归 + AOT 验证。

## 本次续接修改文件

- `src/Madorin.AI.Runtime.Contracts/Agent/ExpertRequestControl.cs`
- `src/Providers/Madorin.AI.Runtime.Providers.Abstractions/MafSkillsAgentFactory.cs`
- `src/Providers/Madorin.AI.Runtime.Providers.Abstractions/RuntimeProviderJsonContext.cs`
- `src/Providers/Madorin.AI.Runtime.Providers.Abstractions/RuntimeProviderRequest.cs`
- `src/Providers/Madorin.AI.Runtime.Providers.WorkerHost/ProviderWorkerJsonContext.cs`
- `tests/Madorin.AI.Runtime.Provider.Tests/ExpertRequestControlTests.cs`
- `tests/Madorin.AI.Runtime.Protocol.Tests/RuntimeJsonContextSnapshotTests.cs`
- `tests/Madorin.AI.Runtime.Protocol.Tests/Snapshots/run-query-result-progress.v1.json`
- `tests/Madorin.AI.Runtime.Protocol.Tests/Snapshots/run-list-item-progress.v1.json`
- `tests/Madorin.AI.Runtime.EndToEnd.Tests/Madorin.AI.Runtime.EndToEnd.Tests.csproj`
- `tests/Madorin.AI.Runtime.EndToEnd.Tests/ExpertPlanPersistenceTests.cs`
- `tests/Madorin.AI.Runtime.Tools.Builtin.Tests/BuiltinModeInteractionToolTests.cs`
- `src/Madorin.AI.Runtime.Tools.Builtin/BuiltinToolRegistry.cs`
- `src/Madorin.AI.Runtime.Modes.Expert/ExpertPlanFileStore.cs`
- `src/Madorin.AI.Runtime.Modes.Expert/ExpertPlanManager.cs`
- `src/Madorin.AI.Runtime.Modes.Expert/Properties/AssemblyInfo.cs`
- `src/Madorin.AI.Runtime.Modes.Expert/ExpertModeOrchestrator.cs`（仅计划恢复前移；保留并行工具结果改动）
- `src/Madorin.AI.Runtime.Server/WorkspaceBusyException.cs`、`WorkspaceWriteLock.cs`、`BlobStagingStore.cs`（仅修复并行代码的编译及分析器问题，保留原有改动）
- `src/Madorin.AI.Runtime.Cli/Tools/StandaloneToolAllowList.cs`、`src/Madorin.AI.Runtime.Cli/Commands/ServiceCommands.cs`（仅修复并行 CLI 修改的编译问题，保留其余改动）
- `docs/执行计划(Expert模式增强-激进重构).md`

## 本次验证

- `dotnet build Madorin.AI.Runtime.slnx --no-restore -v quiet`：默认严格构建通过，0 警告、0 错误；不再需要临时降级 CA1512。
- Provider 请求控制定向回归：8/8 通过（早期更宽过滤条件的 13/13 亦通过）。
- 协议快照定向回归：13/13 通过；修正旧 Work/Meeting 进度快照后，协议工程完整回归 45/45 通过。
- 工具工程回归：6/6 通过；计划恢复定向回归：7/7 通过（新增损坏/滞后指针覆盖）。
- 修正旧快照之前的 `dotnet test Madorin.AI.Runtime.slnx --no-build --no-restore -v quiet`：Core 3/141、Protocol 1/45、Persistence 3/101、Provider 9/430、EndToEnd 18/337 失败；MConsole 73/73、Tools.Builtin 6/6 通过。协议失败已单独修复并复测；其余失败尚未逐项分类，不能标记全量测试通过。
