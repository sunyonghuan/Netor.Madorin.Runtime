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

### 2.1 按 Agent `SkillIds` 过滤技能目录

GM 与各 Sub-Agent 在写入 `InvocationSnapshot` 前，对 Catalog 调用 `FilterBySkillIds(agent.SkillIds)`（`null` / `[]` / 非空语义同 [12-Skills系统](./12-Skills系统.md) §5.1）。同一 Plan 内不同 Agent 可绑定不同技能集。

### 2.2 宿主模式 Skills 与工具 live catalog

宿主集成时：

- Run 级共享 `SkillActivationStore`；`AgentRef.skillDiscoveryMode` 经 Work 路径写入 `SkillsInvocationContext`。
- 每轮 `CreateSkillsContext`；tool-result 成功后 `TryApplyToolResult`（progressive 激活；direct no-op）。
- 工具循环使用 **live catalog**（`liveCatalogProvider`），search/browse 注入后：
  - **全量暴露**：下一轮 `AvailableTools` 跟随 live 增长
  - **Allowlist**：只刷新已允许 ToolId；**initial 快照缺失时不推断为全量**（避免把 allowlist 放大成 live 全目录）

详见 [12-Skills系统](./12-Skills系统.md) §8、[04-宿主集成指南](./04-宿主集成指南.md) §10.9–§10.10。

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

Work 模式的计划采用**二级步骤结构**：一级步骤（交付物）包含多个二级步骤（执行单元）。

### 10.1 数据结构

| 类型 | 说明 |
| --- | --- |
| `WorkPlanDraft` | 初始计划，由 Manager Agent 生成，包含多个一级步骤 |
| `WorkPlanStepDraft` | 一级步骤定义，表示一个交付物阶段，包含 `title`、`description`、`subSteps` 和 `dependsOn` |
| `WorkPlanSubStepDraft` | 二级步骤定义，表示实际执行单元，包含 `subStepId`、`goal`、`instruction`、`targetAgentId` |
| `WorkPlanRevisionSnapshot` | 计划修订历史，记录 Manager 对计划的每次修改 |
| `WorkStepSnapshot` | 已执行步骤的完整状态快照（仅二级步骤有独立记录） |
| `WorkStepAttemptSnapshot` | 单次执行尝试的记录，包含输入输出和耗时 |

### 10.2 二级步骤结构

**一级步骤（`WorkPlanStepDraft`）**：
- `stepId`：一级步骤 ID（格式：`"1"`、`"2"`）
- `title`：一级步骤标题（交付物名称）
- `description`：一级步骤描述（交付物说明）
- `subSteps`：二级步骤数组（必须至少包含一个二级步骤）
- `dependsOn`：依赖的一级步骤 ID 数组（可选）

**二级步骤（`WorkPlanSubStepDraft`）**：
- `subStepId`：二级步骤 ID（格式：`"1.1"`、`"1.2"`、`"2.1"`）
- `goal`：二级步骤目标
- `instruction`：给 Worker Agent 的执行指令
- `targetAgentId`：执行该步骤的 Worker Agent ID
- `expectedOutput`：期望产出（可选，用于验证）

### 10.3 执行模型

1. **展平调度**：二级步骤在持久化时展平到 `work_steps` 表，只有二级步骤有独立的 `WorkStepSnapshot` 记录。
2. **依赖翻译**：一级步骤的 `dependsOn` 会自动翻译到二级步骤的依赖关系：
   - 一级步骤 `"2"` 依赖 `"1"` 时，`"2.1"` 会依赖 `"1"` 的最后一个二级步骤（如 `"1.2"`）
   - 同一级步骤内的二级步骤按顺序依赖（`"1.2"` 依赖 `"1.1"`）
3. **汇总生成**：一级步骤的所有二级步骤完成后，自动生成一级步骤的汇总报告。

### 10.4 旧格式兼容

运行时自动将旧格式计划转换为新格式：
- 旧格式：包含 `goal` 和 `targetAgentId`，无 `subSteps`
- 新格式：自动生成包含一个二级步骤的一级步骤

计划修订历史允许回溯 Manager 的决策过程，用于调试和审计。

## 11. 恢复

`WorkResumeState` 持久化 Plan 和所有 Step 的当前状态，包括：

- Plan 结构和修订历史
- 各 Step 的 `WorkStepLifecycleStatus` 和已完成尝试
- 未完成的后台任务状态
- 等待中的 HITL 请求

断线重连后，Runtime 从 `WorkResumeState` 恢复执行。处于 `Running` 状态的 Step 在重连后重新执行（因为无法确认远端是否已完成），已 `Completed` 的 Step 不重复。

---

## 12. 规划轮与计划修订（运行时行为）

1. **讨论轮**：总经理输出若无法解析为合法 `WorkPlanDraft`（含校验失败），本轮只结束 Run，不发布 `work.plan.updated`，不调度任何步骤。会话保持 `Planning`（若此前已有计划，则恢复进入本轮前的会话状态；若正在等计划确认，则保持 `WaitingForPlanConfirmation`）。
2. **可执行计划**：仅在解析且校验通过后，才 `SavePlanRevision` 并发出 `work.plan.updated`。
3. **计划确认门**（`WorkflowPolicy.RequirePlanConfirmation`）：
   - 发布计划后，若用户本轮未给出明确执行确认，会话进入 `WaitingForPlanConfirmation`，**本轮不调度步骤**。
   - 后续用户输入明确确认（如「确认执行」「批准」）且未要求修订时，Runtime **跳过总经理**，直接按已发布计划执行。
   - 若用户在确认语句中同时给出新计划 JSON，则按修订保存后执行。
   - 独立 CLI 的 Work 模式默认开启该开关。
4. **版本自愈**：若会话已有已发布计划，Runtime 会把 GM 给出的 `planVersion` / `previousPlanVersion` 规范为「当前版本 + 1」并回填 `previousPlanVersion`，避免多轮对话重复发布 `1` 导致失败。
5. **规划上下文**：多轮规划时会向总经理注入已发布版本、下一步期望版本、确认等待提示，以及当前步骤状态摘要。
6. **失败策略**：`Continue` 会把失败步骤落为 `Failed` 后继续；`Retry` 先落 `Failed` 再重置为 `Pending` 重试。依赖失败挡住的后续 Pending 步骤会落盘为 `Skipped` 并发送 `work.step.skipped`。
7. **步骤并发**：`MaxConcurrentSteps` 控制每波就绪步骤上限。同波就绪步骤可在 Provider/工具环并行执行；步骤准备与结果落盘仍串行。生产路径对 Session/Work、ToolIntent、Outbox 使用分连接；同连接对象须经连接级闸门串行化。
8. **用户插话队列（Work 可交付切片，非完整 Run Control）**：
   - 持久化表 `work_pending_inputs`；API：`SqliteWorkRepository.EnqueuePendingUserInputAsync` / `PeekPendingUserInputsAsync` / `ConsumePendingUserInputsAsync`，以及编排器 `WorkModeOrchestrator.EnqueueUserInputAsync`。
   - 规划轮开始时按 FIFO 消费未读插话，注入总经理上下文后标记 `consumed`。
   - Continue / 新输入若明确要求改目标，或携带新计划 JSON / 修订关键词，走版本自愈 + `SavePlanRevision(N+1)`；`reusesStepId` 指向已完成历史步骤时直接落 `completed`，不重跑。
   - 步骤间隙若仍有未消费插话，本轮停靠为 `Paused`（`paused-for-pending-input`），保留未完成步骤，交下一轮规划消费。
   - **缺口**：尚未提供 Server JSON-RPC `run.pause/interrupt/resume` 或 Client SDK 专用方法；宿主可用同会话 `run.existing_session` 的 `InputOverride` 作为下一轮目标文本，或进程内调用上述入队 API。完整控制面见 `docs/备档/工作流暂停与恢复/`。
9. **Work 工具路径（与编排器路径并存）**：
   - `mcli_work_list_plan_templates` / `load_plan_from_template` / `read_template_file`：内置扫盘，只供材料。
   - `mcli_work_set_plan`：校验并返回规范化 `WorkPlanDraft`（支持原生 `planVersion/goal/steps` 或 `main_steps/sub_steps`）。一个 `main_steps` 项是一次派发；同一执行人的 `sub_steps` 折进该步骤的 goal，不按「交付物数 × 门禁数」拆成运行时步骤。执行人不同时才按 sub_step 拆开。步骤数超过 `MaxStepsPerRun` 时工具返回 `too_many_steps`，不发布计划。编排器在总经理工具环成功后优先采用该 `plan` 落盘并发 `work.plan.updated`；仍保留裸 JSON 文本计划作为兼容回退。禁止参数含 `{{placeholders}}`。规划轮会把用户原文和总经理回复写入会话记录，下一轮规划提示带上这段记录。单独的「开始」在计划已发布并等待确认时视为执行确认。
   - 计划确认门仍由 `RequirePlanConfirmation` / `WaitingForPlanConfirmation` 控制；`set_plan` 本身不代替用户确认。
   - `mcli_work_dispatch_step`：要求会话已有已确认计划；按 `stepIndex`/`subStepIndex` 解析目标步骤；经 `WorkStepDispatchBridge` **同步**拉起 `targetAgentId` 子 Agent（编排器内 `ExecuteInvocationAsync`），返回 `{executed,stepId,outputText,...}`。未确认或无计划时拒绝。无绑定桥时仅返回校验通过（`executed=false`）。
   - 目录：`SupportedModes=["work"]`；`project-lead` 仅可见 `dispatch_step` + `read_template_file` + 通用工具。
   - 历史材料：`mcli_work_load_plan_from_chat_history` / `mcli_work_load_plan_from_groupchat` 读取 `ConversationStore` transcript（groupchat 要求 `meetingId` 对应 `meeting_sessions`）；仍须 `set_plan` 发布。
10. **共享交互工具（Work/Meeting）**：
   - `mcli_check_pending_user_input`：按 Session 模式分发。Work **消费** `work_pending_inputs`；Meeting **Peek** `meeting_pending_inputs`（Schema V19）。返回 `{hasPending,consumed,inputs,message}`。`ask_user` 前应先调用。
   - `mcli_ask_user`：向宿主发起审批/HITL（`ApprovalRequest.ToolId=mcli_ask_user`，`Reason=question`）；宿主在 `ApprovalResponse.Reason` 回传答案文本后，工具结果返回 `{answered,question,answer}`。无宿主审批通道时返回 `ask_user_unavailable`。
   - `SupportedModes=["work","meeting"]`；Expert 不可见。Work 仅 `general-manager` 可见；Meeting 仅 `meeting-host` 可见。

---

## 修订记录

| 日期 | 说明 |
| --- | --- |
| 2026-09-22 | chat_history/groupchat 读 transcript；dispatch_step 同步子 Agent；Meeting pending Peek（V19）；ask_user/check_pending 冒烟场景 |
| 2026-09-22 | 历史材料：`get_recent_completed_task_plan` 可读本会话已完成计划；chat_history / groupchat 已登记，本轮返回 not_implemented |
| 2026-09-22 | §12.10：共享 `mcli_ask_user` / `mcli_check_pending_user_input`（Work 队列消费 + 宿主审批通道作答） |
| 2026-09-22 | §12.9：`mcli_work_set_plan` / `dispatch_step` 接通；编排器优先消费 set_plan；模板三件套 + A/B 目录过滤 |
| 2026-09-22 | Schema V18：`work_steps` / dependencies / attempts 主键纳入 `session_id`，同库多 Work 会话可并行落盘 |
| 2026-09-22 | §12.8：Work 插话队列 / 步骤间隙停靠 / 修订复用；标明与完整 Run Control 边界 |
| 2026-03-22 | §12.8：Work 插话 FIFO（V17 `work_pending_inputs`）；步骤间隙遇插话停靠；规划轮消费后注入总经理 |
| 2026-03-22 | §12.7：有工具波次亦可并发（分连接 + 连接级闸门） |
| 2026-03-22 | §12.6–§12.7：依赖失败 Skipped 落盘；无工具波次并发 |
| 2026-03-22 | §12：计划确认门 `WaitingForPlanConfirmation`；CLI 默认开启 RequirePlanConfirmation |
| 2026-03-22 | §12：讨论轮不自动 fallback 执行；计划版本自愈；Continue/Retry 失败落盘 |
| 2026-09-07 | §2.1 SkillIds 过滤；§2.2 宿主 Skills + live catalog（allowlist 不因 initial 缺失放大） |
