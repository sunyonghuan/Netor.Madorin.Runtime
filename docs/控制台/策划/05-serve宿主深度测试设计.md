# Madorin 控制台测试宿主 · serve 宿主深度测试设计（重点）

> 文档状态：策划草案，未实现
>
> 编写日期：2026-07-27
>
> 上级文档：[00-总体策划](./00-总体策划.md)
>
> 契约依据：[宿主集成指南](../../开发文档/04-宿主集成指南.md)（协议 1.1，Client V1）
>
> 参考实现：`samples/Madorin.AI.Runtime.SampleHost`（`reference-host` 与 `MultiInstanceHost` 双 serve 实例）

这是本策划的**重点文档**。控制台充当宿主，核心职责是像真实宿主那样通过 **Client SDK（`Madorin.AI.Runtime.Client.RuntimeClient`）** 连接 `serve` 实例，完整走一遍宿主生命周期与协议交互，并把每一步做成可人工操作、可断言、可录制的测试面板。独立命令（`run`/`config`/`db`/…）是次要层，见 01/02 文档。

---

## 1. 为什么 serve 必须走 Client SDK，而不是 ctl

`ctl` 子命令只能看到 Runtime 的**表层状态**（status/sessions/runs/cancel/credential），本质是薄的运维视图。而宿主真正要验证的是**协议级行为**，只有 Client SDK 暴露：

| 宿主能力 | Client SDK | ctl 能否覆盖 |
| --- | --- | --- |
| 提交 Run（新建/继续 Session） | `StartNewSessionRunAsync` / `StartExistingSessionRunAsync` | 否 |
| 事件流消费 + 确认水位 | `ReadEventsAsync` + `AcknowledgeEventsAsync`（GSN） | 否 |
| 工具授权/审批/调用/结果查询/取消 | `RuntimeHostCallbacks` 5 类回调 | 否 |
| 工具目录发布 | `ReplaceToolCatalogAsync` / `PatchToolCatalogAsync` | 否 |
| 断线重连与恢复 | `ReconnectAsync` + `RehydrateSessionAsync` / `ResumeSessionAsync` | 否 |
| 凭据续期 | `UpdateCredentialsAsync` | 部分（ctl credential 是薄封装） |
| Grant 撤销 | `RevokeGrantAsync` | 否 |
| 实例绑定校验 | `InstanceBinding`（PID/实例/协议/能力） | 否 |

结论：serve 层的测试台组件 `HostClient/` 直接引用 Client SDK，`ctl` 仅作为次要层的旁路验证（比如对照 Client 看到的 Run 列表与 ctl 看到的是否一致）。

---

## 2. 依赖边界与合规宿主

严格遵循[宿主集成指南](../../开发文档/04-宿主集成指南.md)第 1 节，确保测试台本身是**合规宿主**，测的是真实集成路径：

- 只引用 `Madorin.AI.Runtime.Client` 和必要的 `Madorin.AI.Runtime.Contracts`。
- **不引用** Provider 实现、MAF/MEAI、SQLite、Persistence、Server 项目。
- 不解析 Named Pipe 帧、不扫描 Runtime 数据目录、不访问内部 DI。
- 一个测试台进程可持有多个 `RuntimeClient`，每个句柄固定绑定一个实例；查询、取消、事件游标、回调都以该绑定为边界，绝不只按 `runId` 跨实例查找。
- 握手 secret 经重定向 stdin 传递（SDK 负责），不进命令行/环境变量/日志/异常消息。

> 这条边界本身就是被测项：如果测试台不得不越界才能验证某事，说明 SDK 暴露不足，应记为缺陷，而不是在测试台开后门。

---

## 3. 连接 / 拉起 serve（菜单第 1 项）

对应 `HostClientFactory`。构造 `RuntimeClientOptions` 并按 `StartPolicy` 建立 `RuntimeClient`，测试三种启动策略：

| StartPolicy | 行为 | 测试要点 |
| --- | --- | --- |
| `AttachExisting` | 只连现有 Runtime，不存在即失败 | 先手工/菜单 `serve` 起实例，再 attach；目标不存在时应报连接异常，不伪造退出码 |
| `StartIfMissing` | 优先连，连不上才起新进程 | 连已有实例走连接分支；无实例走拉起分支 |
| `AlwaysStart` | 始终起新独立 Runtime | 多工作区/强隔离；`OwnedProcessClosePolicy` 决定释放时是否关进程 |

关键选项与向导项：`WorkspaceDirectory`、`DataDirectory`、`LogDirectory`、`RuntimeInstanceId`、`PipePrefix`、`RuntimeExecutablePath`、`StartupTimeout`、`OwnedProcessClosePolicy`（`KeepAlive`/`Shutdown`），以及可选的加强校验 `ExpectedRuntimeVersion` / `ExpectedRuntimeInstanceId` / `ExpectedServerProcessId`。

连接成功后展示并断言 `InstanceBinding`：`HostInstanceId`、`RuntimeInstanceId`、规范化工作区、Runtime PID、是否拥有进程。同时订阅 `State` 与 `Completion`：
- `Completion` 正常 `DisposeAsync` 后成功完成；
- owned process 退出时以 `RuntimeProcessExitedException`（含 PID/ExitCode/ExitedAt/最后确认 GSN）完成；
- `AttachExisting` 不伪造退出码，只报连接异常。

**断言点**：三策略行为正确；绑定字段与实际一致；加强校验不匹配时按稳定 `RuntimeClientException` 派生类型失败。

---

## 4. 会话与 Run 生命周期（菜单第 2 项）

对应 `RunConductor`，是宿主最核心的循环。

### 4.1 提交 Run
- 新建：`StartNewSessionRunAsync(NewSessionRunRequest)`；继续：`StartExistingSessionRunAsync(ExistingSessionRunRequest)`。
- 向导采集 mode、provider/model/agent selection、输入内容。返回 `runId`。

### 4.2 消费事件流并确认水位
```
await foreach (var item in client.ReadEventsAsync(lastConfirmedGsn, ct))
{
    // 业务投影/持久化成功后才确认
    await client.AcknowledgeEventsAsync(item.Gsn, ct);
}
```
- 事件显式带 `runtimeInstanceId`、`runId`、GSN、Run 内序号。测试台把事件流实时呈现（文本 Delta、状态变化、工具事件）。
- **确认水位规则**：只有在测试台的投影处理成功后才 `AcknowledgeEventsAsync`。提供“延迟确认/提前确认”开关，用来验证：提前确认会导致重连后无法重放未处理事件（这是被测的反面用例）。
- 每个 Runtime 单独保存 `(runtimeInstanceId, lastConfirmedGsn)`；即便多实例产生相同 `runId`/GSN 数值也**绝不合并游标或路由表**——测试台按实例分别持久化游标。

### 4.3 Run 状态投影
消费 `RunStatusChangedEvent` 与 `QueryRunAsync`/`ListRunsAsync` 的追加字段：`Mode`、固定词汇 `Phase`、`CurrentStep`、`CompletedUnits`/`TotalUnits`、`UpdatedAt`、`LastRunSequence`、脱敏 `Error`。
- **断言点**：计数非负且已完成数 ≤ 总数；文本 Delta 不写进度列；旧 Runtime 缺字段按 `null`/`0` 处理。

### 4.4 取消
`CancelRunAsync(runId)` 请求取消，返回是否 accepted；最终 `cancelled` 由后续事件/查询确认。可对照次要层 `ctl cancel` 的表现。

**验收**：能完整跑完一个 Run（提交 → 事件流 → 终态），确认水位单调推进，取消行为符合“accepted ≠ 已取消”。

---

## 5. 宿主回调应答台（菜单第 3 项）

对应 `HostCallbackPanel`。`RuntimeHostCallbacks` 有 5 类回调，通过 `RuntimeClientOptions.HostCallbacks` 注册。SDK 对回调统一施加有界并发（`MaxConcurrency`）、排队/执行超时（`CallbackTimeout`）和取消映射；单个回调异常转成协议错误响应，不终止事件循环。

| 回调 | 请求类型 | 测试台的人工应答 |
| --- | --- | --- |
| 工具权限 | `ToolPermissionRequest` | 人工选择授予/拒绝，保留 Runtime/Run/Agent/Grant/委派归属 |
| 审批 | `ApprovalRequest` | 人工批准/否决 |
| 工具调用 | `ToolCallRequest` | 执行工具并回结果；按稳定 `callId` **幂等** |
| 工具结果查询 | `ToolResultQueryRequest` | 重连/超时后查询已持久化结果，**不重新执行副作用** |
| 工具调用取消 | `ToolCallCancelRequest` | 取消进行中的工具执行 |

### 5.1 人工应答模式
回调到来时，测试台在应答台展示请求详情，由测试者人工决定响应（授予/拒绝、批准/否决、给工具结果）。同时提供**预设策略**（自动授予全部、自动拒绝、按工具名白名单）便于批量场景。

### 5.2 幂等与副作用（重点被测）
- 有副作用的工具调用按 `callId` 幂等：同一 `callId` 再次到达时，测试台必须返回**首次持久化结果**而非重复执行。测试台内部维护 `callId → 结果` 表来验证这一点。
- 重连或超时后，Runtime 会用 `ToolResultQueriedAsync` 查询结果——测试台应从该表返回，**演练“不因重放而重复执行副作用”**。这是宿主集成最容易出错、也最该测的点。

### 5.3 并发与超时
- 设置 `MaxConcurrency`（如 4）与 `CallbackTimeout`（如 30s），验证：超过并发数的回调排队；超时回调被 SDK 转为协议错误但事件循环存活；故意让一个回调抛异常，确认只影响该回调、不终止整个循环。

**验收**：5 类回调都能人工应答并断言归属字段；幂等表命中时不重复执行副作用；并发/超时/异常隔离符合契约。

---

## 6. 工具目录发布（菜单第 4 项）

对应 `ToolCatalogPanel`。宿主通过 `ReplaceToolCatalogAsync`（整表替换）或 `PatchToolCatalogAsync`（增量补丁）向 Runtime 发布可用工具目录，返回 `ToolCatalogUpdateResponse`。

- 向导编辑一个工具目录（工具名、描述、参数 schema），发布后观察后续 Run 的工具事件是否反映新目录。
- 测试增量 patch 与整表 replace 的差异；`RevokeGrantAsync` 撤销某个已授予的 Grant 后，验证该工具调用被拦截。

**验收**：目录发布即时生效于下一轮；replace 与 patch 语义正确；撤销 Grant 后授权被正确拒绝。

---

## 7. 断线重连与恢复演练（菜单第 5 项）

对应 `ReconnectDrill`。这是宿主韧性的核心，按[宿主集成指南第 7 节](../../开发文档/04-宿主集成指南.md)的恢复顺序演练。

### 7.1 触发断线
- 主动 `ReconnectAsync`；或对 owned process 场景，杀掉/重启 serve 进程模拟 Runtime 重启（配合 04 文档的 `ServeHostPool`）。

### 7.2 恢复顺序（逐步可断言）
1. 用原实例信息重连，校验 PID、认证、实例、协议、能力。
2. 从持久化的 `lastConfirmedGsn` 调 `ReadEventsAsync` 重放。
3. 对“至少送达一次”的事件按 `(runtimeInstanceId, gsn)` **去重**。
4. 业务处理成功后再推进确认水位。
5. 查询 Run 终态；Runtime 已重启时按 Session 执行 `RehydrateSessionAsync` / `ResumeSessionAsync`，再从已确认 GSN 继续。

### 7.3 被测反面用例
- **断线期间不要仅凭本地超时把 Run 判失败**：测试台演练“等待确认”状态而非伪造终态。
- **重放不重复执行工具副作用**：结合第 5.2 节的幂等表，验证重放的工具事件命中已有结果。
- 提前确认（第 4.2 节）后断线，验证未处理事件无法重放——确认这是错误用法。

**验收**：重连后校验链完整；事件按 `(instance, gsn)` 去重；水位只在处理成功后推进；rehydrate/resume 后能继续；副作用不因重放而重复。

---

## 8. 凭据续期（菜单第 6 项）

对应 `CredentialPanel`。`UpdateCredentialsAsync` 向运行中的 Runtime 推送新凭据，用于 Provider API Key 续期。

- 新 Key 只从环境变量读取，经 SDK 传递，**不回显、不入用例、不入历史、不入日志**。
- 场景：一个 Run 因凭据过期/缺失进入等待凭据状态 → 通过 `UpdateCredentials` 推送新 Key → 验证该 Run 恢复推进。
- 可对照次要层 `ctl credential update`（薄封装）的行为一致性。

**验收**：凭据推送后等待中的 Run 能恢复；Secret 全程不落明文；归属（Runtime/Run/Provider/Profile）正确。

---

## 9. 与其他文档的衔接

- **多实例并发**（多个 `RuntimeClient` 各绑定一个实例、每实例独立刷新与事件循环、游标不合并）：见[04-并发与多实例测试设计](./04-并发与多实例测试设计.md)。本文的宿主循环是其单实例基础。
- **进程模型 / secret 闭环 / 隔离数据目录**：见[00 文档](./00-总体策划.md) 2.2 / 2.3。serve 进程的拉起与停止由 `ServeHost` 提供。
- **断言 / 用例录制 / 夹具**：见[03 文档](./03-测试增强功能设计.md)。serve 场景在其断言类型上增加**事件断言**（某 GSN 前出现某状态、水位单调）与**回调断言**（某 callId 只执行一次），并可把“连接→提交 Run→应答回调→断线重连→校验”整条链录成用例回放。
- **升级/回退演练**（可选延伸）：宿主集成指南第 9 节的版本升级流程（排空 Run、双向校验、rehydrate、冒烟后切流量）可作为高级 serve 场景，后续增补。

---

## 10. 落地优先级（serve 重点，对齐 00 文档第 7 节）

| 阶段 | 组件 | 产出 |
| --- | --- | --- |
| P0（地基） | `ServeHost` 拉起/停止 serve + secret 注入 | 能起 serve 供连接 |
| P1 | `HostClientFactory` + `RunConductor` | 完整跑一个 Run 并按 GSN 消费事件 |
| P2 | `HostCallbackPanel` + `ToolCatalogPanel` | 工具授权/审批/调用闭环、目录发布 |
| P3 | `ReconnectDrill` + `CredentialPanel` | 重连恢复、幂等副作用、凭据续期 |
| P6 | 多实例（`ServeHostPool`，见 04） | 并发宿主场景 |

验收基线：测试台能以合规宿主身份连接/拉起 serve，提交 Run 并按 GSN 确认消费完整事件流；5 类回调可人工应答且幂等；断线后能重连、去重、rehydrate、从游标继续且不重复执行工具副作用；凭据续期后等待中的 Run 能恢复；全程 Secret 不落明文、依赖边界不越界。