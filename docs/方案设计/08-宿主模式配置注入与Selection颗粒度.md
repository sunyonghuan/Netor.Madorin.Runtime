# 08 — 宿主模式配置注入与 Selection 颗粒度

> 适用：Madorin.AI.Runtime（宿主 `serve` + Client SDK）与 Netor.Madorin.Workstation 对齐。  
> 状态：Runtime/CLI/Contracts/Client SDK **P0–P2 已实现**（Contracts `0.1.26`）；宿主仓 Gateway 组装见 §6，仍不在本文交付内。  
> 约束：**独立模式（`madorin run` / 个人 `config.json` / agents 文件）坚决不改行为**；本文只规范宿主集成路径。  
> 关联：`01-概念与导航`、`04-宿主集成指南`、`15-Provider适配器开发指南`、`F04` 会话与协作、`F06` Runtime、`F09` 系统设置。

---

## 0. 结论摘要

1. CLI **不能**读取宿主 SQLite / 系统设置；执行所需内容必须由宿主经协议 **by-value** 传入（详细配置 + ID）。
2. 配置颗粒度 **不是** Project，也 **不是** serve 进程单例，而是：
   - **Mode**：每个任务会话（WorkSession ↔ Runtime Session）创建时锁定；
   - **厂商 / 模型 / 智能体**：每个会话可不同；对话中可切换，但 **等当前一轮 Run 结束后，下一轮才生效**。
3. 同一 Project 下多任务会话并行时：**业务 Selection / Mode / 本轮冻结快照**按 WorkSession·Session·Run 隔离；serve 进程内 Registry 按 `ProviderId` **共享缓存**（同 Id 后写覆盖前写，不保证跨 Session 互不覆盖），不是业务权威。
4. 独立模式继续使用本地 `config.json`；宿主模式使用进程内 **Host Provider Registry** + 请求内完整 `AgentRef`。

---

## 1. 问题背景

### 1.1 宿主产品模型

```text
Project（宿主项目）
  └─ 多个任务会话 / 工作页 WorkSession（一项工作一标签）
        ├─ Mode：Expert | Work | Meeting（创建锁定，D-12）
        ├─ 对话 Selection 快照：厂商 / 模型 / 智能体（可改，见 §2.0）
        └─ 多轮执行 → 每次发送对应一次 Runtime Run
              ├─ Expert / Meeting：不使用 WorkTask 层，Run.TaskId 应空
              └─ Work：业务 WorkTask（≠ WorkSession）下可聚合多条 Run
```

系统设置中的 Vendor / Model / Agent / Credential 是 **本机材料库**（全局），不是「当前项目唯一执行配置」。

### 1.2 已确认的产品规则

| 规则 | 说明 |
|------|------|
| 任务会话级差异 | 同一项目内，不同 **任务会话（WorkSession）** 可用 **不同** 模式、厂商、模型、智能体 |
| 模式不可热切换 | 与总纲 **D-12**（F04 落实）一致：同会话无 Mode 可写路径；换模式须新会话 / 派生（总纲 **D-54**）。`01-概念与导航` 写「不可隐式改变」，宿主产品侧按 D-12 落实为创建锁定 |
| 选择可热切换 | 同一会话内可改厂商 / 模型 / 智能体 |
| 轮次边界生效 | 切换须 **等待当前一轮 Run 结束**；本轮冻结，下一轮用新配置 |
| 进程隔离 | 宿主与 CLI 隔离；CLI 不读宿主库，只收协议载荷 |

### 1.3 现状断裂（实现前事实）

| 位置 | 事实 |
|------|------|
| `serve` | `ProviderResolver = null` → `UnavailableProviderAdapter` / `ProviderNotConfigured` |
| `NewSessionRunRequest` | 无 Provider 端点 / 协议 / 密钥字段 |
| 宿主 Gateway | `AgentRef.SystemPrompt` 等被填空；Selection 基本只有 ID |
| `credentials.update` | 绑定活跃 Run，不能替代创建时注入 |
| 独立 `run` | 本地拼完整 `AgentRef` + `StandaloneProviderFactory`（**保持不动**） |

---

## 2. 对象映射与配置颗粒度

### 2.0 术语（正交定义，全文以此为准）

| 术语 | 含义 | 不是 |
|------|------|------|
| **Project** | 宿主项目（产品边界） | Runtime Workspace / serve Instance 的别名 |
| **Workspace** | Runtime 工作区根路径（权限与 `.madorin` 基准） | 宿主 Project 本体 |
| **serve Instance** | 一次 `madorin serve` 进程实例 | 业务 Selection 权威 |
| **WorkSession** | 宿主工作页 / 任务会话（一项工作一标签） | `WorkTask` |
| **WorkTask** | 仅 Work 模式下多个 Run 的业务归组（`Run.TaskId`） | WorkSession；Expert/Meeting 无此层 |
| **Runtime Session** | CLI 会话；与 WorkSession 一对一映射 | Project 级单例 |
| **Run** | Session 内一次流式执行；**本轮配置冻结边界** | 会话默认配置本身 |
| **Selection 快照** | 产品侧厂商/模型/智能体选择；协议类型为 **`NextTurnSelection`**。可作会话默认，或某 Run 启动时的**冻结**副本 | 协议字段 `DefaultSelection` 单独一项 |
| **`DefaultSelection`** | `NextTurnSelection` 子记录，仅 `ProviderId` + `ModelId` | 不含 Agent；不是整包 Selection |
| **`ModeOptions`** | 既有协议名：按 Mode **形状**携带的 Agent 等载荷（Expert 单 Agent / Work GM+AvailableAgents / Meeting Participants）。**内容可按轮更新**；**不**表示 Mode 可改 | Mode 枚举本身 |
| **Mode** | Session 运行形态（expert/work/meeting），创建锁定。请求顶层 `Mode` 与 `NextTurnSelection.Mode` 必须一致（既有双写校验） | `ModeOptions` 内可轮换的 Agent |
| **Vendor** | 宿主材料库中的厂商目录项 | Registry 键；不等于 `ProviderId` |
| **`ProviderId`** | Runtime / Registry 可执行配置主键。宿主侧可由 `VendorId` + credential/profile **派生**（单密钥场景字符串可与 VendorId 相同）。多 credential/profile 时不得共用同一 `ProviderId` | 与 Vendor 概念混用；材料库 Vendor 行本身 |
| **pending Selection** | **宿主侧建议**的「待下一轮生效」本地草稿（非协议字段；实现归宿主 / F04） | 已发给 Runtime 的本轮冻结快照；协议只认 TurnOverride / 会话默认 |

### 2.1 宿主 ↔ Runtime 映射

| 宿主 | Runtime CLI | 配置含义 |
|------|-------------|----------------|
| Project | （无直接对象） | **不**承载业务 Selection |
| 项目根目录 → Workspace 路径 | 通常对应一个 `serve` Instance（`--workspace` / `--instance`） | 进程与数据区；Client 句柄按 Workspace 缓存 |
| WorkSession（任务会话 / 工作页） | Runtime Session | Mode 固定；会话默认 **Selection 快照**（`NextTurnSelection`）可更新 |
| WorkTask（仅 Work） | 无独立对象；体现在 `Run.TaskId` | 业务 Run 归组，不单独注入 Provider |
| 一次发送 / 一轮执行 | Runtime Run | **本轮实际**冻结的 Selection 快照 + 解析到的 Provider 适配器 |
| 系统设置 Vendor/Agent/… | 无表 | 宿主组装后 by-value 注入 |

### 2.2 四层颗粒度（必须遵守）

```text
① 材料层（全局，仅宿主）
   Vendor / Credential / Model / Agent 目录
   → CLI 永不直接读取

② 会话层（每个 WorkSession = 每个 Runtime Session）
   Mode（仅创建时锁定）
   默认 Selection 快照 = 完整 NextTurnSelection
     （含 DefaultSelection 的 Provider/Model + ModeOptions 内 Agent 快照）
   创建时注入的 ProviderConfigs + 完整 AgentRef

③ 轮次层（每个 Runtime Run）
   本轮冻结的 Selection 快照 + 解析到的 Provider 适配器
   UI 切换只进 pending；下一轮经 TurnOverride 或已更新的会话默认生效

④ 进程层（serve Instance）
   Host Provider Registry：多 ProviderId 缓存
   不是权威；重启后须由宿主重新注入
```

### 2.3 错误 vs 正确

```text
❌ Project 或 serve 启动时注入「唯一一套」Provider 配置并假定全项目共用
❌ 只传 ID，期望 CLI 反查宿主库或个人 config.json
❌ 当前 Run 执行中热替换 Selection / SystemPrompt
❌ 用 credentials.update 代替 Session 创建时的首次 Provider 注入
❌ 把 ProviderId 写成 VendorId 的别名（多密钥场景会 Registry 互盖）

✅ 每条任务会话独立 Mode + 默认 Selection 快照
✅ 每轮 Run **生效时**具备完整冻结快照（来自会话默认或 TurnOverride）；请求按需增量携带 ProviderConfigs
✅ 切换排队到下一轮；本轮配置冻结
✅ Registry 仅缓存；按**本轮 Run**解析得到的 ProviderId 选用（同 Id 进程内覆盖）
✅ 并行 Session 的业务 Selection 隔离；同一 Session 内 Run 为生效边界
```

---

## 3. 运行流程（宿主模式）

### 3.1 两条入口（对照）

| | 独立模式（不动；含 `01` 的独立运行 + 交互式 REPL） | 宿主模式（本文范围） |
|--|--|--|
| 入口 | `madorin run` / REPL | Client 拉起 `serve` |
| 配置源 | `~/.madorin/config.json` + agents | 协议 by-value |
| Provider | `StandaloneProviderFactory` | Host Provider Registry |
| Agent | 本地 agents → 完整 `AgentRef` | 请求内完整 `AgentRef` |

### 3.2 实例生命周期

```text
宿主激活项目 / 首次需要 Runtime
  → RuntimeClient.CreateAsync
  → 启动: madorin-cli serve --workspace ... --instance ...
  → stdin HandshakeSecret → Named Pipe 握手
  → Connected；Client 句柄按 Workspace 缓存
说明：启动阶段不注入业务 Vendor/Agent；Registry 初始为空。
```

### 3.3 新建 Session + 第一轮（`run.new_session`）

协议载荷：`NewSessionRunRequest`

```text
SessionIdempotencyKey / RunIdempotencyKey
Mode                          // 创建锁定（Session 权威 Mode）
Selection: NextTurnSelection  // 完整 Selection 快照（产品「Selection」）
  SelectionVersion
  Mode                        // 必须与顶层 Mode 相同（既有双写）
  DefaultSelection            // 仅 ProviderId + ModelId（Run 级主执行选型）
  ModeOptions                 // 该 Mode 形状下的 Agent 等载荷（可按轮更新内容）
    Expert  → ExpertModeOptions(AgentRef)
    Work    → WorkModeOptions(GM AgentRef, AvailableAgents[])
    Meeting → MeetingModeOptions(Participants...)
InitialInput[]                // 用户消息
WorkspaceId?
ProviderConfigs[]             // 【方案新增】本会话/本轮所需厂商详情
```

`AgentRef`（已有字段，宿主必须填满实质内容）：

```text
AgentId
PromptTemplateVersion         // 非空，如 "1"
SystemPrompt                  // 非空；编排直接使用
ProviderId? / ModelId?        // 可选；主/非主规则见下条权威
AllowedToolIds? / SkillIds?
```

**Provider / Model 权威（宿主模式）**：

- `DefaultSelection.ProviderId` / `ModelId` 是本轮**主执行选型**权威（Expert 的唯一 Agent；Work 的 GeneralManager；Meeting 见下「主选型」）。
- **现状（既有 Server）**：`primaryProviderId = 主 Agent.ProviderId ?? DefaultSelection.ProviderId`（Meeting 仅用 DefaultSelection），**无**相等校验。
- **方案（宿主模式新增）**：主 Agent 的 `ProviderId`/`ModelId` 省略则回落 `DefaultSelection`；**若给出则必须与 `DefaultSelection` 对应字段相等**（逐字段；仅一侧有值另一侧空 → 失败），禁止静默二选一。比较：trim 后序数敏感相等。
- Work 的 `AvailableAgents`、Meeting 的 Participants 等**非主** Agent：在 **单厂商约束**（与实施分期 P0 同期）下，须省略 `ProviderId` 或等于 `DefaultSelection.ProviderId`（异值 → 请求失败）。实施分期 **P2** 放宽多厂商后：异值视为角色自有 Provider，且须已在 Registry 可解析。
- Meeting：**不做**「某一 Participant ↔ DefaultSelection」的主 Agent 相等校验；Participants 一律按上条**非主**规则。

Server 处理顺序（逻辑）：

```text
1. 校验顶层 Mode == Selection.Mode，且 ModeOptions 类型与 Mode 匹配
   不匹配 → 拒绝整请求（既有 Mode 校验路径）
2. Work：AvailableAgents 非 null 且 Count ≥ 1；各项与 GM 均「完整」（见 §5.5）
3. Upsert ProviderConfigs → Host Provider Registry（先于 Resolve）
4. 宿主模式：主选型相等校验（方案新增）
   primaryProviderId ← DefaultSelection.ProviderId
5. ResolveProviderAdapter（查 Registry；禁止读个人 config.json）
   本轮用到的每个 ProviderId 未命中 → ProviderNotConfigured
6. CreateSession → 持久化 Selection（磁盘 Strip SystemPrompt，保留 PromptHash）
7. CreateRun → RunAccepted → ExecuteRunAsync → 模式编排 → Provider 流式
8. 事件回宿主（output.delta / run.* / credentials.refresh.request 等）
```

### 3.4 同会话下一轮（`run.existing_session`）

```text
SessionId
RunIdempotencyKey
InputOverride[]               // 本轮用户输入（必填）
TurnOverride?: NextTurnSelection   // 若相对默认 Selection 有变，带完整快照
ProviderConfigs?              // 仅当本轮用到尚未注册的 ProviderId
ExpectedSelectionVersion?
```

- Mode 不可变。  
- 无 `TurnOverride` 时用会话已存默认 Selection（续跑 Expert 等场景仍可能需要带完整 prompt 的 Override，见现有 resume 校验）。  
- **本轮使用的配置在 Run 启动时冻结。**

### 3.5 仅更新默认 Selection（`session.selection.update`）

- 更新会话默认，供后续 Run 使用。  
- 同样可附带 `ProviderConfigs?`（新厂商）。  
- 宿主应在 **无活跃 Run** 或明确「只影响后续轮」时调用；UI 切换仍建议与「轮次结束」对齐后再提交。

### 3.6 凭据刷新（附属路径，`credentials.update`）

```text
Provider 401/403
  → credentials.refresh.request 事件（含 RunId, ProviderId）
  → 宿主解密新 Key → credentials.update
  → 更新 Registry；若适配器实现 IRuntimeProviderCredentialUpdater
    → 热更活跃 Run 上 adapter，并自动重试失败请求
  → 未实现该可选接口：认证失败不触发运行时重试（与 15 一致）
```

- **不能**代替创建时 `ProviderConfigs` 注入。  
- 独立模式**不提供**运行时 refresh；认证失败直接终止当前 Run（与 `15` 一致，保持不动）。

### 3.7 事件回到宿主

```text
Runtime outbox → Client 事件通道 → 宿主按既有事件消费路径更新 UI / Run 状态（见 F06 / 04）
```

---

## 4. 对话中切换：轮次边界语义

### 4.1 时序

```text
会话默认 / 本轮选用 = A（厂商A / 模型A / 智能体A）

用户发送 → Run#1 开始（冻结 A）
  │
  │  UI 改选 B
  │  → 宿主只写 pending Selection（B）
  │  → 不取消 Run#1，不向 Runtime 热改本轮输入
  │
Run#1 终态（Completed / Failed / Cancelled）
  │
用户再发送 → Run#2
  → TurnOverride = B 的完整 NextTurnSelection
  → 若 B.ProviderId 未在 Registry：附带 HostProviderConfig
  → Run#2 使用 B
```

### 4.2 与 CLI 既有不变式一致

`01-概念与导航` 已写：切换 Provider、模型、Agent 或提示词只影响 **下一轮**，不改变 `sessionId`，不清空历史，**不新建会话**。  
本文把该不变式落到 **宿主多任务颗粒度** 与 **注入契约**（换 Mode 属另一类变更，须新 Session，见 §4.3）。

### 4.3 变更类型对照

| 变更 | 同一任务会话内 | 做法 |
|------|----------------|------|
| 换 Mode | 否 | 新 WorkSession + 新 `run.new_session` |
| 换厂商 / 模型 / 智能体 | 是 | 当前轮结束后，下一轮 `TurnOverride`（+ 新 Provider 详情） |
| 仅换模型（同厂商） | 是 | `TurnOverride` 改 ModelId；可不重传 ProviderConfig |
| 仅换智能体（同厂商） | 是 | 新完整 `AgentRef` |
| 换厂商 | 是 | 完整 AgentRef + 该厂商 `HostProviderConfig` |
| API Key 轮换（同 Provider） | Run 中 | `credentials.update`（附属） |

### 4.4 多任务并行

```text
同一 serve Instance（通常对应同一 Project 的 Workspace）
  Session-甲 Run 进行中（厂商 A）  —— 本轮冻结为 A
  Session-乙 同时 Run（厂商 B）    —— 业务 Selection/Mode 与甲隔离
  甲 UI 切到 C → 只写甲的 pending → 甲的下一轮 Run 才用 C
```

Registry 可同时缓存 A/B/C；选用键来自**各 Run 冻结 Selection 快照**解析出的 `ProviderId`，而非 Project。若甲乙共用同一 `ProviderId` 却 upsert 不同端点/密钥，后写覆盖前写（与 §0 一致）。

---

## 5. 宿主模式改造方案（CLI 侧）

### 5.1 原则

1. 配置 **by-value**，ID 作主键。  
2. **创建时**主路径注入 Provider；refresh 只换 Key。  
3. Agent 定义只活在请求内 `AgentRef` 快照。  
4. `serve` 使用 Host Provider Registry 作为 `ProviderResolver`；**禁止**宿主路径读个人 `config.json`。  
5. 独立命令与 `StandaloneProviderFactory` 行为 **零变更**（可将「Protocol → Adapter」纯映射抽到共享模块供两端调用，独立入口保持原样）。

### 5.2 契约：`HostProviderConfig`

以独立 `ProviderEntry`（`StandaloneConfig.cs`）的**连接配置面**为蓝本，避免第三套形状；**键与裁剪**如下：

| 独立 `ProviderEntry` | 宿主 `HostProviderConfig` |
|----------------------|---------------------------|
| `Name` | → `ProviderId`（Registry 键；概念 ≠ VendorId） |
| `Protocol` / `BaseUrl` / `ApiKey` | 同名 |
| `AuthenticationHeader` / `AuthenticationScheme` / `UserAgent` / `ApiVersion` | 同名可选 |
| `Models` / `ConfigurationVersion` | 同名可选 |
| `Capabilities` / `ProbePath` / `MaxTools` | **不纳入** HostProviderConfig（独立探测/能力面，宿主路径不走） |

```text
ProviderId                 // 必填；trim 后 length > 0
                           // 可由 VendorId + credential/profile 派生；
                           // 单密钥时字符串可与 VendorId 相同；多密钥须不同 ProviderId
Protocol                   // 必填；须为既有协议目录可识别值
BaseUrl                    // 必填；trim 后 length > 0
ApiKey                     // 必填；解密明文，仅管道/内存（trim 后 length > 0）
AuthenticationHeader?      // 默认可与独立一致（如 Authorization）
AuthenticationScheme?      // 默认可与独立一致（如 Bearer）
UserAgent?
ApiVersion?
Models[]?                  // 可选；null 与 [] 均表示不校验模型列表
ConfigurationVersion?      // 可选
```

### 5.3 请求挂载点

| 请求 | 字段 | 用途 |
|------|------|------|
| `NewSessionRunRequest` | `ProviderConfigs?`（**P0 主路径必填**；协议可空以便独立/兼容，空则 Registry 无项时 Resolve 失败） | Session 创建主注入（**P0**） |
| `ExistingSessionRunRequest` | `ProviderConfigs?` | 下一轮换新厂商时补注册（**P1**） |
| `SessionSelectionUpdateParameters` | `ProviderConfigs?` | 更新默认 Selection 时补注册（**P1**） |
| （可选 **P2**）`providers.upsert` | 批量 | Instance 预热，非必须 |

### 5.4 Server 行为

```text
serve 启动
  → 创建空的 HostProviderRegistry
  → ProviderResolver = id => registry.Resolve(id)  // 替代 null

run.new_session / existing / selection.update
  → 若带 ProviderConfigs：registry.Upsert（按 ProviderId 覆盖）
  → Resolve 时未命中 → 明确错误（保留 ProviderNotConfigured 语义）

持久化
  → Selection 继续 Strip SystemPrompt
  → ApiKey 不得写入 selection JSON / state 明文业务列
  → Registry 仅内存；进程重启后宿主须重注
```

建 adapter：复用与 `StandaloneProviderProtocolCatalog` 相同的协议映射（建议抽共享，独立 factory 薄封装不改对外行为）。

### 5.5 校验（宿主模式）

判定约定（全文校验共用）：字符串「非空」= 非 null 且 `trim` 后 length > 0；比较默认 **序数敏感、先 trim**。失败默认 = **拒绝整请求**（不创建/不启动本 Run），错误沿用既有 JSON-RPC / 校验异常通道；Registry 未命中保留 `ProviderNotConfigured` 语义。

| 规则 | 比较 / 范围 | 边界 | 失败动作 |
|------|-------------|------|----------|
| `DefaultSelection.ProviderId` / `ModelId` | 均非空 | 缺字段或空白 → 失败 | 拒绝请求 |
| Agent「完整」 | `AgentId`、`PromptTemplateVersion`、`SystemPrompt` 均非空 | tools/skills 可为 null（表示未声明）；空数组表示显式无工具/技能 | 拒绝请求 |
| Expert | 恰一个完整 `ExpertModeOptions.Agent` | ModeOptions 类型不是 Expert → 失败 | 拒绝请求 |
| Work | GM 完整；`AvailableAgents != null && Count ≥ 1`；每一项完整；**GM 不计入**列表个数 | `null` 与 `[]` 同失败；列表项不完整与列表空 **可同错类、分消息** | 拒绝请求 |
| Meeting | `Participants != null && Count ≥ 1`；每一项内 Agent 完整 | 同上 | 拒绝请求 |
| Mode 双写 | `request.Mode == Selection.Mode`；ModeOptions 派生类型 ∈ {Expert→ExpertModeOptions, Work→WorkModeOptions, Meeting→MeetingModeOptions} | TurnOverride / selection.update 携带不同 Mode → 拒绝（Session Mode 权威，不可改） | 拒绝请求 |
| 主选型相等（方案新增） | Expert Agent / Work GM：对各有值的 `ProviderId`/`ModelId` 字段，须等于 `DefaultSelection` 对应字段 | 仅一侧有值 → 失败；双侧皆空（省略）→ 允许回落 | 拒绝请求 |
| 单厂商约束（P0 同期） | 非主 Agent 省略或 `ProviderId` 等于 `DefaultSelection.ProviderId`；异值 → 失败 | P2 多厂商放宽见 §3.3 | 拒绝请求 |
| Registry | 「本轮用到的 ProviderId」= `DefaultSelection.ProviderId` ∪ 本请求 ModeOptions 内所有非空 `AgentRef.ProviderId` | Upsert 本请求 `ProviderConfigs` 之后再解析；部分未命中 fail-fast | `ProviderNotConfigured` |
| `HostProviderConfig` | `ProviderId`/`Protocol`/`BaseUrl`/`ApiKey` 非空；`Protocol` 可映射 | 同 Id upsert 覆盖；Models null/`[]` 不校验模型 | **拒绝整请求**（错误中标明 ProviderId）；归属 Runtime Server |

### 5.6 Client SDK

- Contracts + `RuntimeJsonContext` 增加字段。  
- 高层 API 可仍收 `NewSessionRunRequest`（字段在 record 上扩展）。  
- 独立 CLI 不传新字段即可。

### 5.7 明确不改清单（独立）

- `ServiceCommands` 的 `run` / REPL 组装（`BuildAgentRef` 等）  
- `StandaloneConfig*` 读写与校验语义  
- `StandaloneProviderFactory` 对外行为  
- `config` / `agent` / `doctor` 等独立命令  
- `LocalRuntime` 对 `ProviderResolver` 非空的要求  

---

## 6. 宿主侧配合职责（对接说明，非本文实现范围）

宿主在发送前应：

1. 按 **当前任务会话** 解析 Mode（创建时固定）。  
2. 从材料库取 Vendor → 解密 Key → `HostProviderConfig`。  
3. 物化 Agent → 完整 `AgentRef`（SystemPrompt、tools/skills、版本）。  
4. 组装 `NextTurnSelection`（ID + 完整 ModeOptions）。  
5. 若存在活跃 Run：UI 切换只写 pending，**不**对当前 Run 发 TurnOverride。  
6. Run 终态后下一发：带 TurnOverride（若 pending 有变）+ 必要 ProviderConfigs。  
7. 处理 `credentials.refresh.request` → `credentials.update`。

Gateway 禁止再向 Runtime 提交「空 SystemPrompt + 仅 ID」的 AgentRef。

---

## 7. 目标时序（端到端）

### 7.1 任务首次发消息

```text
宿主：材料库 → ProviderConfigs + 完整 AgentRef + Selection + InitialInput
  → run.new_session
Runtime：Registry.Upsert → CreateSession(Mode 锁定) → Run#1（冻结本轮配置）
  → 事件回 UI
```

### 7.2 同任务再发（无切换）

```text
run.existing_session(SessionId, InputOverride)
  可选：不带 TurnOverride（用会话默认）
```

### 7.3 同任务切换厂商/模型/智能体

```text
Run 进行中：UI → pending only
Run 结束 + 再发送：
  TurnOverride = 新完整 Selection
  ProviderConfigs = 新厂商（若需要）
  → Run#N 使用新配置
```

### 7.4 同项目另一任务

```text
另一 WorkSession → 另一 Runtime Session
  可完全不同的 Mode / Provider / Agent（业务 Selection 隔离）
  Registry 仍按 ProviderId 进程共享（同 Id 后写覆盖，见 §0）
```

### 7.5 换模式

```text
派生 / 新建 WorkSession → 新 run.new_session（新 Mode）
  旧 Session Mode 不变
```

---

## 8. 实施分期

| 阶段 | 内容 | 验收 |
|------|------|------|
| P0 契约 | `HostProviderConfig`；`NewSessionRunRequest.ProviderConfigs`；JsonContext | 可序列化 |
| P0 Server | Registry；serve Resolver；new_session upsert；非空 Agent / 主选型相等 / 单厂商非主约束 | 带配置的 new_session 可调到真实 Provider |
| P1 | existing_session / selection.update 的 ProviderConfigs；换厂商下一轮生效 | 多轮切换 |
| P1 | credentials.update 同步 Registry | 活跃 Run 可热更（适配器实现可选接口时）+ 同 Provider 后续轮可用 |
| P2 | 可选 providers.upsert；Work/Meeting 多 Agent 多厂商 | 多角色 |

本文 **P0–P2 仅覆盖 Runtime / CLI / Contracts / Client SDK**。宿主仓 Gateway 组装完整载荷、pending 切换门闸见 **§6 宿主侧配合职责**，由 Workstation 侧跟踪（可后续在 `04` 补章），**不纳入本文交付验收**。

---

## 9. 硬规则清单（评审与实现检查用）

1. CLI 不读宿主配置库；执行快照必须 by-value 传入。  
2. 配置颗粒度 = **任务会话（Session）** 为主，**Run** 为生效边界；非 Project 单例。  
3. Mode 每会话创建锁定；厂商/模型/智能体可按会话不同，并可在会话内按轮切换。  
4. 切换 = **下一轮生效，本轮冻结**；不中断当前 Run。  
5. Registry 是多任务会话共享缓存，须支持多 Provider 并存；本轮用到的 ProviderId = `DefaultSelection.ProviderId` ∪ ModeOptions 内非空 `AgentRef.ProviderId`（同 Id 后写覆盖）。  
6. 换 Mode = 新 Session；换 Selection 快照 = 同 Session 的下一 Run。  
7. ApiKey 仅内存 Registry + 控制通道；不进 selection 落盘明文。  
8. 独立模式行为与入口不变。  
9. `ProviderId` 与 `VendorId` **概念**不可等同；多 credential/profile 时不得共用同一 `ProviderId`，避免 Registry 覆盖。  
10. Work 模式 child Agent 列表与提示词必须完整传递，禁止空 `AvailableAgents`。

---

## 10. 源码锚点（只读对照）

路径相对于 `Netor.Madorin.Ai.Runtime/src/`，工程前缀 `Madorin.AI.Runtime.*`。

| 区域 | 路径 |
|------|------|
| serve 入口 | `Madorin.AI.Runtime.Cli/Commands/ServiceCommands.cs`（`ProviderResolver = null`） |
| 独立组装 | 同文件 `BuildNewRunRequest` / `BuildAgentRef` |
| Provider 本地工厂 | `Madorin.AI.Runtime.Cli/Config/StandaloneProviderFactory.cs`；`ProviderEntry` 在 `StandaloneConfig.cs` |
| Server 解析 | `Madorin.AI.Runtime.Server/RuntimeServer.cs` → `ResolveProviderAdapter` |
| 请求契约 | `Madorin.AI.Runtime.Contracts/Requests/NewSessionRunRequest.cs` 等 |
| Agent 快照 | `Madorin.AI.Runtime.Contracts/Agent/AgentRef.cs` |
| 凭据更新 | `CredentialsUpdateParameters`；事件 `credentials.refresh.request`（`MessageTypes`） |
| 落盘去 prompt | `Madorin.AI.Runtime.Server/RuntimeRunMetadata.RemovePromptContent` |
| Client 拉起 serve | `Madorin.AI.Runtime.Client/RuntimeClient.CreateRuntimeProcessStartInfo` |
| 文本增量事件 | 线名 `output.delta`（`MessageTypes.TextDelta`） |

宿主侧对照：F06 Runtime Gateway / 会话发送路径、F04 `WorkSession.Mode` 与会话选择状态（具体类型名见宿主仓，不纳入本文交付）。

---

## 11. 文档关系

| 文档 | 关系 |
|------|------|
| `01-概念与导航` | 「下一轮生效」不变式；本文扩展多任务颗粒度 |
| `04-宿主集成指南` | 连接/握手/RPC；本文补配置注入专章 |
| `15-Provider适配器开发指南` | 适配器与 refresh；本文定宿主注入主路径 |
| F04 | Mode 锁定、WorkSession/Task/Run 聚合 |
| F06 | 进程与 Gateway |
| F09 | 设置与凭据材料库。疑点 A：**本文 §5.2 / §3.6 提供闭合输入**；台账关闭归 F09 回写 + 总纲，本文不替代关闭 |

Workstation 仓主文（相对 `Madorin/`）：`../Netor.Madorin.Workstation/docs/CLI/开发文档/22-宿主模式配置注入与Selection颗粒度.md`。

本文为 Runtime 仓同步副本（编号 08）。

---

## 12. 修订记录

| 日期 | 说明 |
|------|------|
| 2026-09-04 | 初版：运行流程、任务级颗粒度、轮次边界切换、宿主模式注入方案；独立模式不动 |
| 2026-09-04 | 评审 D3：术语表（Selection/DefaultSelection/ModeOptions/WorkSession/WorkTask/ProviderId）；Provider 权威；映射拆行 |
| 2026-09-04 | 评审 D4：§8 宿主仓移出交付验收；独立模式 refresh 与 15 对齐为「不提供」 |
| 2026-09-04 | 评审 D1：补全 01 不变式「不新建会话」；Mode 注明 D-12；refresh 补 15 可选接口与重试；独立含 REPL |
| 2026-09-04 | 评审 D2/D5：Session 隔离与 Registry 例外对齐；ProviderConfigs 分期标 P0/P1；pending/事件/宿主锚点收边界 |
| 2026-09-04 | 评审 D11/D7：区分既有 ?? 与方案相等校验；output.delta；ProviderEntry 蓝本表；§5.5 可执行阈值；§10 路径补全 |
| 2026-09-04 | 评审 D6/D8/D9/S2：Meeting 非主规则消歧；HostProviderConfig 失败拍板；P0 挂载必填语义；F09 台账归属；兄弟仓路径 |
| 2026-09-04 | Runtime P0–P2 落地：HostProviderConfig / Registry / providers.upsert / 宿主校验；Contracts+Client 0.1.26 |
