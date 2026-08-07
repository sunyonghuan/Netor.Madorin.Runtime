# Work 模式契约

> 当前生效版本：Madorin.AI.Runtime V1
>
> 本文定义 Work 模式的完整数据结构、执行模型、策略参数和状态机。Work 模式在 Session 创建时确定，创建后不可更改。

## 1. 概述

Work 模式由一个总经理（Manager）Agent 主导，负责将用户任务分解为步骤（Step），通过子智能体（Sub-Agent）执行各步骤，支持步骤间的依赖关系（DAG）和并发执行。

独立 CLI 下，选定的 Agent 作为总经理，其余已配置 Agent 作为可用子智能体；需要精细控制时使用宿主 Client SDK 传入完整配置。

## 2. 执行模型

Work 模式的执行流程：

1. Manager Agent 接收用户输入，生成 `WorkPlanDraft`。
2. Plan 包含多个 Step，Step 之间可声明依赖关系，形成 DAG。
3. Runtime 按 DAG 拓扑顺序调度 Step，无依赖的 Step 可并发执行。
4. 每个 Step 可调用一个 Sub-Agent 完成具体工作。
5. 所有 Step 完成后，Manager Agent 生成最终输出。

Runtime 负责 DAG 调度、并发控制和故障恢复；Manager Agent 只负责计划生成和结果综合。

## 3. WorkflowPolicy

`WorkflowPolicy` 控制 Work 模式的资源限制和行为参数。

| 参数 | 默认值 | 硬上限 | 说明 |
| --- | --- | --- | --- |
| `MaxAgentDepth` | 3 | 10 | 子智能体嵌套深度，防止无限递归 |
| `MaxStepsPerRun` | 50 | 500 | 单次 Run 内最大步骤数 |
| `MaxSubAgentJobs` | 20 | — | 同时存在的子智能体任务数 |
| `MaxConcurrentSteps` | 4 | — | 并发执行的步骤数 |
| `StepTimeoutSeconds` | 300 | — | 单步骤超时（秒） |
| `MaxRetriesPerStep` | 2 | — | 每步骤最大重试次数 |
| `RetryBackoffMilliseconds` | 1000 | — | 指数退避的基础间隔（毫秒），实际间隔按已完成尝试次数指数增长（`base × 2^n`，指数封顶 6），并有 60000 毫秒上限封顶 |

硬上限为 Runtime 强制限制，宿主传入的值超出硬上限时会被拒绝并抛出异常（`ArgumentOutOfRangeException` 或校验异常），不会做任何截断（clamp）。未标注硬上限的参数由宿主自行约束。

## 4. 步骤失败策略

`WorkStepFailurePolicy` 定义单步骤执行失败后的行为：

| 策略 | 说明 |
| --- | --- |
| `Stop` | 停止整个 Plan，标记为 Failed |
| `Continue` | 跳过失败步骤，继续执行后续步骤 |
| `Retry` | 按 `MaxRetriesPerStep` 和 `RetryBackoffMilliseconds` 重试 |
| `AskHost` | 暂停并请求 Host 决策（通过 HITL 事件） |

## 5. 循环检测

`WorkLoopDetection` 控制 DAG 中循环引用的检测策略：

| 策略 | 说明 |
| --- | --- |
| `Strict` | 严格检测，任何疑似循环均阻止执行 |
| `Lenient` | 宽松检测，仅在明确循环时阻止 |
| `Disabled` | 关闭循环检测 |

默认 `RequireHumanConfirmationOnLoop` 为 `true`，检测到循环时暂停并请求用户确认。设为 `false` 时按检测策略自动处理。

## 6. 步骤生命周期

`WorkStepLifecycleStatus` 定义单步骤的状态流转：

```text
Pending ─→ Running ─→ Completed

Running ─→ Failed ─┬─ Retry 策略（未达上限）───────────────→ Running（自动重试）
                   ├─ AskHost 策略 ─→ WaitingForApproval ─→ Running（审批通过后）
                   └─ Stop / 重试耗尽 ────────────────────→ Failed（终止）

Running ─→ WaitingForCredentials ─→ Running（凭据更新后）

Running ─→ Interrupted / Skipped
```

各分支是并列的，失败步骤不会依次经过审批、等凭据：

- `Retry` 策略下步骤失败且未达 `MaxRetriesPerStep` 时，`Failed` 直接回到 `Running` 自动重试；重试耗尽则整个 Plan 标记 `Failed`。
- `AskHost` 策略下失败才进入 `WaitingForApproval`，宿主审批通过后回到 `Running` 重试，拒绝则 Plan 标记 `Failed`。
- 仅当步骤执行中出现凭据失效时才进入 `WaitingForCredentials`，凭据更新后回到 `Running`。
- `Stop` 策略或前置失败等场景直接进入 `Failed` / `Skipped`。

| 状态 | 说明 |
| --- | --- |
| `Pending` | 等待调度，依赖的 Step 尚未完成 |
| `Running` | 正在执行 |
| `Completed` | 成功完成 |
| `Failed` | 执行失败且未重试或重试耗尽 |
| `Interrupted` | 被外部中断 |
| `Skipped` | 因前置 Step 失败或策略决定被跳过 |
| `WaitingForApproval` | 等待 HITL 审批 |
| `WaitingForCredentials` | 等待凭据更新 |

## 7. 后台任务

`WorkBackgroundJobStatus` 用于 Sub-Agent 的异步任务状态：

| 状态 | 说明 |
| --- | --- |
| `Pending` | 已创建，等待执行 |
| `Running` | 正在执行 |
| `Completed` | 成功完成 |
| `Failed` | 执行失败 |
| `Cancelled` | 被取消 |
| `Interrupted` | 被外部中断 |

后台任务独立于 Step 生命周期，Step 可以等待关联的后台任务完成后再进入 `Completed` 状态。

## 8. 会话状态

`WorkSessionStatus` 定义 Work 模式的整体状态：

```text
Planning → Executing → Completed
             ↓
          Paused / WaitingForApproval / WaitingForCredentials
             ↓
          Failed / Cancelled / Interrupted
```

| 状态 | 说明 |
| --- | --- |
| `Planning` | Manager Agent 正在生成 Plan |
| `Executing` | Plan 正在执行中 |
| `Paused` | 因故障或人工介入暂停 |
| `WaitingForApproval` | 等待 HITL 审批 |
| `WaitingForCredentials` | 等待凭据更新 |
| `Completed` | 所有 Step 完成 |
| `Failed` | 不可恢复的错误导致终止 |
| `Cancelled` | 被用户或宿主取消 |
| `Interrupted` | 被外部中断 |

## 9. 上下文策略

`WorkContextPolicy` 控制每个 Step 执行时的上下文窗口：

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `PreservePlan` | `true` | 在上下文中保留完整 Plan |
| `PreserveCurrentStep` | `true` | 保留当前 Step 的详细信息 |
| `PreserveToolResults` | `true` | 保留工具调用结果 |
| `PreserveReasoning` | `false` | 保留推理过程（Token 开销大） |
| `MaxTokens` | 无限制 | 上下文 Token 上限，超出时截断早期消息 |
| `TailMessageCount` | 无限制 | 尾部消息窗口大小，与 `MaxTokens` 取较严格者 |

## 10. 计划结构

Work 模式的计划和步骤通过以下数据结构表达：

| 类型 | 说明 |
| --- | --- |
| `WorkPlanDraft` | 初始计划，由 Manager Agent 生成 |
| `WorkPlanStepDraft` | 单个步骤定义，包含描述、依赖和 Sub-Agent 指定 |
| `WorkPlanRevisionSnapshot` | 计划修订历史，记录 Manager 对计划的每次修改 |
| `WorkStepSnapshot` | 已执行步骤的完整状态快照 |
| `WorkStepAttemptSnapshot` | 单次执行尝试的记录，包含输入输出和耗时 |

计划修订历史允许回溯 Manager 的决策过程，用于调试和审计。

## 11. 恢复

`WorkResumeState` 持久化 Plan 和所有 Step 的当前状态，包括：

- Plan 结构和修订历史
- 各 Step 的 `WorkStepLifecycleStatus` 和已完成尝试
- 未完成的后台任务状态
- 等待中的 HITL 请求

断线重连后，Runtime 从 `WorkResumeState` 恢复执行。处于 `Running` 状态的 Step 在重连后重新执行（因为无法确认远端是否已完成），已 `Completed` 的 Step 不重复。
