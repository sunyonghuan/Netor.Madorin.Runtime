# Agent 委派工具契约

> 当前生效版本：Madorin.AI.Runtime V1（工具目录 `CatalogVersion` 为 `3.0.0`）
>
> 本文描述 Runtime 暴露给模型的 Agent 委派工具，用于支持 `@agentname` 语义和多 Agent 协作。

## 1. 工具目录

| Tool ID | 功能 | 风险 | 主要权限 |
| --- | --- | --- | --- |
| `mcli_delegate_agent` | 将任务委派给指定 Agent | 低风险 | `agent.delegate` |

工具展示给模型的描述：

```text
Delegates a task to a specific agent from the available agent pool.
```

## 2. 工具定义

### 2.1 输入 Schema

```json
{
  "type": "object",
  "properties": {
    "agentName": {
      "type": "string",
      "description": "Name of the target agent (case-insensitive)"
    },
    "task": {
      "type": "string",
      "description": "Task description to delegate"
    }
  },
  "required": ["agentName", "task"],
  "additionalProperties": false
}
```

### 2.2 输出 Schema

```json
{
  "type": "object",
  "properties": {
    "delegated": {
      "type": "boolean"
    },
    "agentName": {
      "type": "string"
    },
    "task": {
      "type": "string"
    },
    "message": {
      "type": "string"
    }
  },
  "required": ["delegated", "message"],
  "additionalProperties": false
}
```

## 3. 工具行为

### 3.1 设计原则

`mcli_delegate_agent` 是一个**元数据工具**，不直接执行 Agent 调用，而是返回委派意图的结构化数据。实际的 Agent 调用由上层编排器（Orchestrator）处理。

**为什么不直接调用？**

- Agent 调用需要访问 ModeOptions（Work/Meeting 配置）
- 工具执行器设计为无状态，不持有会话上下文
- 保持工具层的简单性和可测试性

### 3.2 验证规则

工具执行器负责验证：

1. **参数完整性**：`agentName` 和 `task` 必填且非空
2. **参数类型**：必须为字符串类型

工具执行器**不负责**：

- Agent 是否存在（由编排器查找）
- Agent 是否可用（由编排器验证）
- 当前上下文是否允许委派（由编排器决策）

### 3.3 返回值

成功验证后返回：

```json
{
  "delegated": true,
  "agentName": "Developer",
  "task": "Fix the authentication bug",
  "message": "Task delegation request accepted. The orchestrator will route to agent 'Developer'."
}
```

参数验证失败返回：

```json
{
  "delegated": false,
  "message": "Agent name must be a non-empty string."
}
```

## 4. 模式集成

### 4.1 Work 模式

**角色**：General Manager (GM)

**注入条件**：
- 当前 Agent 是 GM
- `AvailableAgents` 中存在除 GM 外的其他 Agent

**System Prompt 注入格式**：

```markdown
## Available Agents for Delegation

You can delegate tasks to the following agents using the `delegate_to_agent` tool:

- **Developer**
- **Tester**
- **Designer**

Use the tool when the user mentions "@AgentName" or "let {Agent} do...".
```

**注意事项**：
- GM 自身也在 `AvailableAgents` 中，注入前需过滤
- 只有当存在可委派的其他 Agent 时才注入列表

### 4.2 Meeting 模式

**角色分配**：

| 角色 | 是否注入参会者列表 | 原因 |
| --- | --- | --- |
| **Host** | ✅ 是 | 负责选择下一个发言者 |
| **Selector** | ✅ 是 | 协助选择发言者 |
| **Participant** | ❌ 否 | 被选中来回答问题，不应转手代理 |
| **Summarizer** | ❌ 否 | 总结会议内容，不需要委派 |

**System Prompt 注入格式**：

```markdown
## Available Participants for Delegation

You can delegate tasks to the following participants using the `delegate_to_agent` tool:

- **Developer** (agent-dev)
- **Tester** (agent-test)

Use the tool when the user mentions "@ParticipantName" or "let {Participant} do...".
```

**注意事项**：
- 仅注入 `Status = Active` 的参会者
- 显示格式：`DisplayName (AgentId)`
- Participant 不应使用此工具（其他 Participant 应通过 Host/Selector 选择）

### 4.3 Expert 模式

Expert 模式为单 Agent 模式，**不支持**委派工具。

## 5. 宿主集成指南

### 5.1 构造 ModeOptions

**Work 模式示例**：

```csharp
var generalManager = new AgentRef(
    agentId: "gm",
    promptTemplateVersion: "v1",
    systemPrompt: "You are the General Manager...",
    providerId: "anthropic",
    modelId: "claude-opus-4");

var availableAgents = new[]
{
    generalManager,  // GM 自身也在池中
    new AgentRef("developer", "v1", "You are a developer...", "anthropic", "claude-sonnet-4"),
    new AgentRef("tester", "v1", "You are a tester...", "anthropic", "claude-haiku-4")
};

var workOptions = new WorkModeOptions(
    generalManager: generalManager,
    availableAgents: availableAgents);
```

**Meeting 模式示例**：

```csharp
var participants = new[]
{
    new MeetingParticipant(
        participantId: "p1",
        agent: new AgentRef("developer", "v1", "You are a developer...", "anthropic", "claude-sonnet-4"),
        displayName: "Developer",
        joinOrder: 0,
        status: ParticipantStatus.Active),
    new MeetingParticipant(
        participantId: "p2",
        agent: new AgentRef("tester", "v1", "You are a tester...", "anthropic", "claude-haiku-4"),
        displayName: "Tester",
        joinOrder: 1,
        status: ParticipantStatus.Active)
};

var hostAgent = new AgentRef("host", "v1", "You are the meeting host...", "anthropic", "claude-opus-4");

var meetingOptions = new MeetingModeOptions(
    participants: participants,
    hostAgent: hostAgent);
```

### 5.2 处理委派事件

当模型调用 `mcli_delegate_agent` 工具后，宿主应：

1. **接收工具结果事件**：从事件流中读取 `ToolResultEvent`
2. **解析委派意图**：提取 `agentName` 和 `task`
3. **路由决策**：
   - Work 模式：由 GM 决定是否接受委派
   - Meeting 模式：由 Host/Selector 决定下一个发言者
4. **执行调用**：根据模式规则调用目标 Agent

**注意**：当前 Runtime 版本 (V1) 工具仅返回元数据，实际的 Agent 调用由编排器内部处理。宿主通过事件流观察委派结果，无需手动路由。

### 5.3 错误处理

| 错误场景 | 工具返回 | 建议处理 |
| --- | --- | --- |
| `agentName` 为空 | `delegated: false` | 提示模型提供有效的 Agent 名称 |
| `task` 为空 | `delegated: false` | 提示模型提供任务描述 |
| Agent 不存在 | `delegated: true`，编排器拒绝 | 编排器返回错误消息给模型 |
| Agent 不可用 | `delegated: true`，编排器拒绝 | 编排器返回替代建议给模型 |
| 当前角色不可委派 | 工具未注入 | 模型看不到该工具 |

## 6. 协议版本

| 版本 | 变更 | 引入时间 |
| --- | --- | --- |
| 1.0.0 | 初始版本，Work/Meeting 模式支持 | 2026-09-12 |

## 7. 安全与权限

### 7.1 权限需求

- **工具级别**：`agent.delegate`（低风险）
- **默认授权**：自动授予，无需用户交互
- **权限范围**：仅返回元数据，不执行实际调用

### 7.2 安全考虑

- **无状态设计**：工具执行器不持有会话状态，避免上下文泄露
- **参数验证**：严格验证输入参数，防止注入攻击
- **权限隔离**：委派不绕过目标 Agent 的权限检查
- **审计日志**：所有委派请求记录到审计日志

## 8. 测试覆盖

参见 `Madorin.AI.Runtime.Provider.Tests` 项目中的 `BuiltinDelegateAgentToolExecutorTests.cs`：

- ✅ 工具识别测试
- ✅ 有效输入测试
- ✅ 缺失参数测试（`agentName`、`task`）
- ✅ 空值参数测试
- ✅ 错误工具 ID 测试
- ✅ JSON 序列化测试

## 9. 最佳实践

### 9.1 模型提示词建议

在 Agent System Prompt 中建议包含：

```text
When the user mentions "@AgentName" or "let {Agent} handle...", use the delegate_to_agent tool
to route the task to the appropriate specialist agent.

Example:
- User: "@Developer fix the login bug"
  → Use delegate_to_agent with agentName="Developer" and task="fix the login bug"
```

### 9.2 宿主实现建议

- **Agent 名称规范化**：建议使用 `StringComparison.OrdinalIgnoreCase` 查找
- **DisplayName 回退**：Meeting 模式优先显示 `DisplayName`，回退到 `AgentId`
- **去重逻辑**：Work 模式构建 `AvailableAgents` 时检查 GM 是否已存在
- **状态过滤**：Meeting 模式仅注入 `Active` 状态的参会者

### 9.3 性能优化

- **懒加载注入**：仅在 GM/Host/Selector 角色时才注入列表
- **缓存列表**：同一 Session 内可缓存 Agent 列表，避免重复序列化
- **批量查询**：宿主启动时预加载所有可用 Agent 配置

## 10. 相关文档

- [04-宿主集成指南](./04-宿主集成指南.md) - Runtime Client SDK 使用
- [07-模型文件工具契约](./07-模型文件工具契约.md) - 文件工具参考
- `src/Madorin.AI.Runtime.Tools.Builtin/BuiltinDelegateAgentToolExecutor.cs` - 工具实现
- `src/Madorin.AI.Runtime.Modes.Work/WorkModeOrchestrator.cs` - Work 模式集成
- `src/Madorin.AI.Runtime.Modes.Meeting/MeetingModeOrchestrator.cs` - Meeting 模式集成
