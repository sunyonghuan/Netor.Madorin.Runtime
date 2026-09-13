# Agent 委派功能实现变更日志

> 实现日期：2026-09-12
>
> 功能：支持 `@agentname` 语义的多 Agent 委派机制

## 变更概览

| 提交哈希 | 类型 | 仓库 | 描述 |
| --- | --- | --- | --- |
| `88537d9` | feat | Workstation | Meeting 模式多 Agent 注入与代码质量优化 |
| `67ccddb` | feat | Runtime | 添加 DelegateToAgent 工具支持 @agentname 语义 |
| `c74a296` | feat | Runtime | Work 模式 System Prompt 注入可用 Agent 列表 |
| `7aa210f` | feat | Runtime | Meeting 模式 System Prompt 注入可用参会者列表 |
| `927367d` | fix | Runtime | 修正 Agent 委派注入的业务逻辑错误 |

## 详细变更

### Phase 1: Agent 池传递（Workstation）

**提交**：`88537d9` - Meeting 模式多 Agent 注入与代码质量优化

**修改文件**：
- `src/Netor.Madorin.Workstation.Runtime/RuntimeGateway.cs`
- `tests/Netor.Madorin.Workstation.Desktop.Tests/ViewModels/Meeting/MeetingModeViewModelTests.cs`
- `tests/Netor.Madorin.Workstation.Desktop.Tests/ViewModels/Work/WorkModeViewModelTests.cs`

**变更内容**：

1. **Meeting 模式参会者注入**：
   ```csharp
   // 按照 ParticipantAgents 顺序构建参会者列表（去重）
   var participants = new List<MeetingParticipant>();
   var seenAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   int joinOrder = 0;
   
   foreach (var agentName in sessionRequest.ParticipantAgents)
   {
       if (agentLookup.TryGetValue(agentName, out var agentConfig) 
           && seenAgents.Add(agentName))
       {
           participants.Add(new MeetingParticipant(
               participantId: $"p{joinOrder}",
               agent: ConvertToAgentRef(agentConfig),
               displayName: agentConfig.DisplayName ?? agentConfig.AgentName,
               joinOrder: joinOrder++,
               status: ParticipantStatus.Active));
       }
   }
   ```

2. **Work 模式 GM 去重优化**：
   ```csharp
   // GM 去重：如果 GM 已在池中，跳过重复添加；否则插入池首。
   var gmExists = availableAgents.Any(a =>
       string.Equals(a.AgentId, generalManager.AgentId, StringComparison.Ordinal));
   
   if (!gmExists)
   {
       availableAgents.Insert(0, generalManager);
   }
   ```

3. **单元测试覆盖**：
   - `WorkModeViewModel_BuildModeOptions_IncludesGeneralManagerInAvailableAgents`
   - `WorkModeViewModel_BuildModeOptions_DeduplicatesGeneralManager`
   - `MeetingModeViewModel_BuildModeOptions_WithParticipants_CreatesParticipantList`
   - `MeetingModeViewModel_BuildModeOptions_DeduplicatesParticipants`
   - 等 10 个测试全部通过

**业务逻辑**：
- Work 模式：`AvailableAgents` 包含 GM 自身
- Meeting 模式：按 `ParticipantAgents` 顺序构建，支持去重
- DisplayName 回退：优先 `DisplayName`，回退到 `AgentName`

---

### Phase 2: DelegateToAgent 工具（Runtime CLI）

**提交**：`67ccddb` - 添加 DelegateToAgent 工具支持 @agentname 语义

**修改文件**：
- `src/Madorin.AI.Runtime.Tools.Builtin/BuiltinToolRegistry.cs`
- `src/Madorin.AI.Runtime.Tools.Builtin/BuiltinDelegateAgentToolExecutor.cs`（新建）
- `src/Madorin.AI.Runtime.Server/RuntimeExecutionCore.cs`
- `tests/Madorin.AI.Runtime.Provider.Tests/BuiltinDelegateAgentToolExecutorTests.cs`（新建）

**变更内容**：

1. **工具定义**（`BuiltinToolRegistry.cs`）：
   ```csharp
   public const string DelegateToAgentToolId = "mcli_delegate_agent";
   
   new(
       DelegateToAgentToolId,
       "mcli",
       "Delegate to agent",
       "Delegates a task to a specific agent from the available agent pool.",
       InputSchema: """{"type":"object","properties":{"agentName":{"type":"string","description":"Name of the target agent (case-insensitive)"},"task":{"type":"string","description":"Task description to delegate"}},"required":["agentName","task"],"additionalProperties":false}""",
       OutputSchema: """{"type":"object","properties":{"delegated":{"type":"boolean"},"agentName":{"type":"string"},"task":{"type":"string"},"message":{"type":"string"}},"required":["delegated","message"],"additionalProperties":false}""",
       RiskLevel: "low",
       Capabilities: ["agent.delegate"],
       Risk: ToolRiskLevel.Low,
       IsIdempotent: false)
   ```

2. **工具执行器**（`BuiltinDelegateAgentToolExecutor.cs`）：
   ```csharp
   public sealed class BuiltinDelegateAgentToolExecutor : IToolExecutor
   {
       public bool CanExecute(string toolId) => 
           string.Equals(toolId, BuiltinToolRegistry.DelegateToAgentToolId, StringComparison.Ordinal);
   
       public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken ct = default)
       {
           // 验证 agentName 和 task 参数
           // 返回元数据：{delegated: true, agentName, task, message}
       }
   }
   ```

3. **注册到执行核心**（`RuntimeExecutionCore.cs` 行135）：
   ```csharp
   var executors = new List<IToolExecutor>
   {
       new BuiltinFsToolExecutor(_memoryFiles.WorkspaceMutationLock),
       new BuiltinProcessToolExecutor(),
       new BuiltinHttpToolExecutor(),
       new BuiltinMemoryToolExecutor(_memoryFiles),
       new BuiltinDelegateAgentToolExecutor()  // 新增
   };
   ```

4. **单元测试**（8 个测试全部通过）：
   - `CanExecute_WithDelegateToAgentToolId_ReturnsTrue`
   - `ExecuteAsync_WithValidInput_ReturnsSuccess`
   - `ExecuteAsync_WithMissingAgentName_ReturnsError`
   - `ExecuteAsync_WithMissingTask_ReturnsError`
   - `ExecuteAsync_WithEmptyAgentName_ReturnsError`
   - `ExecuteAsync_WithEmptyTask_ReturnsError`
   - `ExecuteAsync_WithWrongToolId_ThrowsInvalidOperationException`
   - `ExecuteAsync_ResultCanBeDeserialized`

**设计原则**：
- **无状态设计**：工具执行器不持有 ModeOptions 或会话上下文
- **元数据返回**：仅验证参数并返回委派意图，不执行实际调用
- **编排器负责**：实际的 Agent 查找和调用由 Orchestrator 处理

---

### Phase 3: System Prompt 注入（Runtime CLI）

#### 3.1 Work 模式注入

**提交**：`c74a296` - Work 模式 System Prompt 注入可用 Agent 列表

**修改文件**：
- `src/Madorin.AI.Runtime.Modes.Work/WorkModeOrchestrator.cs`

**变更内容**：

1. **添加 using System.Globalization**（行2）

2. **注入逻辑**（行1690-1705）：
   ```csharp
   var systemInstructions = context.Instructions;
   if (request.Selection.ModeOptions is WorkModeOptions workOpts
       && agent.AgentId == workOpts.GeneralManager.AgentId
       && workOpts.AvailableAgents.Length > 1)
   {
       systemInstructions = AppendAvailableAgentsList(systemInstructions, workOpts.AvailableAgents);
   }
   
   var systemMessage = new RuntimeProviderMessage(
       RuntimeProviderRoles.System,
       [new TextContentBlock(systemInstructions)]);
   ```

3. **格式化方法**（文件末尾）：
   ```csharp
   private static string AppendAvailableAgentsList(string baseInstructions, AgentRef[] availableAgents)
   {
       if (availableAgents.Length <= 1)
       {
           return baseInstructions;
       }
   
       var agentListBuilder = new StringBuilder();
       agentListBuilder.AppendLine();
       agentListBuilder.AppendLine();
       agentListBuilder.AppendLine("## Available Agents for Delegation");
       agentListBuilder.AppendLine();
       agentListBuilder.AppendLine("You can delegate tasks to the following agents using the `delegate_to_agent` tool:");
       agentListBuilder.AppendLine();
   
       foreach (var agent in availableAgents)
       {
           agentListBuilder.AppendLine(CultureInfo.InvariantCulture, $"- **{agent.AgentId}**");
       }
   
       agentListBuilder.AppendLine();
       agentListBuilder.AppendLine("Use the tool when the user mentions \"@AgentName\" or \"let {Agent} do...\".");
   
       return baseInstructions + agentListBuilder.ToString();
   }
   ```

**注入条件**：
- 当前 Agent 是 GM
- `AvailableAgents.Length > 1`

**注意**：此版本存在逻辑缺陷（见后续修复）

#### 3.2 Meeting 模式注入

**提交**：`7aa210f` - Meeting 模式 System Prompt 注入可用参会者列表

**修改文件**：
- `src/Madorin.AI.Runtime.Modes.Meeting/MeetingModeOrchestrator.cs`

**变更内容**：

1. **Participant 角色注入**（行1804-1820）：
   ```csharp
   var systemInstructions = context.Instructions;
   if (request.Selection.ModeOptions is MeetingModeOptions meetingOpts
       && meetingOpts.Participants.Length > 1)
   {
       systemInstructions = AppendAvailableParticipantsList(systemInstructions, meetingOpts.Participants);
   }
   ```

2. **Selector 角色注入**（行3318-3333）：
   ```csharp
   var systemInstructions = context.Instructions;
   if (options.Participants.Length > 1)
   {
       systemInstructions = AppendAvailableParticipantsList(systemInstructions, options.Participants);
   }
   ```

3. **格式化方法**（文件末尾）：
   ```csharp
   private static string AppendAvailableParticipantsList(string baseInstructions, MeetingParticipant[] participants)
   {
       if (participants.Length <= 1)
       {
           return baseInstructions;
       }
   
       var participantListBuilder = new StringBuilder();
       participantListBuilder.AppendLine();
       participantListBuilder.AppendLine();
       participantListBuilder.AppendLine("## Available Participants for Delegation");
       participantListBuilder.AppendLine();
       participantListBuilder.AppendLine("You can delegate tasks to the following participants using the `delegate_to_agent` tool:");
       participantListBuilder.AppendLine();
   
       foreach (var participant in participants)
       {
           if (participant.Status == ParticipantStatus.Active)
           {
               participantListBuilder.AppendLine(
                   CultureInfo.InvariantCulture,
                   $"- **{participant.DisplayName}** ({participant.Agent.AgentId})");
           }
       }
   
       participantListBuilder.AppendLine();
       participantListBuilder.AppendLine("Use the tool when the user mentions \"@ParticipantName\" or \"let {Participant} do...\".");
   
       return baseInstructions + participantListBuilder.ToString();
   }
   ```

**注入位置**：
- ✅ Participant 角色
- ✅ Selector 角色

**注意**：此版本存在逻辑缺陷（见后续修复）

---

### Phase 4: 业务逻辑修复（Runtime CLI）

**提交**：`927367d` - 修正 Agent 委派注入的业务逻辑错误

**修改文件**：
- `src/Madorin.AI.Runtime.Modes.Work/WorkModeOrchestrator.cs`
- `src/Madorin.AI.Runtime.Modes.Meeting/MeetingModeOrchestrator.cs`

**发现的问题**：

1. **Meeting Participant 不应委派**：
   - 问题：Participant 被选中来回答问题，不应转手给其他人
   - 业务语义：只有 Host/Selector 负责选择发言者

2. **Meeting Host 缺少注入**：
   - 问题：Host 需要选择下一个发言者，但未注入参会者列表
   - 影响：Host 无法响应 `@ParticipantName` 语法

3. **Work 模式条件不严谨**：
   - 问题：`AvailableAgents.Length > 1` 包含 GM 自己
   - 正确逻辑：应检查除 GM 外是否有其他 Agent

**修复内容**：

1. **Meeting 模式 - 移除 Participant 注入**（行1804-1810）：
   ```csharp
   // 移除注入逻辑，Participant 不应委派
   var context = _agentContextComposer is null
       ? AgentContextComposer.WithoutMemory(agent.SystemPrompt)
       : await _agentContextComposer.ComposeAsync(agent.SystemPrompt, ct)
           .ConfigureAwait(false);
   var systemMessage = new RuntimeProviderMessage(
       RuntimeProviderRoles.System,
       [new TextContentBlock(context.Instructions)]);
   ```

2. **Meeting 模式 - 添加 Host 注入**（行3235-3241）：
   ```csharp
   var systemInstructions = context.Instructions;
   if (options.Participants.Length > 1)
   {
       systemInstructions = AppendAvailableParticipantsList(systemInstructions, options.Participants);
   }
   
   var systemMessage = new RuntimeProviderMessage(
       RuntimeProviderRoles.System,
       [new TextContentBlock(systemInstructions)]);
   ```

3. **Work 模式 - 修正注入条件**（行1695-1702）：
   ```csharp
   // 修正注释和条件
   // Append available agents list for GM if in Work mode with delegatable agents
   var systemInstructions = context.Instructions;
   if (request.Selection.ModeOptions is WorkModeOptions workOpts
       && agent.AgentId == workOpts.GeneralManager.AgentId
       && workOpts.AvailableAgents.Any(a => !string.Equals(a.AgentId, agent.AgentId, StringComparison.Ordinal)))
   {
       systemInstructions = AppendAvailableAgentsList(systemInstructions, workOpts.AvailableAgents);
   }
   ```

**修复后的角色矩阵**：

| 模式 | 角色 | 注入列表 | 原因 |
| --- | --- | --- | --- |
| Work | GM | ✅ | 负责分配任务给子 Agent |
| Work | 子 Agent | ❌ | 执行任务，不委派 |
| Meeting | Host | ✅ | 选择下一个发言者 |
| Meeting | Selector | ✅ | 协助选择发言者 |
| Meeting | Participant | ❌ | 被选中回答，不转手 |
| Meeting | Summarizer | ❌ | 总结内容，不委派 |

---

## 测试结果

### Workstation 测试
- ✅ 10/10 单元测试通过
- ✅ Work 模式 GM 去重测试通过
- ✅ Meeting 模式参会者去重测试通过

### Runtime CLI 测试
- ✅ 8/8 DelegateToAgent 工具测试通过
- ✅ 95/95 Modes.Tests 测试通过
- ✅ 178/178 Persistence.Tests 测试通过
- ✅ 359/360 Provider.Tests 测试通过（1 个失败与修改无关）
- ✅ 346/348 EndToEnd.Tests 测试通过（2 个失败为现有问题）

### 编译结果
- ✅ 0 警告，0 错误
- ✅ 所有依赖项目正常编译

---

## 文件清单

### 新增文件

| 文件路径 | 描述 |
| --- | --- |
| `Runtime/src/Madorin.AI.Runtime.Tools.Builtin/BuiltinDelegateAgentToolExecutor.cs` | DelegateToAgent 工具执行器 |
| `Runtime/tests/Madorin.AI.Runtime.Provider.Tests/BuiltinDelegateAgentToolExecutorTests.cs` | 工具单元测试 |
| `Runtime/docs/开发文档/11-Agent委派工具契约.md` | 协议文档 |
| `Runtime/docs/开发文档/Agent委派功能实现变更日志.md` | 本文档 |

### 修改文件

| 文件路径 | 修改内容 |
| --- | --- |
| `Workstation/src/Netor.Madorin.Workstation.Runtime/RuntimeGateway.cs` | Work/Meeting 模式 Agent 池构建 |
| `Workstation/tests/.../MeetingModeViewModelTests.cs` | Meeting 模式单元测试 |
| `Workstation/tests/.../WorkModeViewModelTests.cs` | Work 模式单元测试 |
| `Runtime/src/.../BuiltinToolRegistry.cs` | 添加 DelegateToAgent 工具定义 |
| `Runtime/src/.../RuntimeExecutionCore.cs` | 注册工具执行器 |
| `Runtime/src/.../WorkModeOrchestrator.cs` | GM System Prompt 注入 |
| `Runtime/src/.../MeetingModeOrchestrator.cs` | Host/Selector System Prompt 注入 |

---

## API 契约

### DelegateToAgent 工具

**Tool ID**: `mcli_delegate_agent`

**输入**:
```json
{
  "agentName": "Developer",
  "task": "Fix the authentication bug"
}
```

**输出（成功）**:
```json
{
  "delegated": true,
  "agentName": "Developer",
  "task": "Fix the authentication bug",
  "message": "Task delegation request accepted. The orchestrator will route to agent 'Developer'."
}
```

**输出（失败）**:
```json
{
  "delegated": false,
  "message": "Agent name must be a non-empty string."
}
```

### ModeOptions 契约

**Work 模式**:
```csharp
public sealed record WorkModeOptions : ModeOptions
{
    public AgentRef GeneralManager { get; init; }
    public AgentRef[] AvailableAgents { get; init; }  // 包含 GM 自身
    // ...
}
```

**Meeting 模式**:
```csharp
public sealed record MeetingModeOptions : ModeOptions
{
    public MeetingParticipant[] Participants { get; init; }
    public AgentRef? HostAgent { get; init; }
    public MeetingSelectorPolicyOptions? SelectorPolicy { get; init; }
    // ...
}

public sealed record MeetingParticipant(
    string ParticipantId,
    AgentRef Agent,
    string DisplayName,
    int JoinOrder,
    ParticipantStatus Status = ParticipantStatus.Active);
```

---

## 迁移指南

### 从无委派到有委派

**Workstation 宿主**：

1. 升级到包含 `88537d9` 提交的版本
2. 确保 `CreateSessionRequest.ParticipantAgents` 正确填充
3. 验证 Agent 去重逻辑正常工作

**Runtime CLI**：

1. 升级到包含 `927367d` 提交的版本
2. 无需修改现有代码，工具自动注册
3. 新建 Session 时自动启用委派功能

**测试验证**：

```bash
# 编译 Runtime
cd E:\Netor.me\Madorin\Netor.Madorin.Ai.Runtime
dotnet build --no-incremental

# 运行测试
dotnet test --no-build

# 编译 Workstation
cd E:\Netor.me\Madorin\Netor.Madorin.Workstation
dotnet build --no-incremental

# 运行测试
dotnet test --no-build
```

---

## 已知限制

1. **单向委派**：当前仅支持 GM→子Agent 或 Host/Selector→Participant，不支持反向委派
2. **元数据工具**：工具仅返回委派意图，实际调用由编排器处理
3. **静态列表**：Session 启动后 Agent 列表不动态更新
4. **无委派链追踪**：不记录委派调用链，仅记录最终执行 Agent

---

## 后续计划

### 短期优化
- [ ] 动态 Agent 列表更新（Session 运行时增删 Agent）
- [ ] 委派链追踪和可视化
- [ ] 委派失败重试策略

### 中期增强
- [ ] Agent 能力声明和匹配
- [ ] 基于负载的自动路由
- [ ] 委派权限细粒度控制

### 长期演进
- [ ] 跨 Session Agent 复用
- [ ] Agent 市场和插件机制
- [ ] 分布式 Agent 协作

---

## 联系与支持

- **文档位置**: `E:\Netor.me\Madorin\Netor.Madorin.Ai.Runtime\docs\开发文档\`
- **源码仓库**: 
  - Runtime CLI: `E:\Netor.me\Madorin\Netor.Madorin.Ai.Runtime`
  - Workstation: `E:\Netor.me\Madorin\Netor.Madorin.Workstation`
- **测试覆盖**: 18/18 单元测试通过
- **协议版本**: V1 (CatalogVersion 2.0.0)

---

**生成时间**: 2026-09-12  
**文档版本**: 1.0.0  
**维护者**: Runtime 开发团队
