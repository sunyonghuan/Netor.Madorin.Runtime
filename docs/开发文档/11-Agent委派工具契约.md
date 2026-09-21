# Agent 委派工具契约

> 当前生效版本：Madorin.AI.Runtime V1（工具目录 `CatalogVersion` 为 `3.2.0`）
>
> 本文描述 **同步** Agent 委派工具。并行后台作业与结果回收见 [12-并行Agent作业契约.md](./12-并行Agent作业契约.md)。

## 1. 工具目录

| Tool ID | 功能 | 风险 | 主要权限 |
| --- | --- | --- | --- |
| `mcli_delegate_agent` | 将任务委派给 Expert 模式可用 Agent 池中的指定 Agent | 低风险 | `agent.delegate` |

工具展示给模型的描述：

```text
Delegates a task to a specific agent from the Expert mode available agent pool. The Expert orchestrator runs the target agent and returns its output in result.
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
    "success": {
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
    },
    "result": {
      "type": "string"
    }
  },
  "required": ["delegated", "success", "message"],
  "additionalProperties": false
}
```

## 3. 工具行为

### 3.1 两段式设计

`mcli_delegate_agent` 分两段完成：

1. **工具执行器（无状态）**：只校验 `agentName` / `task`，返回中间态 JSON（`delegated: true`, `success: false`）。
2. **Expert 编排器（有会话上下文）**：在工具循环中拦截成功校验结果，同步调用目标子 Agent，把最终 JSON 写回同一 `tool result`（成功时 `success: true` 且带 `result`）。

模型看到的始终是编排器回写后的最终 tool result，而不是执行器的中间受理文案。

**为什么不在执行器内直接调用子 Agent？**

- 子 Agent 调用需要 `ExpertModeOptions.AvailableAgents`、会话与 Run 上下文
- 工具执行器保持无状态，便于授权、同意范围与单测
- 真正路由放在编排器，与 `@agentName` 用户输入路径复用 `InvokeSubAgentAsync`

### 3.2 验证规则（执行器）

工具执行器负责验证：

1. **参数完整性**：`agentName` 和 `task` 必填且非空
2. **参数类型**：必须为字符串类型

工具执行器**不负责**：

- Agent 是否存在（由 Expert 编排器查找）
- Agent 是否可用（由 Expert 编排器验证）
- 当前上下文是否允许委派（由 Expert 编排器决策）

### 3.3 返回值

**执行器中间态（参数合法，尚未路由）**：

```json
{
  "delegated": true,
  "success": false,
  "agentName": "Developer",
  "task": "Fix the authentication bug",
  "message": "Delegation arguments accepted for agent 'Developer'. Expert orchestrator will execute the sub-agent and replace this intermediate result."
}
```

**Expert 编排器最终成功**：

```json
{
  "delegated": true,
  "success": true,
  "agentName": "Developer",
  "task": "Fix the authentication bug",
  "message": "Sub-agent 'Developer' completed the task.",
  "result": "…子 Agent 文本输出…"
}
```

**Expert 编排器最终失败示例（Agent 不存在）**：

```json
{
  "delegated": false,
  "success": false,
  "agentName": "missing",
  "task": "do something",
  "message": "Sub-agent 'missing' not found in AvailableAgents. Available: [expert, developer]"
}
```

**参数验证失败（执行器）**：

```json
{
  "delegated": false,
  "success": false,
  "message": "Agent name cannot be empty."
}
```

## 4. 模式集成

### 4.1 Expert 模式（本工具的主路径）

Expert 支持两条委派路径：

| 路径 | 触发方式 | 谁执行 | 结果落点 |
| --- | --- | --- | --- |
| 工具委派 | 主 Agent 调用 `mcli_delegate_agent` | Expert 工具循环 → `RouteDelegateAgentToolAsync` → `InvokeSubAgentAsync` | 同一 tool result（`success` + `result`） |
| 用户 `@` 委派 | 用户输入以 `@agentId <task>` 开头 | Run 入口 `TryParseDelegationSyntax` → `InvokeSubAgentAsync` | 会话 assistant 内容后继续主 Agent |

**System Prompt 注入**（存在可委派子 Agent 时）：

- 引导模型使用 `mcli_delegate_agent`
- 说明最终 tool result 含 `success` / `message` / `result`
- 说明用户 `@agentName` 仅适用于用户开场输入，不能替代对话中途的工具委派
- 列出可用子 Agent：`**agentId** (`@agentId`) - 描述`

**失败条件**（tool result `success: false`）：

- 未配置 `AvailableAgents`
- `agentName` 不在池中
- 委派给主 Agent 自身
- 子 Agent 执行失败（Provider 失败、工具轮次超限等）

### 4.2 Work 模式

Work **不**通过 `mcli_delegate_agent` 执行步骤。

GM 在 Planning 阶段于 `WorkPlanStepDraft.targetAgentId` 指定执行 Agent；编排器按计划调度。

System Prompt 引导使用 `targetAgentId`，而不是本工具。

### 4.3 Meeting 模式

Meeting **不**通过 `mcli_delegate_agent` 选择发言者。

Host / Selector 输出 JSON `participantId`（或 conclude）；编排器按参会者调度。

## 5. 宿主集成指南

### 5.1 Expert 模式构造 ModeOptions

```csharp
var expert = new AgentRef(
    agentId: "expert",
    promptTemplateVersion: "v1",
    systemPrompt: "You coordinate specialists...",
    providerId: "anthropic",
    modelId: "claude-opus-4");

var developer = new AgentRef(
    agentId: "developer",
    promptTemplateVersion: "v1",
    systemPrompt: "You are a developer...",
    providerId: "anthropic",
    modelId: "claude-sonnet-4");

var expertOptions = new ExpertModeOptions(
    agent: expert,
    availableAgents: [expert, developer]);
```

未提供 `AvailableAgents` 或多于一个可委派 Agent 时，工具仍可能出现在目录中，但编排器会返回 `success: false`。

### 5.2 观察委派结果

宿主无需手动二次路由。事件流中的 tool result 已是最终 JSON：

1. 读取 `ToolCallCompleted` / tool role 消息
2. 解析 `success`、`message`、`result`
3. 成功时 `result` 即为子 Agent 文本输出

### 5.3 错误处理

| 错误场景 | 谁返回 | tool result |
| --- | --- | --- |
| `agentName` / `task` 非法 | 执行器 | `delegated: false`, `success: false` |
| Agent 不存在 / 无池 / 委派自身 | Expert 编排器 | `success: false`，`message` 说明原因 |
| 子 Agent 执行失败 | Expert 编排器 | `delegated: true`, `success: false` |
| Work/Meeting 误调工具 | 工具可执行，但模式不会按 Expert 方式路由执行结果为子 Agent 业务输出 | 以各模式内置机制为准，勿依赖本工具做步骤/发言者分配 |

## 6. 协议版本

| 版本 | 变更 | 引入时间 |
| --- | --- | --- |
| 1.0.0 | 初始元数据工具，Work/Meeting 文档导向 | 2026-09-12 |
| 1.1.0 | Expert 编排器同步路由；输出增加 `success`/`result`；Catalog `3.1.0` | 2026-09-20 |
| 1.2.0 | 并行作业工具 `mcli_agent_job_*`（create/wait/result 回收，无定时）；Catalog `3.2.0` | 2026-09-20 |

## 7. 安全与权限

### 7.1 权限需求

- **工具级别**：`agent.delegate`（低风险）
- **默认授权**：可在无宿主 Grant 时默认允许（仅元数据校验段）
- **实际子 Agent 工具调用**：仍走目标 Agent 的授权与同意链，不因委派绕过

### 7.2 安全考虑

- 执行器无状态，不持有会话
- 编排器禁止委派给主 Agent 自身
- 子 Agent 以 `ParentAgentId` 进入工具调用，Grant 链可审计
- 委派请求与结果进入会话/事件流

## 8. 测试覆盖

- `BuiltinDelegateAgentToolExecutorTests`：参数校验与中间态输出
- `ExpertToolLoopTests.RunAsync_DelegateAgentTool_RoutesSubAgentAndReturnsResult`：工具 → 子 Agent → `result`
- `ExpertToolLoopTests.RunAsync_DelegateAgentTool_UnknownAgent_ReturnsFailureResult`：未知 Agent 失败回写
- HostIntegration：`@` 语法解析与 System Prompt 注入文案

## 9. 最佳实践

1. Expert 对话中途委派：优先 `mcli_delegate_agent`，读取 `result`，勿把中间「accepted」文案当最终答案。
2. 用户显式点名开场：可用 `@agentId <task>`。
3. Work 分步执行：只用 `targetAgentId`。
4. Meeting 选人：只用 `participantId` JSON。
5. 宿主构造 Expert 会话时务必注入完整 `AvailableAgents`。
