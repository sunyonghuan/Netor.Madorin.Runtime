# 10 · 宿主模式 Skills 发现策略 Direct

> **状态**：**已落地**（2026-03-26 策划推演 + 实现）  
> **Progressive 基线**：已在主干实现（见 §R / 09）  
> **前置**：`09-宿主模式Skills渐进发现与激活.md`  
> **对端宿主**：Workstation F08  
> - `备档/裁决-渐进发现双开关-20260326.md`  
> - `执行/F08-渐进发现开关-完整流程规格.md`  
> **原则**：独立模式零回归；Direct 只扩展宿主模式 Loadable 初值与契约，不改脚本 Grant / Catalog 基线

---

## R. 文档 × 现码推演评审（必须先读）

对照 2026-03-26 主干代码后的结论：**09 Progressive 主路径已落地；本文初稿把 Direct 写成「定稿可执行」但代码侧零实现。** 下列为逻辑/流程级问题；实施以本节 + §2～§6 冻结为准。

### R1. 【严重】`SkillDiscoveryMode` 在代码中不存在

| 位置 | 现码事实 |
|------|----------|
| `Contracts/Agent/AgentRef.cs` | 仅 `AgentId/PromptTemplateVersion/SystemPrompt/ProviderId/ModelId/AllowedToolIds/SkillIds`，**无** DiscoveryMode |
| `MeetingParticipant` 手写 JSON | `WriteAgentRef` / `ReadAgentRef` 只序列化上述字段 |
| `SkillsInvocationContext` | 有 `IsHostMode` / `ActivationStore` / `AllowlistSkillIds` / Catalog maps，**无** mode |
| 全 `src/**/*.cs` | **零** `SkillDiscoveryMode` / `discoveryDirect` 符号 |

→ 宿主即使下发 mode，Runtime **无法识别**；关技能按需后仍走 Progressive 空 Loadable → **假死**（正是本文要解决的问题，但尚未开工）。

### R2. 【严重】能力位 `skills.discoveryDirect` 未声明

现码 `SkillsCapabilities`：

```csharp
// Contracts/Capabilities/SkillsCapabilities.cs
SkillsCapabilities(
  Enabled, SourceKinds, ResourcesSupported, ScriptsSupported, HotRefreshSupported)
// 无 DiscoveryDirect
```

`RuntimeServer` initialize 构造 Skills 时同样未写 discovery 字段。  
宿主门闩若按 F08 读 `skills.discoveryDirect`，**今日恒为不支持** → 正确行为是 **禁止关技能按需**；文档不得暗示「已声明 true」。

### R3. 【严重】Direct Loadable 算法与现 `ResolveHostLoadableMaterializedPaths` 不一致

现码（Progressive only）：

```text
loadableNames = ActivationStore.GetLoadableNames(Allowlist)   // 激活 ∩ Allowlist
  若空且 SkillNames/Paths 非空 → 用 SkillNames 当 Initial（可选）
paths = 各 name 的 Catalog path / 投影 paths / ScopeRoots 惰性解析
```

- 宿主 V1：`HostModeSkillsSnapshot.ProjectAdvertiseEmpty` **清空** `SkillNames/Paths` → Initial 实际恒 ∅。  
- Direct 需要：`loadableNames = Allowlist 全部（∩ Catalog 有效名）`，**不依赖** Activation。  
- 若只「启动时把 Allowlist 灌进 ActivationStore」：会撞 `DefaultMaxLoadableSkills = 24` 硬顶 → Allowlist>24 时 Direct **残缺**。  
  **冻结**：Direct **不走** Activation 名额；`ResolveHostLoadable*` / `IsLoadable` 按 mode 分支，24 顶仅约束 Progressive 激活。

### R4. 【中】`TryResolveLoadPath` 硬绑「先 search」

现码 host 分支：

```text
不在 Allowlist → 拒绝
ActivationStore 空或不含 name → "Call skill.search first."
```

Direct 下 load 合法条件应为 **∈ Allowlist**（且路径可解析），**禁止**再要求 activation / search。  
文案错误码需区分 mode，避免 Direct 用户看到「先 search」。

### R5. 【中】空 Loadable 的 `host-skills-guide` 内联技能

`HostModeLoadableSkillsSource.GetSkillsAsync`：paths 空时注入内联技能：

- name: `host-skills-guide`  
- 文案：**Call skill.search first, then load_skill**

| mode | 期望 |
|------|------|
| progressive + 空激活 | 保留 guide（或等价）以维持 MAF 三工具注册（探针 A） |
| direct + Allowlist 非空 | **不应**再依赖 guide；应 Advertise Allowlist 短描述 |
| direct + Allowlist 空/`[]` | 仍可 guide 或空 Provider 策略；全关时 load 全拒 |

Direct 实现时 guide 文案不得再写死 search-only。

### R6. 【中】search 钩子无 mode 短路

`SkillSearchActivationHook.TryApplySearchResult`：识别 toolId/name 后解析 hits，**无** discovery mode 参数。  
Direct：应 **no-op 直接 return 0**（防御：宿主若仍误注入 search）。  
识别已支持：`host.skill.search` / `skill.search` / `skill_search` / 后缀与正则。

### R7. 【中】JSON 契约双路径

| 路径 | 现码 |
|------|------|
| `AgentRef` + `RuntimeJsonContext` | source-gen 序列化（Expert/Work by-value） |
| Meeting `MeetingParticipant` | **手写** Read/WriteAgentRef |

扩展 `SkillDiscoveryMode` 必须 **两处同时改**，否则 Meeting 丢字段。  
JSON 名冻结：`skillDiscoveryMode`（camelCase，与 `skillIds` 一致）。

### R8. 【低】BoundDescription 边界与文档用词

现码 `HostModeLoadableSkillsSource.BoundDescription`：

- 上限 `MaxAdvertiseDescriptionLength = 1024`  
- 第一遍边界：`. ! ? 。 ！ ？ \n ;`（**无**中文 `；`，无空白）  
- 第二遍：空白  
- 后缀 `…`  
- cut 过小（< min(80, max/2)）则硬切 maxLength  

文档写「对齐 Runtime」时须与此一致；宿主可多认 `；`，但不得声称与 Runtime 逐字符同一实现除非共享代码。

### R9. 【低】09 已写、代码已有、本文初稿未钉死的 Progressive 常数

| 常数 | 代码 | 含义 |
|------|------|------|
| `DefaultMaxHitsPerSearchResult` | 8 | 单次 search 结果激活上限 |
| `DefaultMaxLoadableSkills` | 24 | Run 内激活种类硬顶（仅 Progressive） |
| 钩子入口 | Work/Expert/Meeting 的 tool-result → `HostModeSkillsSnapshot.TryApplyToolResult` | 唯一同轮激活主路径 |

### R10. 【低】独立模式

`CreateSkillsProvider`：非 host 仍要求 `SkillMaterializedPaths` 非空才建 Provider。  
Direct **只**在 `context.IsHostMode == true` 生效；独立 CLI 零改语义。

---

## 0. 落地状态总表

| 能力 | 代码状态 | 文档 |
|------|----------|------|
| 宿主模式判定 | ✅ `isHostModeSkills` ← `_hostProviderRegistry is not null` 等 | 09 |
| Provider 常驻 + DisableCaching | ✅ `MafSkillsAgentFactory` | 09 |
| ActivationStore Run 级 | ✅ `RuntimeExecutionCore` 创建 | 09 |
| search 钩子激活 | ✅ `SkillSearchActivationHook` | 09 |
| Loadable Source + BoundDescription | ✅ `HostModeLoadableSkillsSource` | 09 |
| Advertise 空投影 | ✅ `ProjectAdvertiseEmpty` | 09 |
| **SkillDiscoveryMode** | ✅ `AgentRef` + Meeting JSON + Context | 本文 §2～§6 |
| **skills.discoveryDirect** | ✅ initialize `true` | 本文 §2.1 |
| Direct Loadable=Allowlist | ✅ Resolve/TryResolve 分支，不占 24 顶 | 本文 §3 |

**宿主 F08**：在 Runtime 未声明 `discoveryDirect=true` 前，**禁止**关闭技能按需发现（或关后回滚）——与代码现状一致。

---

## 1. 问题（Direct 要解决什么）

宿主任务「技能按需发现 = 关」后：不再注入 `skill.search` / `skill.dir.host_global`。  
若 Runtime 仍 Progressive（Loadable≈激活集，常空）→ 技能名不可见 = **假死**。

故 Runtime 必须识别 **SkillDiscoveryMode=direct**，在 **每次构建 SkillsInvocationContext / Resolve Loadable** 时将可 Advertise/load 的名集视为全 Allowlist（短描述），且 **不**把全文 SKILL 灌进 Provider paths 广告。

---

## 2. 契约（宿主 → Runtime）· 冻结

### 2.1 材料字段

落点：**`AgentRef`**（by-value Selection 已有通道），不要另发明第二 RPC。

| 字段 | JSON 名 | C# | 语义 |
|------|---------|-----|------|
| SkillIds | `skillIds` | `string[]?` | Allowlist（已有） |
| SkillDiscoveryMode | `skillDiscoveryMode` | `string?` | `progressive` \| `direct`；**缺省/null/未知 = progressive** |

```csharp
// 目标形状（待实现）
public sealed record AgentRef(
    string AgentId,
    string PromptTemplateVersion,
    string SystemPrompt,
    string? ProviderId = null,
    string? ModelId = null,
    string[]? AllowedToolIds = null,
    string[]? SkillIds = null,
    string? SkillDiscoveryMode = null);  // 新增
```

**必须同步**：

1. `AgentRef` 记录  
2. `RuntimeJsonContext` 源生成（随 record 自动）  
3. `MeetingParticipant.WriteAgentRef` / `ReadAgentRef` 手写读写  
4. 各 Mode 从 `agent.SkillDiscoveryMode` → `SkillsInvocationContext`（见 §4）

**禁止**：Source / `load_skill` 热路径反向 RPC 问宿主开关。  
**真相**：进入 Orchestrator 构建 context 时冻结 mode + SkillIds；已在途 Run 不回溯。

### 2.2 能力声明（供宿主门闩）· 冻结

扩展现有 `SkillsCapabilities`（**唯一**布尔，不加数组）：

```csharp
public sealed record SkillsCapabilities(
    bool Enabled = true,
    string[]? SourceKinds = null,
    bool ResourcesSupported = true,
    bool ScriptsSupported = true,
    bool HotRefreshSupported = true,
    bool DiscoveryDirect = false);  // 新增；实现 Direct 后 initialize 置 true
```

对外 JSON（STJ camelCase）：

```json
{
  "skills": {
    "enabled": true,
    "sourceKinds": ["directory"],
    "resourcesSupported": true,
    "scriptsSupported": true,
    "hotRefreshSupported": true,
    "discoveryDirect": true
  }
}
```

| `discoveryDirect` | 语义 |
|-------------------|------|
| `true` | 本进程支持 `skillDiscoveryMode=direct` |
| 缺省 / `false` | **不支持**；宿主不得关技能按需 |

落点：`RuntimeServer` 构造 `InitializeResponse` 的 `Skills:` 实参。  
实现 Direct 功能的**同一变更集**内必须置 `true`，禁止「代码已 direct、能力位仍 false」或反向。

---

## 3. 双策略（宿主模式内）

`IsHostMode == true` 时：

| | progressive（默认 · 已实现） | direct（待实现） |
|--|------------------------------|------------------|
| Allowlist | `SkillIds` | 同左 |
| Loadable 名集 | `(Initial ∪ Activation) ∩ Allowlist`；V1 Initial=∅ | **`Allowlist` 全部 Name**（再 ∩ 能解析到路径的 Catalog/源根） |
| Advertise | Loadable 短描述（Activation 描述优先，否则 CatalogDescriptions，Bound 1024） | 同算法，名集=Allowlist |
| 激活名额 24 | 生效 | **不占用**；不灌 ActivationStore 冒充 Direct |
| search 钩子 | 生效，topK≤8 | **no-op** |
| load 门禁 | ∈ Loadable（激活+Allowlist） | ∈ Allowlist（+路径可解析） |
| 空名集 UI | `host-skills-guide` 保三工具 | Allowlist 非空则出真实名表；空则 guide/空策略另定 |
| script Grant | Gateway + Grant | **不放松** |
| Provider | 常驻 | 常驻 |

```text
# progressive（现码）
LoadableNames = Activation.GetLoadableNames(Allowlist)
                ?? Initial SkillNames 投影（宿主 V1 通常为空）

# direct（目标）
LoadableNames = Allowlist 中可解析路径的 Name
                // Allowlist null = 慎用「全 Catalog 名」（与 IsAllowed 现语义一致）
                // Allowlist [] = 全关
```

**切断膨胀**：Direct 仍禁止 `FilterBySkillIds → 全部 MaterializedPaths → 独立模式式 Provider(paths)` 把长正文当广告；继续走 `HostModeLoadableSkillsSource`（Inline 短 description + 按需 body）。

---

## 4. 实现落点（对照现码 · 可执行清单）

| # | 落点 | 改动 |
|---|------|------|
| 1 | `AgentRef` + Meeting 手写 JSON | + `SkillDiscoveryMode` / `skillDiscoveryMode` |
| 2 | `SkillsCapabilities` + `RuntimeServer` initialize | + `DiscoveryDirect`；实现后 `true` |
| 3 | `SkillsInvocationContext` | + `SkillDiscoveryMode` 或 `bool IsDirectDiscovery`（建议枚举/字符串与 Agent 一致） |
| 4 | Work / Expert / Meeting 构建 context | `agent.SkillDiscoveryMode` → context；缺省 progressive |
| 5 | `MafSkillsAgentFactory.ResolveHostLoadableMaterializedPaths` | direct 分支：名集=Allowlist |
| 6 | `MafSkillsAgentFactory.TryResolveLoadPath` | direct：跳过 activation 检查；错误文案不提 search |
| 7 | `HostModeLoadableSkillsSource.GetSkillsAsync` | direct 空 Allowlist vs progressive guide 文案 |
| 8 | `SkillSearchActivationHook.TryApplySearchResult` 或调用方 | context/mode 为 direct 时 return 0 |
| 9 | 脚本路径 `RunScriptThroughRuntimeAsync` | 已走 `TryResolveLoadPath`，随 6 自动正确 |
| 10 | 测试 | 见 §8；**独立模式回归全绿** |

**不改**：

- Catalog 扫描/物化/热刷新主语义  
- `builtin.skill.script.run` Consent/Grant  
- 非 host 的 paths Provider  
- 工具目录 Replace（宿主职责）

### 4.1 mode 解析规则（冻结）

```text
raw = agent.SkillDiscoveryMode?.Trim()
if raw is null or empty → progressive
if ordinal-ignore-case equals "direct" → direct
else → progressive   // 未知值保守，不抛
```

若 `direct` 但进程 `DiscoveryDirect != true`：  
Runtime **降级为 progressive** 并打诊断/日志（可选）；宿主侧应以能力位禁止下发。双端都要防假死。

### 4.2 Direct 与 MaxLoadable=24

- Progressive：保持 `SkillActivationStore.DefaultMaxLoadableSkills = 24`。  
- Direct：**禁止**用「把 N 个 Allowlist 名 TryActivate」实现（N>24 静默丢名）。  
- 正确：Resolve 时直接枚举 Allowlist。

### 4.3 Advertise 描述来源（Direct）

与现 `ResolveAdvertiseDescription` 相同优先级：

1. ActivationStore 条目 ShortDescription（Direct 通常无）  
2. `CatalogDescriptionsByName`  
3. fallback = name  

一律 `BoundDescription(..., 1024)`。

---

## 5. 与宿主开关时序

```text
用户关「技能按需发现」
  → 宿主：读 capabilities.skills.discoveryDirect == true？
       否 → UI 禁止或回滚（今日代码路径）
       是 → 持久化任务 SkillDiscoveryMode=direct
  → 宿主：目录去掉 skill meta（立即）
  → 下次发送
  → Materializer：AgentRef.skillIds + skillDiscoveryMode=direct + SystemPrompt 后缀
  → Runtime：context mode=direct → Loadable=Allowlist 短描述
```

已在途 Run **不**改 mode。

---

## 6. Progressive 现码基线（评审锚定，防文档漂移）

实现 Direct 时不得破坏下列已验证行为：

| 项 | 代码锚点 |
|----|----------|
| 宿主 Provider 永非 null（空 Loadable 也建） | `CreateSkillsProvider` + guide 技能 |
| 同轮 search→load | tool-result → `TryApplyToolResult` → Activation → `DisableCaching` GetSkills |
| search 工具识别 | `CanonicalToolId=host.skill.search` 等 |
| hits 解析 | `hits` / `results` / `skills` 数组；name/skillName/skill/id |
| 单次激活 ≤8 | `DefaultMaxHitsPerSearchResult` |
| Run 激活 ≤24 | `DefaultMaxLoadableSkills` |
| Allowlist null/[]/非空 | `SkillActivationStore.IsAllowed` |
| 路径权威 | Catalog map → 投影 paths → ScopeRoots（activation scope 优先） |
| 独立模式 | paths 空 → Provider null |

---

## 7. 禁止

- 独立模式行为漂移  
- Direct = 全量长 description / 全文 SKILL 进 system 或 paths 广告  
- Direct 放松脚本 Grant  
- 无 `discoveryDirect=true` 时静默当已支持（宿主已摘 meta）  
- 用 ActivationStore 灌满 Allowlist 冒充 Direct（撞 24 顶）  
- 第二套 mode RPC / 热路径问宿主  
- `skills.discoveryModes` 数组双源  

---

## 8. 验收

### 8.1 Progressive 回归（实现 Direct 前后都要绿）

| # | 期望 |
|---|------|
| P1 | 独立 CLI 测试零回归 |
| P2 | 空激活仍有 load_skill 等工具（guide 或等价） |
| P3 | 同轮 search→load 探针 B |
| P4 | Allowlist 外无法 load |
| P5 | 单次 search 激活 ≤8；Run 激活 ≤24 |

### 8.2 Direct（新）

| # | 期望 |
|---|------|
| D1 | mode 缺省 / 未知 = progressive |
| D2 | direct + SkillIds=[a,b] → Advertise 含 a,b 短描述（非 guide-only） |
| D3 | direct + load(a) 成功且**无需** search |
| D4 | direct + load(未启用) 拒绝 |
| D5 | direct + 无 skill.search 工具仍可 D3 |
| D6 | direct + Allowlist 长度 >24 仍全部可 Advertise/load（不丢名） |
| D7 | direct 下 search 钩子 no-op（若仍调用） |
| D8 | initialize `skills.discoveryDirect === true` |
| D9 | Meeting 路径 round-trip 保留 `skillDiscoveryMode` |
| D10 | progressive 用例 P1–P5 仍过 |

---

## 9. 与宿主 skill.search 边界

| 项 | 宿主 | Runtime |
|----|------|---------|
| hits description | 完整描述，≤1024，宿主主截断 | 钩子存 ShortDescription；Advertise 再 Bound |
| body | search **不**返回 | 仅 load_skill / Source body |
| topK | ≤8 返回前收窄 | 防御性 `DefaultMaxHitsPerSearchResult=8` |
| 宽查询 | 宿主先收窄 | 不依赖 Runtime 截长列表 |

开关语义（对端）：**技能按需发现默认开** = progressive。

---

## 10. 分期（Runtime）

| 阶段 | 内容 | 预估 |
|------|------|------|
| R-S0 | 契约：AgentRef mode + SkillsCapabilities.DiscoveryDirect + Meeting JSON | 0.5～1d |
| R-S1 | context 贯穿 + Resolve/TryResolve/钩子/guide 分支 | 1.5～2.5d |
| R-S2 | 测试 D1–D10 + Progressive 回归 + 文档 12 回写 | 1～1.5d |

**门闩**：R-S0 末若尚未置 `discoveryDirect=true`，不得合入「半套 Direct」。  
与宿主 F08 S2 **联调窗口**对齐。

---

## 11. 变更记录

| 日期 | 说明 |
|------|------|
| 2026-03-26 | 初稿：Direct 契约与 Loadable 语义 |
| 2026-03-26 | 能力位名冻结 `discoveryDirect` |
| 2026-03-26 | **代码推演 R1–R10**：标明 Direct 未实现；钉死 AgentRef/Meeting/Capabilities/Resolve 落点；禁止 Activation 灌满；24 顶仅 Progressive；guide/错误文案；验收 D6/D9 |
| 2026-03-26 | **实现落地**：AgentRef/Meeting JSON、SkillsCapabilities.DiscoveryDirect、Context 贯穿、Resolve/TryResolve/钩子/guide 分支；测试 D* + Progressive 回归；`discoveryDirect=true` |

---

## 12. 评审结论

| 级别 | 数 | 代表 |
|------|----|------|
| 严重 | 3 | mode 字段缺失；能力位缺失；Direct 与 Resolve/24 顶冲突 |
| 中 | 4 | load 文案绑 search；guide 文案；钩子无短路；JSON 双路径 |
| 低 | 3 | Bound 边界字符；Progressive 常数；独立模式边界 |

**本文初稿不可按「已定稿已实现」开工**；按 §R 修正后的 §2～§8 实施。  
**Progressive（09）可继续作为宿主默认**；Direct 为显式增量，合入前宿主必须靠 `discoveryDirect` 门闩。
