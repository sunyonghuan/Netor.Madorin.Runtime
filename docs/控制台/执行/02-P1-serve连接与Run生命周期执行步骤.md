# P1 · serve 连接与 Run 生命周期执行步骤

> 阶段状态：完成
>
> 当前进度：100%
>
> 阶段目标：用 Client SDK 以合规宿主身份连接或拉起 Runtime，提交 Run，消费事件流，并按 GSN 正确确认。
>
> 来源策划：`05-serve宿主深度测试设计` 第 1-4、10 节，`00-总体策划` 第 2、7 节。

P1 是控制台测试宿主的主线能力。完成 P1 后，测试台必须能像真实宿主一样完整跑通一个 Run，而不是通过 `ctl` 或内部服务绕过协议。

## 1. 阶段任务总表

| 任务 | 当前进度 | 状态 | 产出 |
| --- | ---: | --- | --- |
| P1-01 Client SDK 依赖接入与边界检查 | 100% | 完成 | 合规引用，禁止内部依赖 |
| P1-02 HostClientFactory 与 RuntimeClientOptions | 100% | 完成 | 三种 StartPolicy |
| P1-03 InstanceBinding 展示与校验 | 100% | 完成 | 绑定字段断言 |
| P1-04 RuntimeClient State/Completion 观测 | 100% | 完成 | 连接状态与异常投影 |
| P1-05 Run 请求向导与提交 | 100% | 完成 | 新建/继续 Session Run |
| P1-06 事件流消费与 GSN 确认 | 100% | 完成 | ReadEvents + Acknowledge |
| P1-07 Run 状态投影与查询 | 100% | 完成 | 状态、Phase、计数、错误脱敏 |
| P1-08 取消 Run | 100% | 完成 | Cancel accepted 与终态区分 |
| P1-09 P1 验收用例 | 100% | 完成 | 完整 Run 生命周期证明 |

## 2. P1-01 Client SDK 依赖接入与边界检查

当前进度：100%

执行步骤：

1. 修改 C# 文件前先刷新/确认 CodeMap 工作区。
2. 检查 `tools/Madorin.AI.Runtime.MConsole/` 是否有 README；如果没有，检查父目录。
3. 参考 `samples/Madorin.AI.Runtime.SampleHost` 的项目引用方式。
4. 在 MConsole 工程中引用 `Madorin.AI.Runtime.Client`。
5. 仅在必要时引用 `Madorin.AI.Runtime.Contracts`。
6. 确认项目没有引用 Provider、Server、Persistence、SQLite、MAF/MEAI。
7. 如果出现必须引用内部项目才能完成需求的情况，停止实现并记录 SDK 暴露不足。

完成标准：

- 工程可以编译。
- 依赖树中没有 Runtime 内部实现项目。
- 代码中没有读取 Runtime 数据目录的逻辑。

执行记录：

```text
状态：完成
进度：100%
记录：MConsole 只引用 Client 与 Contracts；CodeMap 无 baseline 时按规则降级精确读取公共源码。
```

## 3. P1-02 HostClientFactory 与 RuntimeClientOptions

当前进度：100%

组件：`HostClient/HostClientFactory.cs`

需要支持的 StartPolicy：

| StartPolicy | 行为 | 本阶段测试 |
| --- | --- | --- |
| AttachExisting | 只连接现有 Runtime | 目标不存在时报连接失败 |
| StartIfMissing | 先连接，失败后拉起 | 已有实例走 attach；无实例走 start |
| AlwaysStart | 始终起新实例 | 可配 OwnedProcessClosePolicy |

执行步骤：

1. 定义测试台自己的连接配置模型，不直接暴露 SDK 内部对象到菜单层。
2. 映射 `WorkspaceDirectory`、`DataDirectory`、`LogDirectory`、`RuntimeInstanceId`、`PipePrefix`。
3. 映射 `RuntimeExecutablePath`，默认来自 P0 的 `MadorinLocator`。
4. 映射 `StartupTimeout`。
5. 映射 `OwnedProcessClosePolicy`：`KeepAlive` 与 `Shutdown`。
6. 映射加强校验项：`ExpectedRuntimeVersion`、`ExpectedRuntimeInstanceId`、`ExpectedServerProcessId`。
7. 创建 `RuntimeClient` 时注册 P2 预留的 HostCallbacks 空实现或占位实现。
8. 连接失败时保留 SDK 异常类型和消息，不伪造退出码。
9. 对 `AttachExisting` 不存在的情况，展示为连接异常。
10. 对 `StartIfMissing` 记录实际走的是 attach 还是 start 分支。

完成标准：

- 三种策略都能从菜单或最小命令入口触发。
- 连接结果能显示策略、实例、是否拥有进程。
- 失败信息稳定且不含 secret。

执行记录：

```text
状态：完成
进度：100%
记录：三种策略统一映射到 RuntimeClient.CreateAsync，真实 attach/start 分支已验证。
```

## 4. P1-03 InstanceBinding 展示与校验

当前进度：100%

需要展示的字段（Binding 未公开的协议版本通过 `GetRuntimeStatusAsync` 补充，capabilities 当前 SDK 不提供查询面，明确显示不可用而不伪造）：

```text
HostInstanceId
RuntimeInstanceId
WorkspaceDirectory
ServerProcessId
ProtocolVersion
Capabilities
OwnsProcess
```

执行步骤：

1. 连接成功后立即读取 `InstanceBinding`。
2. 显示规范化工作区路径。
3. 显示 Runtime PID、协议版本、实例 ID。
4. 如果配置了 ExpectedRuntimeInstanceId，校验不匹配时失败。
5. 如果配置了 ExpectedServerProcessId，校验不匹配时失败。
6. 如果配置了 ExpectedRuntimeVersion，校验不匹配时失败。
7. 绑定校验失败只关闭当前 client，不影响其他实例。
8. 将绑定信息写入测试台执行上下文，供后续 Run、监控、并发使用。

完成标准：

- 绑定字段可读。
- 加强校验能触发失败。
- 多实例时每个绑定独立保存。

执行记录：

```text
状态：完成
进度：100%
记录：Binding、Runtime status、请求策略、实际 attach/start 分支和 owns process 已形成稳定快照。
```

## 5. P1-04 RuntimeClient State/Completion 观测

当前进度：100%

执行步骤：

1. 当前 SDK 的 `State` 是只读属性而非事件；在连接、状态查询、Completion 和 Dispose 边界采集状态时间线。
2. 订阅 `Completion`。
3. 正常 `DisposeAsync` 时标记连接关闭。
4. Owned process 异常退出时投影 `RuntimeProcessExitedException`。
5. 展示 PID、ExitCode、ExitedAt、最后确认 GSN。
6. `AttachExisting` 的断开不伪造进程退出码，只报告连接状态。
7. Completion 异常中不得显示 secret。

完成标准：

- 状态变化可见。
- Runtime 异常退出能留下诊断。
- Dispose 与异常退出可区分。

执行记录：

```text
状态：完成
进度：100%
记录：状态时间线、Completion 正常结束和 RuntimeProcessExitedException 诊断投影已实现。
```

## 6. P1-05 Run 请求向导与提交

当前进度：100%

组件：`RunConductor`

执行步骤：

1. 支持新建 Session Run：`StartNewSessionRunAsync(NewSessionRunRequest)`。
2. 支持继续已有 Session Run：`StartExistingSessionRunAsync(ExistingSessionRunRequest)`。
3. 向导采集 `mode`，枚举值至少覆盖 `expert`、`meeting`、`work`。
4. 向导采集 provider/model/agent selection；当前 `NextTurnSelection` 契约要求非空 provider/model，提交前必须解析完成。
5. 向导采集输入内容。
6. 提交前展示请求摘要，但不显示 secret。
7. 提交成功后保存 `runId`、`sessionId`、Runtime 实例绑定。
8. 提交失败时保留 SDK 异常和请求摘要。

完成标准：

- 能提交新建 Run。
- 能提交继续已有 Session 的 Run。
- 返回的 runId 可用于后续事件消费和取消。

执行记录：

```text
状态：完成
进度：100%
记录：Expert/Meeting/Work 请求、新建 Session 和继续 Session 提交均已实现并测试。
```

## 7. P1-06 事件流消费与 GSN 确认

当前进度：100%

核心规则：

```text
只有测试台完成事件投影处理后，才能调用 AcknowledgeEventsAsync。
```

执行步骤：

1. 为每个 Runtime 维护独立游标 `(runtimeInstanceId, lastConfirmedGsn)`。
2. 调用 `ReadEventsAsync(lastConfirmedGsn, ct)`。
3. 对每个事件展示 RuntimeInstanceId、RunId、GSN、Run 内序号、事件类型。
4. 将文本 Delta、状态变化、工具事件分别投影到测试台视图。
5. 投影成功后调用 `AcknowledgeEventsAsync(item.Gsn, ct)`。
6. 更新本地 `lastConfirmedGsn`。
7. 水位只能递增，不能回退。
8. `lastConfirmedGsn` 不能超过已处理事件 GSN。
9. 提供“延迟确认”测试开关，用于 P3 重连演练。
10. 提供“提前确认”反面用例开关，但默认禁用并明确标注危险。
11. 多实例时即使 runId 或 GSN 数值相同，也按 Runtime 实例隔离。

完成标准：

- 能实时看到事件流。
- GSN 确认可观测、可记录。
- 重启测试台后能从持久化水位恢复读取位置。

执行记录：

```text
状态：完成
进度：100%
记录：严格执行投影成功、SDK 确认成功、本地游标原子持久化的顺序；按实例隔离且禁止回退。
```

## 8. P1-07 Run 状态投影与查询

当前进度：100%

需要投影的字段：

```text
Mode
Phase
CurrentStep
CompletedUnits
TotalUnits
UpdatedAt
LastRunSequence
Error
```

执行步骤：

1. 消费 `RunStatusChangedEvent` 并更新本地 Run 状态。
2. 使用 `QueryRunAsync` 获取单个 Run 最终状态。
3. 使用 `ListRunsAsync` 获取看板列表。
4. 计数断言：`CompletedUnits >= 0`。
5. 计数断言：`CompletedUnits <= TotalUnits`。
6. 旧 Runtime 缺失字段时按 `null` 或 `0` 处理。
7. Error 字段必须脱敏展示。
8. 文本 Delta 不写入进度列，避免混淆状态。

完成标准：

- Run 状态能从事件和查询两路一致投影。
- 终态可识别。
- 错误信息不泄露 secret。

执行记录：

```text
状态：完成
进度：100%
记录：事件投影、计数校验、终态识别、QueryRun 和 ListRuns 入口已实现。
```

## 9. P1-08 取消 Run

当前进度：100%

执行步骤：

1. 提供 `CancelRunAsync(runId)` 操作。
2. 展示返回的 accepted 结果。
3. 明确提示 accepted 只表示取消请求被接受，不等于 Run 已取消。
4. 继续消费事件或查询状态，直到观察到最终 cancelled 或其他终态。
5. 保留与 `ctl cancel` 对照的扩展点，实际对照可在 P4/P5 完成。

完成标准：

- 可对运行中的 Run 发起取消。
- accepted 与最终态被区分记录。
- 取消失败时有清晰错误。

执行记录：

```text
状态：完成
进度：100%
记录：取消 accepted 与查询到的当前/最终状态分开建模，不把 accepted 当作 cancelled。
```

## 10. P1-09 P1 验收用例

当前进度：100%

最小验收链：

1. 使用 `StartIfMissing` 连接测试工作区 Runtime。
2. 展示 InstanceBinding。
3. 新建 Session Run。
4. 消费事件流。
5. 每个事件处理成功后推进 GSN。
6. 查询 Run 直到终态。
7. 关闭 RuntimeClient。
8. 如果 owned process 策略为 Shutdown，确认 serve 进程被停止。

P1 阶段验收标准：

- 三种 StartPolicy 行为正确。
- 一个 Run 能完整执行到终态。
- GSN 水位单调推进。
- 游标按 Runtime 实例隔离。
- 取消操作能区分 accepted 和终态。
- 全程不越过 Client SDK 边界。

执行记录：

```text
状态：完成
进度：100%
记录：MConsole 22/22 测试、真实三策略连接冒烟和 ReferenceHost 确定性 E2E 均通过。
```

## 11. 2026-07-27 实际执行与验收记录

实施按 P1-01 到 P1-09 顺序完成。主要产出位于 `HostClient/`：`HostClientFactory`、`HostClientSession`、`RunConductor`、`RunRequestFactory`、`EventStreamConsumer`、`RuntimeCursorStore` 和 `RunEventProjector`。

验证证据：

```powershell
dotnet test .\tests\Madorin.AI.Runtime.MConsole.Tests\Madorin.AI.Runtime.MConsole.Tests.csproj -c Debug
dotnet run --project .\tools\Madorin.AI.Runtime.MConsole -c Debug --no-build -- `
  --smoke-p1 --runtime <madorin.exe> --workspace <owned-temp-workspace>
dotnet test .\tests\Madorin.AI.Runtime.EndToEnd.Tests\Madorin.AI.Runtime.EndToEnd.Tests.csproj `
  -c Debug --no-build `
  --filter "FullyQualifiedName~ReferenceHost_ThreeRuntimes_DemonstratesStage7HostWorkflow"
```

结果：MConsole 测试 22/22；AttachExisting=`attach`；StartIfMissing 对现有实例=`attach`、对缺失实例=`start`；AlwaysStart=`start`；owned processes 全部停止；确定性 ReferenceHost E2E 1/1 通过。

复检结论：GSN 只在投影成功后确认，持久化水位不超前且不回退；运行时游标按 `runtimeInstanceId` 隔离；取消请求接受状态与 Run 终态严格区分；未引入 Provider、Server、Persistence 或 SQLite 项目引用。
