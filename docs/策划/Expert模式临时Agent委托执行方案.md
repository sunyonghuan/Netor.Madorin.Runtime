# Expert 模式临时 Agent 委托执行方案

> 日期：2026-10-11
> 状态：2026-10-12 完整 slnx 构建及 8 项目回归完成：1161 通过、0 失败、15 原有跳过；原 30 项含明确迁移映射全部通过，ReferenceHost 已恢复执行。ctl 历史归因及远端/平台边界仍见第 10 节。
> 范围：Expert 模式、小型计划、同步委托、并行 Agent Job

## 1. 目标

在现有 Expert 委托链上增加一次性临时 Agent：

1. 优先从 `AvailableAgents` 匹配已有 Agent。
2. 未匹配时，由主 Agent 创建一个临时 Agent。
3. 临时 Agent 只执行当前任务，完成后立即释放。
4. 临时 Agent 不写入 `.madorin/agents/`，不进入全局 Agent 池。
5. 临时 Agent 不参与计划恢复，只把执行结果和摘要写入步骤结果。
6. 简单只读搜索任务支持并行创建 3-5 个临时 Agent。

## 2. 非目标

- 不实现永久 Agent 的运行时注册。
- 不持久化临时 Agent 的完整提示词、模型配置或权限定义。
- 不允许临时 Agent 创建其他 Agent。
- 不允许临时 Agent 默认写文件、删文件、执行进程或访问网络。
- 不改变已有 CLI Agent 配置格式。

## 3. 委托策略

```text
委托请求
  -> 按 agentName、DisplayName、Description、Capabilities 匹配已有 Agent
  -> 匹配成功：使用已有 Agent
  -> 未匹配且 createIfMissing=false：返回 Agent not found
  -> 未匹配且 createIfMissing=true：创建临时 Agent
  -> 执行任务
  -> 返回结果
  -> 释放临时 Agent
```

默认策略：

- 普通计划步骤：主 Agent 执行。
- 指定已有 Agent：优先使用已有 Agent。
- 指定 `createIfMissing=true`：允许创建临时 Agent。
- 代码搜索和目录扫描：允许并行临时 Agent。

## 4. 契约调整

扩展 `AgentRef` 的能力描述字段：

```csharp
string[]? Capabilities = null
```

扩展 `mcli_delegate_agent` 参数：

```json
{
  "agentName": "code-reviewer",
  "task": "检查 README 的命令示例",
  "createIfMissing": true,
  "temporary": true,
  "role": "代码审查助手",
  "capabilities": ["filesystem.search", "documentation.review"],
  "allowedToolIds": ["mcli_fs_read", "mcli_fs_search", "mcli_fs_inspect"],
  "timeoutMs": 120000
}
```

兼容规则：

- 只有 `agentName` 和 `task` 的旧请求继续走已有 Agent 匹配。
- `temporary` 必须为 true 才能触发临时创建。
- `createIfMissing` 默认 false，防止错误名称静默创建 Agent。
- 临时 Agent 使用主 Agent 的 provider/model，不能由模型扩大权限。

## 5. 本次真实验收结果

最近一次三文件真实验收路径：`C:\Users\Administrator\AppData\Local\Temp\MadorinRealCliPlan-20261012-000401`。

源码 CLI 返回成功，三个文件精确内容分别为 `alpha`、`beta`、`gamma`，均为无换行的 5/4/5 字节。诊断程序没有使用模型文本作为 PASS 条件，而是验证文件内容、计划状态及 `tool_intents` 与 `tool_grants` 的关联：

- 临时 Agent：`temp-f21c42e29db04f5eb595d5caab826055`
- `parent_agent_id=planner`
- 子 invocation：`a9555084613744129bac3fb1305764c7`
- gamma 写入 call：`call_00_mApyHWI0wW3wvl1Y2oc61041`
- status：`succeeded`
- Grant Agent 与 intent Agent 相同
- `AllowedCallIds` 精确包含该 call
- `AllowedWriteRoots` 覆盖 `temp-output/gamma.txt`
- Grant 与 intent 的 `approval_request_id` 相同
- 返回结果 `written=true`、`mode=create`、`byteLength=5`，SHA256 与实际 gamma 文件一致
- 主 Agent gamma 写入证据为零，诊断输出 `childGamma=True|parentGamma=False`

测试配置副本在每次运行结束时删除，输出包含 `CREDENTIAL_COPY|deleted`。此前完整 Provider 套件的 `404 passed, 23 failed, 15 skipped` 结果保留，本轮未重复运行。

## 5. 临时 Agent 工厂

新增内部 `TemporaryAgentFactory`，输入：

- 主 Agent
- role
- task
- capabilities
- allowedToolIds
- timeoutMs

输出一个内存中的 `AgentRef`：

- AgentId：`temp-{guid}`
- SystemPrompt：由固定模板、role、task 组成
- ProviderId/ModelId：继承主 Agent
- AllowedToolIds：显式请求与主 Agent 工具列表求交集；未指定请求时默认只读；宿主权限回调仍逐次验证工具和文件路径
- 不写入可复用的 Agent 配置或计划中的临时定义

临时 Agent 的系统提示必须明确：

- 只完成当前任务。
- 不创建其他 Agent。
- 仅在请求、主 Agent 和宿主都允许时执行写入或删除。
- 不执行未授权工具。
- 返回结构化、简洁、可合并的结果。

## 6. 权限模型

临时 Agent 权限必须满足：

```text
临时 Agent 权限 <= 主 Agent 权限
```

代码搜索默认只允许：

- `mcli_fs_read`
- `mcli_fs_search`
- `mcli_fs_inspect`
- `mcli_fs_list`

临时 Agent 可以在主 Agent 已允许且请求显式列出的范围内使用：

- `mcli_fs_mkdir`
- `mcli_fs_write`
- `mcli_fs_append`
- `mcli_fs_edit`
- `mcli_fs_delete`

禁止临时 Agent 使用：

- 文件复制、移动
- 进程执行
- HTTP 请求
- memory 写入
- `mcli_delegate_agent`
- Agent Job 创建
- 计划创建和计划放弃

授权范围仍由现有宿主权限系统解析，临时 Agent 不绕过审批。

## 7. 并行执行

复用现有 Agent Job：

1. 主 Agent 将搜索范围拆成 3-5 个独立任务。
2. 调用 `mcli_agent_job_create_many`。
3. 每个 job 使用已有匹配 Agent 或一次性临时 Agent。
4. 调用 `mcli_agent_job_wait`。
5. 主 Agent 汇总、去重并写入当前计划步骤结果。

并行临时 Agent 有以下限制：

- 默认最多 5 个。
- 每个任务独立 workspace 读取范围。
- 总超时由父任务控制。
- 子任务不得写入共享文件。
- 失败任务返回错误摘要，不影响其他任务结果收集。

## 8. 小型计划集成

计划步骤继续保存 `agentName`，增加运行时策略字段时保持向后兼容：

```text
PrimaryAgent
ExistingAgentOnly
TemporaryAgentIfUnmatched
PreferParallelTemporaryAgents
```

第一阶段不要求计划管理器持久化临时 Agent 定义。计划只保存：

- 步骤状态
- 执行结果摘要
- executionMode
- temporaryAgentCount

执行流程：

```text
开始步骤
  -> 判断步骤策略
  -> 已有 Agent 匹配则委托已有 Agent
  -> 未匹配且允许临时 Agent 则创建并执行
  -> 并行任务等待并合并
  -> 由主 Agent 调用 execute_step 标记结果
```

## 9. 实施阶段

### Phase 1：契约与匹配（已完成）

- [x] 增加 Agent capabilities 描述。
- [x] CLI 将 Agent capabilities 传入 `AgentRef`。
- [x] 增加已有 Agent 的 ID、显示名和 capabilities 匹配。
- [x] 保持旧 `agentName` 委托行为不变。
- [x] 为委托参数增加可选临时 Agent 字段。
- [x] 新增临时 Agent 工厂的只读权限收缩。
- [x] 临时 Agent 工厂测试与已有委托回归测试通过。

### Phase 2：临时 Agent 工厂（已完成）

- [x] 实现内存临时 Agent 工厂。
- [x] 实现权限求交集和只读默认白名单。
- [x] 接入同步 `mcli_delegate_agent` 的真实临时执行路径。
- [x] 临时 Agent 执行后释放引用的路径不写入配置和计划持久化。
- [x] 临时 Agent 工厂与同步委托回归测试通过。

### Phase 3：并行 Agent Job（已完成）

- [x] `mcli_agent_job_create` 和 `create_many` 支持临时 Agent 请求字段。
- [x] Job Worker 优先匹配已有 Agent，未匹配时按显式策略创建临时 Agent。
- [x] 保持旧 `AgentJobWorker` 接口兼容。
- [x] 沿用现有每 Run 作业容量和并行数量限制。
- [x] 并行 Job 回归测试通过。

### Phase 4：小型计划（已完成）

- [x] 计划步骤支持 `executionPolicy` 字段。
- [x] 支持 `PrimaryAgent`、`ExistingAgentOnly`、`TemporaryAgentIfUnmatched`、`PreferParallelTemporaryAgents` 策略值。
- [x] 计划步骤保留已有 `agentName` 兼容行为。
- [x] 计划 DTO 向后兼容，旧计划可继续反序列化。

### Phase 5：真实宿主验收（已完成）

- [x] 真实模型创建包含临时 Agent 委托的计划。
- [x] 真实模型通过 `createIfMissing=true` 创建并调用一次性 reviewer。
- [x] 验证临时 Agent 不写入 Agent 配置、不进入持久化 Agent 池。
- [x] 验证临时 Agent 只读权限边界。
- [x] 验证文件产出和计划最终状态。
- [x] 真实模型通过 `mcli_agent_job_create_many` 创建 3 个只读临时 Agent 并等待结果。
- [x] 真实模型完成临时 reviewer 委托、文件写入和计划状态核验。

说明：并行任务使用三个虚构目录作为只读边界测试对象，目录不存在导致三个子 Job 返回 failed；主 Agent 正确收集失败结果并继续完成计划，验证了失败隔离和主流程收敛。

### 原始阶段草案（历史对照，当前状态以上方复选项和第 10 节为准）

#### Phase 2：临时 Agent 工厂

- 实现内存临时 Agent 工厂。
- 实现权限求交集和只读默认白名单。
- 接入同步 `mcli_delegate_agent`。
- 临时 Agent 执行后释放引用。

#### Phase 3：并行 Agent Job

- 让 `create` 和 `create_many` 支持临时 Agent 请求。
- 限制最大并行临时 Agent 数量为 5。
- 保持 wait/result/cancel 的现有生命周期。

#### Phase 4：小型计划

- 增加步骤执行策略。
- 优先已有 Agent，未匹配时按策略创建临时 Agent。
- 记录执行摘要和临时 Agent 数量。
- 补充计划恢复时不恢复临时 Agent 的测试。

#### Phase 5：真实宿主验收

- 真实模型创建小型计划。
- 计划包含一个已有 Agent 匹配步骤。
- 计划包含一个未匹配的临时 Agent 步骤。
- 并行执行 3 个只读搜索临时 Agent。
- 验证临时 Agent 不写入配置、不突破权限。
- 验证文件产出和计划最终状态。

## 10. 当前剩余工作（2026-10-12）

以下项目尚未闭环，后续执行必须逐项更新证据：

- [x] **计划策略自动执行**：`execute_step` 已根据 `executionPolicy` 选择已有 Agent、未命中时创建临时 Agent，或用明确的 `parallelTasks` 创建 2–5 个并行任务；`PrimaryAgent` 保持主模型工具执行方式。真实 `policy` 场景中三种 Agent 策略的步骤均完成，证据目录 `.artifacts/expert-policy-20261012-023908/`。
- [x] **步骤完成校验**：`update_step_status(completed)` 被拒绝；主 Agent 的 `execute_step` 检查运行期间的工具证据，失败工具不能被后续文本掩盖，纯分析步骤需声明 `completionMode=analysis` 和可核对的 `expectedOutput`；Agent 步骤根据子调用及 Job 实际结果更新状态，终态步骤不能重复执行。
- [x] **成功的并行真实验收**：`.artifacts/expert-parallel-20261012-023830/` 中三目录 marker 实际存在，一次 `create_many` 创建 3 个不同临时 Agent，3 个 Job 均成功，执行时间区间有重叠，诊断输出 `RESULT|PASS`。
- [x] **完整回归重跑**：七个方案项目均执行，结果见下表；失败仍需单独排查，未把回归结果表述为全绿。

已完成且保留证据：

- 临时 Agent 独立身份、Grant、工具和路径绑定已通过真实三文件写入验收；`.artifacts/expert-files-20261012-023951/` 输出 `GAMMA|...|verified=True` 和 `RESULT|PASS`。
- 三路并行和自动计划策略的真实模型场景分别输出 `PARALLEL|jobs=3|children=3|overlap=True`、`POLICY|ExistingAgentOnly/TemporaryAgentIfUnmatched/PreferParallelTemporaryAgents`，均为 `RESULT|PASS`。诊断 `finally` 删除临时配置副本。
- 定向 Agent/计划/授权测试已通过；完整回归保留如下实际结果（`.artifacts/expert-regression-20261012/*.trx`）：

| 项目 | 通过 | 失败 | 跳过 |
| --- | ---: | ---: | ---: |
| Core | 138 | 0 | 0 |
| Protocol | 45 | 0 | 0 |
| MConsole | 73 | 0 | 0 |
| Provider | 419 | 13 | 15 |
| Persistence | 99 | 2 | 0 |
| EndToEnd | 324 | 15 | 0 |
| Tools.Builtin | 0（无可发现用例） | 0 | 0 |

Provider 失败集中在 Worker 生命周期／取消、兼容协议、Unicode 脚本路径及工作区锁；EndToEnd 失败涉及样例宿主路径、旧 Work/Meeting 断言、HTTP 超时与多实例；Persistence 的两例恢复测试失败需独立核查。此表按 TRX 的 total/passed/failed 与 skipped 用例核对，未把环境或历史问题列为已修复。

### 30 项失败的修复复测（2026-10-12）

原表为修复前基线，保留作为对照。合并工作区复测：

| 项目 | 通过 | 失败 | 跳过 | 证据 |
| --- | ---: | ---: | ---: | --- |
| Provider | 432 | 0 | 15 | `.artifacts/expert-regression-final-20261012/Provider.trx` |
| Persistence | 101 | 0 | 0 | `.artifacts/expert-regression-final-20261012/Persistence.trx` |
| EndToEnd | 340 | 0 | 1 | `.artifacts/expert-regression-final-20261012/endtoend-recheck.trx` |

修复包括：限制 Worker 同时启动数量以避免握手资源争用；将管道退出写失败映射为结构化事件；恢复测试使用实际 Session/Run ID；样例路径、CLI 旧模式断言与帮助快照更新；HTTP 实际 SessionId、协议事件反序列化及跨 Run 过滤修复；连接恢复测试清理和配置修正。

仍须明确的限制：

- 原 30 项中 29 项在复测中通过；旧 `ReferenceHost_ThreeRuntimes_DemonstratesStage7HostWorkflow` 因示例依赖已移除模式被明确跳过，尚未迁移修复。
- 合并后的首次 EndToEnd 全套曾出现 `CtlStatus_WrongSecretAndInstance_ReturnStableErrors` 退出码偏差；隔离及全套复跑通过，竞态根因尚未确认，保留首次 `EndToEnd.trx`。
- `.csx` 测试缩短了临时目录并设置 UTF-8，任意 Windows 长路径下的 dotnet-script 缓存路径问题未解决。
- Provider 原有 15 项跳过未算作通过。

### 全量复核与新增阻断修复（2026-10-12，本轮）

证据根目录：`.artifacts/expert-full-regression-20261012-110831/`。保留 `initial/`、`focused/`、`final/`、`stress/` 的全部原始失败和复测记录；最终全套以 `complete/` 为准；构建记录为 `build-discovery.log`，不以隔离通过覆盖历史失败。

最终完整 slnx 构建成功，0 警告、0 错误（SDK 10.0.401；`global.json` 允许 latestFeature）。所有 8 个测试项目均实际执行命令，完整 TRX 如下；不把空测试项目算作测试通过。

| 项目 | 总数 | 通过 | 失败 | 跳过 |
| --- | ---: | ---: | ---: | ---: |
| Core | 138 | 138 | 0 | 0 |
| EndToEnd | 346 | 346 | 0 | 0 |
| HostIntegration | 9 | 9 | 0 | 0 |
| MConsole | 73 | 73 | 0 | 0 |
| Persistence | 101 | 101 | 0 | 0 |
| Protocol | 45 | 45 | 0 | 0 |
| Provider | 464 | 449 | 0 | 15 |
| Tools.Builtin | 0 | 0 | 0 | 0 |
| 合计 | 1176 | 1161 | 0 | 15 |

每项目证据为 `complete/Madorin.AI.Runtime.{项目}.Tests.trx`；`suite-summary.csv`、`original-30-status.csv`、`skipped-boundaries.csv` 由同目录 `summarize.ps1` 从原始 TRX 生成。原 30 项逐条匹配全部 Passed，5 个重命名/Expert-only 迁移明确列在下表；不声称已移除的 Work/Meeting 模式恢复。

本轮确认并处理：

- `HostIntegration.Tests` 原来不在 slnx 中；无构建直接 `dotnet test --no-build` 只返回构建成功、没有发现测试，不能算通过。已加入 slnx，恢复构建并实际执行 9 项用例。
- `ReferenceHost_ThreeRuntimes_DemonstratesStage7HostWorkflow`：SampleHost 为索引 1/2 创建 Expert selection，移除该用例的 Ignore；原有三实例分流、GSN、权限、审批、取消、Selection、重启恢复、secret 不泄漏断言全部保留，定向 2/2 通过（`focused/ReferenceHost.trx`）。
- 原 30 中 `KillDuringInvoke_FailsPendingInvocation` 在初次全套再次失败。固定 300ms 后杀进程可能发生在 hello 之前，实际得到 `ProviderWorkerUnavailable`。改为收到真实 Provider 请求后才杀进程，继续严格要求 `ProviderWorkerExited`，不放宽断言。
- 同步 `mcli_delegate_agent` 的 schema 宣告 `timeoutMs`，原路由却未读取/执行；补充整数及 >=1000ms 校验，使用 linked token 限制子调用，超时返回失败且父取消仍传播。`focused/delegate-timeout-before.trx` 保留修复前失败。
- PrimaryAgent 步骤原来能按时间从全局证据缓存借用其他 Running 步骤的工具成功证据。完成校验现在只读取该步骤持久化 Evidence；新增两步骤回归先复现错误完成，再验证第二步骤被拒绝、第一步骤正常完成。`focused/evidence-before.trx` 保留修复前失败。
- Runtime 原来直接异步覆盖 `runtime.pid`，文件存在并不代表 JSON 已完整发布。改为同目录临时文件写完后 rename，finally 清理临时文件；增加首次发布/替换陈旧文件、元数据全部身份字段及停止清理回归。
- Windows pid 读写/替换期间真实出现 sharing violation；CLI 发现阶段现对 IOException 做最多 9 次、间隔 50ms 的有界重试，取消 token 仍有效，JSON 错误和身份校验不放宽。新增 `CtlStatus_TransientPidSharingViolation_PreservesAuthenticationError` 持有独占句柄后释放，严格验证最终鉴权失败而非连接失败。发布及 ctl 定向见 `focused/discovery.trx`。
- `ctl status` 错误凭据/实例与 Worker kill 各独立重复 20 次，全部通过（`stress/ctl-1..20.trx`、`stress/worker-1..20.trx`）；前述运行期间还存在全套并发压力。

原 30 项逐项核对（E=EndToEnd，P=Provider，S=Persistence；证据为上述 complete/ 对应项目 TRX）：

| # | 项目 | 原失败用例 | 本轮状态/迁移 |
| ---: | --- | --- | --- |
| 1 | E | CommandTree_AllPathsAndHelpRemainStable | Passed |
| 2 | E | SampleHost_ProjectReferencesOnlyClientAndContracts | Passed → SampleHost_ProjectReferencesOnlyClientContractsAndEntities |
| 3 | E | WorkRun_PureModelStepsContinueOfflineAndReplayBufferedOutbox | Passed → ExpertRun_PureModelContinuesOfflineAndReplaysBufferedOutbox |
| 4 | E | TwoHostsWithTwoRuntimes_IsolatedAndCrossConnectionsFail | Passed |
| 5 | E | RunEndpoint_WithInvalidProviderId_ReturnsError | Passed |
| 6 | E | SessionList_PaginationAndFilters_ReturnExpectedHumanAndJsonResults | Passed |
| 7 | E | SessionContinuation_PreservesContext | Passed |
| 8 | E | WorkspaceAndInstanceLocks_AreAtomicAndIndependent | Passed |
| 9 | E | RunEndpoint_WithStopSequences_TerminatesEarly | Passed |
| 10 | E | ThreeModes_AfterRestartResumeUnifiedStateAndContinueMonotonicGsn | Passed → ExpertSessions_AfterRestartResumeUnifiedStateAndContinueMonotonicGsn |
| 11 | E | RunForTests_WorkWithOneAgent_RejectsBeforeDataDirectoryCreation | Passed → RunForTests_RetiredWorkWithOneAgent_RejectsBeforeDataDirectoryCreation |
| 12 | E | RunForTests_ThreeModes_RouteConfiguredAgentsAndComplete | Passed → RunForTests_ExpertRunsAndRetiredModesAreRejected |
| 13 | E | Server_HostAndMcpTools_RequestsConsentPersistsBlobAndContinuesProvider | Passed |
| 14 | E | ReferenceHost_ThreeRuntimes_DemonstratesStage7HostWorkflow | Passed，恢复执行、不再 Ignore |
| 15 | E | EventDisconnect_ReconnectsWithoutRepeatingControlInitialization | Passed |
| 16 | S | RecoverOnStartupAsync_WithRunningRun_MarksRunInterrupted | Passed |
| 17 | S | ResumeSessionAsync_WithExistingSession_ReturnsLatestRunState | Passed |
| 18 | P | StandaloneFactory_CompatiblePreset_CancellationStopsSlowStream (Custom) | Passed |
| 19 | P | StandaloneFactory_CompatiblePreset_CancellationStopsSlowStream (Gemini) | Passed |
| 20 | P | StandaloneFactory_CompatiblePreset_EnablesToolCallingByDefault (GLM) | Passed |
| 21 | P | StandaloneFactory_CompatiblePreset_MapsRedactedAuthenticationFailure (GLM) | Passed |
| 22 | P | StandaloneFactory_CompatiblePreset_MapsRedactedAuthenticationFailure (Gemini) | Passed |
| 23 | P | StandaloneFactory_CompatiblePreset_CancellationStopsSlowStream (GLM) | Passed |
| 24 | P | StandaloneFactory_CompatiblePreset_EnablesToolCallingByDefault (Custom) | Passed |
| 25 | P | SupportedInterpreterExecutesUnicodePathAndArgumentArray (.csx) | Passed，长路径限制另列 |
| 26 | P | StandaloneFactory_ProxyAuthentication_UsesConfiguredHeader | Passed |
| 27 | P | KillDuringInvoke_FailsPendingInvocation | Passed，本轮修复真实请求就绪同步 |
| 28 | P | TwoConnections_ShareWorkerAndDoNotMixKeys | Passed |
| 29 | P | StandaloneFactory_CompatiblePreset_MapsRedactedAuthenticationFailure (Custom) | Passed |
| 30 | P | KillThenRelaunch_KeepsWorkspaceLock | Passed |

最终代码上再次执行历史 ctl status 与新增 pid 占用用例，各重复 20 次、共 40 次通过，证据 `stress-latest/ctl-1..20.trx`。全套同时覆盖 AgentJob 取消/超时/容量释放、计划失败恢复、授权 intent/ledger 终结、child call-bound Grant、跨 Provider 默认模型选择和真实本地 Worker 协议。

边界与真实剩余风险：

- `ctl status` 历史退出码 3→5 没有保留足够异常细节，不能证明历史那一次必然由 pid 部分写入导致。本轮修复实际发布竞态并重复验证，但历史故障归因仍未完全闭环；不将 20 次通过称为所有时序已根治。
- 新增 pid 测试初稿要求 Windows 对持续持有的旧目标文件句柄进行替换，该超出发现协议的假设失败；实验 TRX 全部保留。最终验证真实的完整元数据发布、陈旧替换与清理，不修改原有测试断言。
- 跨 Provider 的自动化验证使用确定性本地 Provider/HTTP/Worker；没有启用真实远端模型收费调用，没有读取或输出远端密钥。真实 Provider/本地 Ollama 的 opt-in smoke 仍属于未验证边界。
- 原有 Provider 15 项 NotExecuted 不计通过：12 项 opt-in 真实 Provider/skills probe、1 项大小写敏感文件系统、2 项测试环境未发现 `.py`/`.sh` 解释器。没有新增跳过；Windows 任意长路径的 dotnet-script 缓存问题仍未闭环。
- 同步委托超时依赖 Provider/工具协作响应 CancellationToken；不协作的第三方插件硬中止、进程级强杀与远端请求停止计费未验证。
- 语义 MCP 的 ensure_baseline 和 workspace_create(session) 均被 dirty baseline 拒绝；未 stash、commit、push、publish，改用指定范围 Bash rg/Read。现有用户修改全部保留。

### 剩余链路与原 15 项跳过复核（2026-10-12，继续执行）

证据根目录：`.artifacts/expert-chain-audit-20261012-121558/`。原 dirty 全部保留；没有提交、推送、发布或改写用户凭据。当前会话没有可调用的 charpcodemap 工具，未声称完成语义索引；使用限定目录的 Bash rg 和 Read。

本轮生产修复：

- `ExpertPlanManager`：Running → Pending 清除旧 Evidence、StartedAt、CompletedAt、Progress、Result、ErrorMessage，防止重新开始后借用上一次尝试的工具成功证据。`focused/evidence-reset-before.trx` 保留修复前失败。
- `ExpertModeOrchestrator`：模型工具路由禁止同时启动第二个 PrimaryAgent 步骤，消除新流程证据归属歧义；不禁止真正的子 Agent 并行。直接管理器/历史多 Running 数据仍需人工选择保留的步骤并重置其他步骤，未自动重放有副作用的工具。
- `TemporaryAgentFactory`：补齐 memory 写入/追加/更新/删除和 AgentJob 查询/等待/取消的禁用列表，防止临时 Agent 修改持久记忆或操控兄弟 Job。
- `ExecuteAgentJobAsync`：临时并行 Agent 现在直接沿用请求的 `AllowedToolIds`，由 `TemporaryAgentFactory` 与父 Agent allowlist 求交并继续过滤既定危险工具；显式允许且父级授权的 `mcli_fs_write`、`mcli_fs_append`、`mcli_fs_edit`、`mcli_fs_delete` 不再被强制收缩为只读。同步委托保持同一权限语义。memory 写入/追加/更新/删除及 AgentJob 控制禁令来自本方案第 6 节的既有边界，未扩大到文件写删。新增并行显式写入回归覆盖该约定。
- 新增 5 项回归，使用生产工厂、真实计划私有路由、ToolGateway、ToolCatalogStore 与 Provider 请求捕获，不复制生产逻辑。权限修正定向 `focused/permission-correction.trx` 为 4/4；旧的只读过度限制回归已改为显式写入授权回归。

### 权限修正复核（2026-10-12）

本轮发现上一版把并行临时 Job 的 `AllowedToolIds` 与默认只读集合强制求交，错误地丢失了显式授权的文件写/删能力。该限制已撤销。当前规则是：临时 Agent 权限不超过父 Agent allowlist；请求工具必须显式列出；宿主 ToolGateway/Grant 仍逐调用校验；既定危险工具、memory mutation 和兄弟 AgentJob 控制仍禁止。`focused/permission-correction.trx`：4/4 通过，包含并行 Job 显式写权限保留、同步委托显式写权限保留、memory/Job 控制禁令和原有父权限求交。

全量统计按最终 TRX 重新解析：Core 138、EndToEnd 346、HostIntegration 9、MConsole 73、Persistence 101、Protocol 45、Provider 469（465 通过、1 失败、3 跳过）、Tools.Builtin 0；总计 1181，465+138+346+9+73+101+45=1177 通过，1 失败，3 跳过。Provider 唯一失败仍是 `AnthropicMinimal_ThinkingStreaming_ReturnsReasoningAndText`，不修改断言。

原 15 项跳过当前全量第三项具体为：`ConfiguredAzureOpenAI_ReturnsTerminalEvent`（缺 Azure endpoint/deployment/key）；`CaseEquivalentNamesWithinOneSourceDisableAllConflicts`（Windows 非大小写敏感文件系统）；`MissingInterpreterReturnsStructuredError`（六种可选解释器均已安装）。此外 thinking 用例是 1 个真实失败，不是跳过。原 15 项逐项 CSV 与最终 TRX 必须以本目录最新生成文件为准。


逐项原跳过状态（最终全量对应 Provider TRX；可用性不等同于通过）：

| # | 原跳过用例 | 当前验证与结论 |
| ---: | --- | --- |
| 1 | ConfiguredProvider_ReturnsTerminalEvent (OpenAICompatible) | 已启用，既有 deepseek 配置，Passed |
| 2 | ConfiguredProvider_ReturnsTerminalEvent (Anthropic) | 已启用，magpie-anthropic，Passed；axis-code 首轮失败保留 |
| 3 | Live_HostModeAfterActivation_ModelCanLoadSkillBody | 已启用，既有默认 Provider 配置，Passed |
| 4 | ConfiguredAzureOpenAI_ReturnsTerminalEvent | NotExecuted；无 Azure endpoint/deployment/key 配置，不能用兼容 Provider 冒充 Azure |
| 5 | Live_HostModeEmptyLoadable_OutboundRequestContainsSkillToolsWithoutBody | 已启用，Passed |
| 6 | AnthropicMinimal_ThinkingStreaming_ReturnsReasoningAndText | 已执行，Failed：无 Thinking；不得弱化 reasoning 断言 |
| 7 | ConfiguredProvider_ReturnsTerminalEvent (OpenAI) | 已启用，magpie-openai，Passed |
| 8 | ConfiguredLocalOllama_ReturnsTerminalEvent | 已启用，本机已有 SparkLLM/Spark-X2.5-4B:16k，正确 /v1 endpoint，Passed |
| 9 | AnthropicAdapter_RealCancellation_StopsStreamWithoutWaitingForText | 已启用，Passed；仅证明客户端取消及时，不证明远端停止计费 |
| 10 | AnthropicMinimal_NonStreaming_ReturnsTextAndUsage | 已启用，Passed |
| 11 | ConfiguredDashScope_ReturnsTerminalEvent | 已启用，既有 Aliyun 配置，Passed |
| 12 | SupportedInterpreterExecutesUnicodePathAndArgumentArray (.py) | 已有 Python312 加入测试进程 PATH，Passed；不修改系统 PATH |
| 13 | CaseEquivalentNamesWithinOneSourceDisableAllConflicts | NotExecuted；Windows 大小写不敏感目录，隔离目录 fsutil enable 返回拒绝访问；未提升权限或改动工作区属性 |
| 14 | SupportedInterpreterExecutesUnicodePathAndArgumentArray (.sh) | 已有 Git Bash 加入测试进程 PATH，Passed；Unicode 路径/参数断言不变 |
| 15 | AnthropicAdapter_RealToolCall_EmitsIncrementalArgumentsAndCompletion | 已启用，Passed，保留增量参数和 continuation 断言 |

原 15 项合计：12 Passed、1 Failed、2 NotExecuted。启用全部解释器后 `MissingInterpreterReturnsStructuredError` 在全套内新增一个环境跳过（六种解释器都可用）；`focused/missing-interpreter-original-path.trx` 用原 PATH 单独执行 Passed，不把该定向结果冒充全套执行结果。

远端证据及边界：首轮 `skip-audit.trx` 为 12 通过、5 失败、2 跳过；axis-code 的 Anthropic 返回 403，Ollama 首次诊断使用了缺少 /v1 的地址。后者更正仅测试进程配置，不改 Provider 断言。`anthropic-alternate.trx` 为 6 通过、1 失败，`ollama-correct-endpoint.trx` 为 1 通过。`thinking-wire-summary.json` 为原始 SSE 结构计数：HTTP 200、一个 text 块、七个 text_delta，没有 thinking 块/增量；未记录密钥、请求头或完整正文。现有代理模型不提供本用例要求的 reasoning，无法宣称 Anthropic thinking 端到端已通过；没有直连官方配置可交叉验证。

本轮审查边界与仍需注意的风险：

- 计划恢复保留持久化状态，不自动重跑 Running 子任务；进程崩溃后的真实外部副作用仍需对账，不能仅凭文本把步骤改成 Completed。本轮修复的是显式重置后的旧证据重用，不是事务级 exactly-once。
- 模型新建 PrimaryAgent 步骤的歧义已阻断；旧文件或直接调用管理器构造的多 Running 步骤仍采用旧的首项归属行为，必须先显式重置多余步骤。现有跨步骤证据隔离回归保持通过。
- AgentJob 的取消、超时、排队取消、容量回收、Dispose 协作退出已有生产协调器测试，本轮定向及全量均重跑；非协作插件超过 30 秒 drain 后继续运行、与 Dispose 竞争创建 Job 的极端时序没有新增确定性覆盖，不宣称强制中止已闭环。
- 新建并行临时 Job 已限只读；匹配到预先配置的已有 Agent 仍保持已有权限语义。若要求所有既有 Agent 也强制只读，需要另行明确兼容策略。未知宿主工具只受原父级/宿主授权约束，本轮没有将所有宿主工具一律禁用。
- 宿主授权继续由 Gateway/Consent 执行；未改变 call-bound Grant、child AgentId、ParentAgentId 和路径授权。跨 Provider 测试同时包含本地确定性请求及已有真实代理配置，但这不等于所有厂商官方 endpoint 均通过。
- 原有任意 Windows 长路径 dotnet-script 限制、历史 ctl 竞态根因不完全可追溯、远端取消不等于停止计费等风险继续保留。

<!-- CHAIN_AUDIT_FINAL_RESULTS -->

## 11. 验收标准

- 旧的已有 Agent 委托测试全部通过。
- 未匹配且默认关闭临时创建时明确失败。
- 未匹配且显式开启临时创建时成功完成一次委托。
- 临时 Agent 不出现在 `.madorin/agents/`。
- 临时 Agent 不能调用写文件、进程、网络和二次委托工具。
- 并行 3-5 个临时 Agent 能收集全部结果。
- 计划最终状态和步骤结果正确持久化。
- CLI、SDK 宿主和真实模型流程至少各有一条验证证据。
- 构建保持 0 警告、0 错误。

## 12. 风险与回退

- 模型可能滥用 `createIfMissing`：默认关闭，且由工具 schema 和权限层双重限制。
- 模型生成的 role 可能过于宽泛：使用固定系统模板和工具白名单。
- 并行任务可能扩大成本：限制数量、超时和父任务预算。
- 临时 Agent 结果可能过长：限制结果长度并要求摘要。
- 新流程失败时：回退到已有 Agent 匹配或由主 Agent 执行。
