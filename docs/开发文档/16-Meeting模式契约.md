# Meeting 模式契约

> 当前生效版本：Madorin.AI.Runtime V1
>
> 本文定义 Meeting 模式的完整数据结构、角色分工、调度策略和状态机。Meeting 模式在 Session 创建时确定，创建后不可更改。

## 1. 概述

Meeting 模式将多个 Agent 组织为参会者，由主持人（Host）调度发言，支持轮次摘要和 Human-in-the-Loop 审批。Runtime 负责维护会议状态、执行选择策略和管理故障恢复。

独立 CLI 下，Meeting 的参与者集合从所有已配置 Agent 自动派生；需要精细控制时使用宿主 Client SDK 传入完整配置。

## 2. 角色

`MeetingInvocationRole` 枚举定义每个 Agent 在单轮调度中的角色：

| 角色 | 说明 |
| --- | --- |
| `Participant` | 普通参会者，按调度策略获得发言权 |
| `Host` | 主持人，控制会议节奏、处理 HITL 审批 |
| `Selector` | 选择器，决定下一个发言的参会者 |
| `Summarizer` | 摘要员，生成轮次摘要或最终总结 |

同一个 Agent 可以在不同轮次中承担不同角色。HITL 审批请求由 Runtime（orchestrator）经回调发出并处理，响应也回到 orchestrator，Host 角色 Agent 并不接收审批请求；HITL 请求中的 `HostInvocationId` 只是标识符。

## 3. 参与者管理

每个参与者拥有独立的 `ParticipantStatus`：

| 状态 | 说明 |
| --- | --- |
| `Active` | 正常参与调度 |
| `Standby` | 暂停发言，保留在参与者列表中 |
| `Removed` | 从会议中移除，不再参与调度 |

参与者在 Session 创建时配置。状态可在运行期通过事件动态调整。

### 3.1 按参与者 `SkillIds` 过滤技能目录

Meeting 四条 invocation 路径（Participant / Summarizer / Host / Selector）在写入 `InvocationSnapshot` 前，各自对 Catalog 调用 `FilterBySkillIds(agent.SkillIds)`：

- `null`：该角色看到全量 Catalog
- `[]`：该角色 Skills 为空
- 非空：仅名单内技能

同一会议内不同角色可绑定不同技能集。细节见 [12-Skills系统](./12-Skills系统.md) §5.1。

### 3.2 宿主模式 Skills 与工具 live catalog

宿主集成（`isHostModeSkills`）时：

- Run 级共享 `SkillActivationStore`；各参与者 `AgentRef.skillDiscoveryMode` 随手写 JSON 往返（`progressive` / `direct`）。
- tool-result 成功后走 `HostModeSkillsSnapshot.TryApplyToolResult`（progressive 激活；direct 钩子 no-op）。
- 工具循环使用 **live catalog**（`liveCatalogProvider` → `ToolCatalogStore.CaptureSnapshot()`，可附带内部 skill script 描述符），而非仅 Run 起始冻结快照。
- 每轮工具结果写回后刷新 `AvailableTools`：
  - **全量暴露**（启动时已暴露目录全部工具）：跟随 live 增长（search/browse 注入后下一轮可见新短名）
  - **Allowlist**（`AllowedToolIds` 非空）：只刷新已允许 ToolId 的描述符，**不**扩大集合；`initial` 快照缺失时**不**推断为全量

Skills 总述见 [12-Skills系统](./12-Skills系统.md) §8；宿主接入见 [04-宿主集成指南](./04-宿主集成指南.md) §10.9–§10.10。

## 4. 选择策略

`MeetingSelectorPolicy` 决定每轮调度的发言顺序：

| 策略 | 说明 |
| --- | --- |
| `RoundRobin` | 按参与者列表循环轮转，无需 Selector 或 Host 介入 |
| `SelectorDriven` | 由 Selector 角色的 Agent 选择下一个发言者 |
| `HostDriven` | 由 Host 角色的 Agent 控制发言顺序 |

`SelectorDriven` 是默认策略，每轮需要一次额外的模型调用来做选择决策；`HostDriven` 同样每轮需要一次额外的模型调用。`RoundRobin` 按参与者列表循环轮转，不依赖额外的 LLM 调用。

## 5. 上下文策略

`MeetingContextStrategy` 控制每个参会者看到的上下文范围：

| 策略 | 说明 |
| --- | --- |
| `Full` | 完整历史，所有发言均可见 |
| `TailWindow` | 仅保留最近 N 条消息 |
| `SlidingWindow` | 滑动窗口，随轮次推进 |
| `SummaryCompressed` | 用摘要压缩早期历史，保留近期原文 |

上下文策略影响 Token 消耗和模型理解质量。长会议建议使用 `SummaryCompressed` 避免上下文溢出。

## 6. 摘要模式

`MeetingSummaryMode` 控制是否生成轮次摘要：

| 模式 | 说明 |
| --- | --- |
| `None` | 不生成摘要 |
| `PerRound` | 每轮结束后生成轮次摘要 |
| `FinalOnly` | 仅在会议结束时生成最终摘要 |

当摘要生成失败时，`MeetingSummaryFallbackStrategy` 决定降级行为：

| 降级策略 | 说明 |
| --- | --- |
| `TailWindow` | 用尾部窗口替代摘要 |
| `Full` | 回退到完整历史 |
| `Fail` | 直接失败，不降级 |

`MeetingSummaryFallbackStrategy` 是 `MeetingContextPolicy.SummaryFailureFallback` 字段（默认 `TailWindow`），属于上下文投影层的默认降级选项，只描述“摘要不可用时用什么投影替代”。它与 §8.4 的角色失败策略 `MeetingSummarizerFailurePolicy` 各自独立、协同生效，二者的职责边界见 §8.4。

## 7. HITL（Human-in-the-Loop）

会议中的 HITL 通过 `MeetingHitlAction` 枚举表达审批结果：

| 动作 | 说明 |
| --- | --- |
| `Approve` | 批准当前提案或发言 |
| `Reject` | 拒绝并取消会议（与 `Cancel` 走同一分支，不存在"重新处理"逻辑） |
| `Supplement` | 补充信息后继续 |
| `Cancel` | 取消当前会议 |

当 `HitlEnabled` 为 `true` 时，HITL 是一个在首轮开始前触发一次的门禁（gate），而非贯穿运行期、对每条提案或发言反复审批的循环。门禁通过后本次 Run 内不再触发。

流程：

1. 首轮调度前，Runtime 发出 `meeting.hitl.request` 事件（`ApprovalRequestId` 后缀为 `host-before-round`），携带待审批内容。
2. 会议进入 `WaitingForApproval` 状态，暂停调度。
3. 宿主或 CLI 返回 `meeting.hitl.response`，携带 `MeetingHitlAction`。
4. Orchestrator 直接根据审批结果处理：`Approve` 恢复调度并进入首轮；`Supplement` 持久化补充信息后继续；`Reject` 与 `Cancel` 取消会议。

门禁的审批结果由 orchestrator 直接消费，而非交由 Host Agent 决策。

独立 CLI 模式下，HITL 请求阻塞等待用户在终端输入响应。

## 8. 失败策略

Meeting 模式为四种角色分别定义了独立的失败策略：

### 8.1 参会者失败 `MeetingParticipantFailurePolicy`

| 策略 | 说明 |
| --- | --- |
| `Skip` | 跳过该参会者，继续下一位 |
| `Retry` | 重试该参会者的调用 |
| `Pause` | 暂停会议等待人工介入 |
| `Fail` | 终止会议 |

### 8.2 主持人失败 `MeetingHostFailurePolicy`

| 策略 | 说明 |
| --- | --- |
| `Pause` | 暂停会议 |
| `Retry` | 重试主持人调用 |
| `Fail` | 终止会议 |

### 8.3 选择器失败 `MeetingSelectorFailurePolicy`

| 策略 | 说明 |
| --- | --- |
| `DeterministicFallback` | 回退到 RoundRobin 顺序 |
| `RetryThenFallback` | 先重试，失败后回退 RoundRobin |
| `Pause` | 暂停会议 |
| `Fail` | 终止会议 |

### 8.4 摘要员失败 `MeetingSummarizerFailurePolicy`

| 策略 | 说明 |
| --- | --- |
| `FallbackTailWindow` | 用尾部窗口替代摘要 |
| `FallbackFull` | 回退到完整历史 |
| `RetryThenTailWindow` | 先重试，失败后用尾部窗口 |
| `Fail` | 终止会议 |

#### 与 `MeetingSummaryFallbackStrategy` 的职责边界

摘要失败涉及两个枚举，二者语义相近但作用层次不同，同时生效而非二选一：

- `MeetingSummarizerFailurePolicy`（本节，`MeetingPolicy.SummarizerFailure` 字段，默认 `FallbackTailWindow`）是 **Summarizer 角色的失败策略**，是唯一提供“先重试再降级”（`RetryThenTailWindow`）的一侧，决定摘要员调用失败后是否重试以及如何降级。
- `MeetingSummaryFallbackStrategy`（§6，`MeetingContextPolicy.SummaryFailureFallback` 字段，默认 `TailWindow`）是 **上下文投影层的默认降级选项**，只描述摘要不可用时改用哪种投影。

当摘要最终仍然失败时，orchestrator 同时读取两者并按“取更严格者”的逻辑合并：任一为 `Fail`（含 `SummaryFailureFallback == Fail`）即终止会议；否则任一要求完整历史（`FallbackFull` 或 `Full`）就回退到 `Full` 投影，其余情况回退到 `TailWindow`。因此角色策略负责“是否重试与角色侧降级偏好”，上下文策略负责“投影层的默认降级底线”，两者叠加得到最终降级结果。

## 9. 结论条件

`MeetingConclusionCondition` 是单个枚举值（非 `[Flags]`，不可组合），默认 `HostDecides`：

| 条件 | 说明 |
| --- | --- |
| `MaxRounds` | 达到最大轮次数后自动结束 |
| `HostDecides` | Host Agent 发出结论信号 |
| `ConsensusReached` | 参会者达成共识（当前 orchestrator 未消费此值） |

需要注意：`MeetingConclusionCondition` 除作为 `MeetingPolicy.ConcludeCondition` 的默认字段值外，orchestrator 并未按该枚举分支来决定终止；`ConsensusReached` 目前没有任何消费点。实际的会议终止由以下机制驱动，满足任一即结束：

- **达到最大轮次**：当前轮为最后一轮（`isLastRound`）。
- **会议超时**：超过 `MeetingTimeoutSeconds`（`HasMeetingTimedOut`）。
- **终止条件文本匹配**：参会者输出命中 `MeetingPolicy.TerminationConditions` 中的字符串（`MatchesTerminationCondition`）。
- **Host 结论决策**：`HostDriven` 下 Host 输出 conclude 决策。

结束后进入摘要阶段（如配置了 `FinalOnly` 摘要）。

## 10. 状态机

### 10.1 会议状态 `MeetingSessionStatus`

```text
                      ┌─ Approve / Supplement ──────────→ Running
NotStarted ─┬─ 门禁 ─→ WaitingForApproval ─┤
            │                             └─ Reject / Cancel ─→ Cancelled
            └──────────(HITL 未启用)──────────────────────────→ Running

Running ─→ Summarizing ─→ Completed
Running ─────────────────→ Completed（摘要模式为 None，跳过 Summarizing）
Summarizing ─→ Failed（摘要失败且降级策略为 Fail，见 §8.4）
Running ─→ Paused（故障或人工介入）
Running / Paused ─→ Failed / Cancelled
```

`WaitingForApproval` 是首轮开始前触发一次的 HITL 门禁（见 §7），并非运行期反复审批：`HitlEnabled` 为 `true` 时会议从 `NotStarted` 先进入 `WaitingForApproval`，`Approve`／`Supplement` 后进入首轮 `Running`，`Reject`／`Cancel` 直接进入 `Cancelled`（不经 `Paused`）；`HitlEnabled` 为 `false` 时直接进入 `Running`。运行期的故障或人工介入才会使 `Running` 进入 `Paused`。

`Summarizing` 为可选阶段：摘要模式为 `None` 时 `Running` 直接进入 `Completed`；进入 `Summarizing` 后若摘要生成失败且降级策略（`MeetingSummarizerFailurePolicy` 或 `MeetingSummaryFallbackStrategy`）为 `Fail`，则终止为 `Failed`，其余降级策略仍进入 `Completed`。

| 状态 | 说明 |
| --- | --- |
| `NotStarted` | 已创建，尚未开始第一轮 |
| `Running` | 正常调度执行中 |
| `WaitingForApproval` | 等待 HITL 审批 |
| `Paused` | 因故障或人工介入暂停 |
| `Summarizing` | 正在生成最终摘要 |
| `Completed` | 正常结束 |
| `Failed` | 因不可恢复错误终止 |
| `Cancelled` | 被用户或宿主取消 |

### 10.2 单次调度状态 `MeetingInvocationStatus`

| 状态 | 说明 |
| --- | --- |
| `Scheduled` | 已排入调度，等待执行 |
| `Running` | 正在执行模型调用 |
| `Completed` | 本轮完成 |
| `Skipped` | 被选择策略跳过 |
| `Interrupted` | 被外部中断 |
| `Failed` | 调用失败 |

## 11. 恢复

`MeetingResumeState` 持久化当前轮次状态，包括：

- 当前轮次编号和已完成的调度记录
- 各参与者的 `ParticipantStatus`
- 未完成的 HITL 请求
- 摘要生成进度

断线重连后，Runtime 从 `MeetingResumeState` 恢复会议。处于 `Running` 状态的调度在重连后重新执行，已 `Completed` 的调度不重复。

---

## 修订记录

| 日期 | 说明 |
| --- | --- |
| 2026-09-07 | §3.1 SkillIds 过滤；§3.2 宿主 Skills + Meeting live catalog / AvailableTools 刷新 |
