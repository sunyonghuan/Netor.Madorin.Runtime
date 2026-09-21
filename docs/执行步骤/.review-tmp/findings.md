# Reviewer Findings

## 约定探测

- 工作目录：`E:\Netor.me\Madorin\Netor.Madorin.Ai.Runtime`
- 目标文档：`docs/执行步骤/上下文压缩与手动压缩命令修复方案.md`
- 代码范围：当前 Runtime 仓库 + `E:\Netor.me\Madorin\Netor.Madorin.Workstation`
- `detect_conventions.py` / `check_refs.py` 未执行：当前环境没有 `python`、`py` 或 `python3` 命令。
- 已手工验证方案列出的 Runtime 14 个文件和 Workstation 6 个文件均存在。
- 目标文档没有 Markdown 外部链接，脚本引用检查暂无可检查链接。

## 发现清单

### F-001 — 阻断 — 执行步骤标题层级错误

- 位置：第 59-61 行；第 90、129 行及后续 Step 标题。
- 问题：`## 4. 执行顺序` 下面的 `## Step 1`、`## Step 2` 与第 4 节处于同级，导致主目录结构断裂；`### Step 1 验收标准` 又变成 Step 1 的子标题，但 Step 1 本身不是第 4 节的子标题。
- 影响：Markdown 目录、阶段边界和后续自动检查可能把 Step 当成一级章节，无法表达“第 4 节下的 9 个步骤”。
- 建议：统一为 `### 4.1 Step 1 ...`、`### 4.2 Step 2 ...`，验收标准降为 `#### 验收标准`；或者移除“4. 执行顺序”，让每个 Step 成为 `##`，但不能混用。

### F-002 — 严重 — Windows 复现命令不可直接执行

- 位置：第 65-71 行。
- 问题：命令使用 Unix shell 的反斜杠 `\` 续行，但工作环境和宿主 README 约定为 PowerShell；复制执行会把 `\` 当作参数或导致命令解析失败。
- 影响：第一步本身可能制造一个与业务无关的 CLI 参数错误，无法区分“命令写法错误”和“压缩功能错误”。
- 建议：改成 PowerShell 可复制的一行命令，或使用 PowerShell 反引号，并明确 `<exe>`、`<workspace>`、`<data-directory>`、`--json` 的真实位置和全局参数顺序。

### F-003 — 严重 — Provider 配置/凭据桥接没有形成可执行方案

- 位置：第 139-149 行、第 363-371 行。
- 问题：方案要求把 `Compaction.VendorId`/`Compaction.ModelId` 转换为 Runtime 的 Provider/Model，但当前宿主通过 CLI 启动独立压缩 Runtime；仅传 ProviderId/ModelId 不能保证 CLI 进程拥有对应 Provider 配置、凭据引用、BaseUrl 和可用模型目录。
- 影响：即使参数注入成功，CLI 仍可能在 Provider resolver、配置加载或凭据校验阶段失败；方案没有规定凭据如何安全传递，也没有定义“宿主 materializer 与 CLI StandaloneConfig”的关系。
- 建议：Step 1/3 增加“配置来源和凭据来源决策”：复用工作区 Runtime 配置、为 CLI 生成短生命周期 HostProviderConfig、或改用已连接的 Runtime Client API。明确禁止把 API Key 放入命令行，并增加 Provider 配置/凭据不可用的集成测试。

### F-004 — 严重 — 手动压缩结果返回链路没有完全定义

- 位置：第 100-127 行、第 129-137 行、第 327-338 行。
- 问题：方案要求 Gateway 成功后解析 `SessionCompactionResult`，但现有 `IRuntimeGateway.CompactSessionAsync`、`IConversationTurnService.CompactAsync` 和 Desktop/QuickChat 调用均是 `Task` 无返回值。方案没有明确各层的新返回类型、兼容策略、UI 展示入口和错误对象。
- 影响：实现者可能只修改 Gateway，结果仍在 Service/UI 层丢失，用户只能看到“压缩完成”而无法确认 provider/model、cacheHit、token 变化。
- 建议：在 Step 2 中明确完整签名迁移：`Gateway -> Service -> Chat/QuickChat` 返回 `SessionCompactionResultDto`，或者保留旧接口并新增 `CompactSessionWithResultAsync`；列出所有调用方和测试替身。

### F-005 — 严重 — 缓存复用目标与当前架构约束尚未闭合

- 位置：第 18-21 行、第 383-385 行、第 331-338 行。
- 问题：方案一方面要求手动压缩后验证下一次 Invocation 的 cache hit/miss，另一方面又只提出增加 cache key，没有明确 Expert、Meeting、Work 哪个入口读取缓存、如何把缓存转换为各自的 projection、以及 Meeting round summary/Work plan projection 如何与通用缓存兼容。
- 影响：实现者可能直接让模式读取 `ConversationCompactCacheV1`，绕过当前“每次 Invocation 从 canonical history 创建 ContextProjection”的架构约束，造成策略、工具目录、Agent Prompt 或 Round 状态不匹配。
- 建议：先在 Step 6 选择一种明确语义：
  1. 手动压缩仅作为显式维护缓存，不承诺下一轮自动复用；或
  2. 增加统一 `ProjectionCacheKey` 和 `IContextProjectionCache`，由各模式在构造 projection 前经过同一 Resolver 判断命中，并明确每种模式的适配规则。完成选择前不要把 Step 9 的“必须 cache hit”作为验收条件。

### F-006 — 一般 — 窗口参数和模型压缩参数的落地边界仍然模糊

- 位置：第 32-39 行、第 139-159 行、第 214-246 行。
- 问题：目标写成“传递压缩 Provider、Model 以及窗口参数”，但 Step 4 又把 `SegmentSize`/`RawTailSize` 的单位留到后续冻结；当前没有一个最小可交付版本说明哪些参数本轮必做、哪些参数只保留 UI、哪些参数暂不支持。
- 影响：实现可能先把 UI 的 `RawTailSize=20` 错当成 Token，或为了满足设置项而一次性引入“分段摘要 + 原始尾部”的大改动。
- 建议：在方案前部增加 MVP 边界：第一阶段只接入 `Strategy`、`ProviderId`、`ModelId`、`KeepLastTokens`；`MaxDisplaySegments` 明确为 UI-only；`SegmentSize`/`RawTailSize` 在语义冻结前不得下发。

### F-007 — 一般 — 验收命令和测试项目不够具体

- 位置：第 327-341 行。
- 问题：只写“执行 Debug 构建和相关测试”，没有列出具体 solution/project、测试项目、过滤器、CLI 冒烟命令和预期结果文件。
- 影响：执行者无法判断“相关测试”范围，也无法复现同一门禁；跨两个仓库时尤其容易只验证 Desktop。
- 建议：补充 Runtime/Workstation 的具体 `dotnet test` 路径和过滤器，以及成功压缩后的 JSON 字段断言、日志位置和真实 Provider 测试的禁用/启用条件。

### F-008 — 一般 — 没有把“当前已知实现”和“计划新增实现”分栏

- 位置：全文，尤其第 1 节、第 5 节和第 6 节。
- 问题：已有 `LocalRuntime` Summary 压缩、模式投影和宿主 CLI 调用，与计划新增的 `IContextCompactor`、安全缓存复用、Selection 注入混在同一条执行链路中。
- 影响：执行时容易误以为某些能力已经具备，或者重复重构已存在的实现。
- 建议：增加“现有实现保留点 / 新增实现点 / 明确不改动点”三列表格，并把每个 Step 关联到其中一类。

## 各维度结论

- D1 结构与可执行性：发现 F-001、F-002。
- D2 事实与代码一致性：代码文件路径均存在；但 F-003、F-004 的跨进程配置/返回链路需要补充事实约束。
- D3 目标与范围：总体目标清晰；F-006 需要收敛 MVP 范围。
- D4 架构职责：F-005 需要明确缓存 Resolver 与三种模式的边界。
- D5 数据/状态/缓存：F-005。
- D6 错误处理与诊断：F-003、F-004、F-007。
- D7 测试与验收：F-007。
- D8 安全与凭据：F-003。
- D9 可维护性：F-008。
- D10 文档引用：未发现外部 Markdown 链接；代码路径手工校验通过。
- D11 代码一致性：PARTIAL。目标文档涉及两个仓库，自动脚本因环境缺少 Python 未运行；人工只完成路径存在性检查，未宣称已覆盖代码事实。
