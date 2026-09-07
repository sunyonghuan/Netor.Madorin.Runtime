# 宿主模式 Skills 渐进发现与激活

> **状态**：Progressive **已落地**（2026-09-07 裁决 + 主干实现）；Direct 见 `10`（**已落地**）  
> **前置**：`05-Skills加载与调用方案.md`  
> **对端**：Workstation `备档/裁决-技能渐进发现与激活模式-20260907.md`（宿主爆炸半径、元工具、Allowlist）  
> **原则**：独立模式零行为回归；宿主模式只改 Advertise/激活/Provider 装配，不改脚本 Grant、Catalog 物化基线、非 Skills 子系统

---

## 0. Runtime 改动纪律

### 0.1 允许改

| 落点 | 内容 |
|------|------|
| `MafSkillsAgentFactory` | 宿主/独立双分支；宿主 **Provider 永不因空 Loadable 为 null**；宿主始终动态 Source |
| 新 `SkillActivationStore` | Run 级激活表 |
| Tool result 钩子 | 识别宿主 `skill.search` hits → upsert 激活（**唯一**同轮激活主路径） |
| 宿主模式 Skills Source | Loadable 视图 + 短 description；Catalog path 优先，否则按 scope/源根惰性解析 |
| 模式判定 | 与 `HostModeSelectionValidator` / 宿主集成 Run 一致 |
| 测试 | 独立回归 + 宿主同轮 search→load + 空 Provider 探针 |

### 0.2 禁止改（防回归）

- 独立模式 paths → Provider 现网路径  
- 脚本 `builtin.skill.script.run` / Consent / Grant  
- Catalog 扫描、物化、热刷新、诊断码主语义（宿主模式可少 Advertise，不拆 Catalog 服务）  
- 工具目录 Replace/Patch、非 Skills 的 Provider 适配器  
- 在 Runtime 内建 embedding / skill 向量库  
- 要求宿主提供 workspace 目录工具（**本进程已有 WorkspaceDirectory**）

### 0.3 自有路径（已求证，不依赖宿主目录工具）

| 根 | 求证 |
|----|------|
| **workspace** | `RuntimeClientOptions.WorkspaceDirectory` → 进程 `--workspace` / 绑定；`ResolveSources` → `{ws}/.madorin/skills` |
| **user-global** | `ResolveSources` → `{UserProfile}/.madorin/skills` |
| **host-global** | `initialize.skillSources` 中宿主传入的 directory（现 sourceId=`host-global`） |

模型若需「安装侧全局技能根」字符串：宿主元工具 `skill.dir.host_global`（对端裁决）；**不**实现 `skill.dir.workspace` / `skill.dir.user_global`。

---

## 1. 双模式

| | 独立 | 宿主 |
|--|------|------|
| 判定 | 非宿主集成 | 宿主 by-value Selection / 宿主集成（与现 Host 模式校验对齐） |
| Advertise | 现网有效 Catalog name+description | **仅 Loadable**，短 description |
| Provider | paths 非空才建（现网） | **始终建**（空 Loadable 也建） |
| 激活 | 无 | Run 级；tool-result 钩子 |
| SkillIds | 可选过滤 | **Allowlist 门禁**（名称列表）；≠ Advertise 全量 paths |

---

## 2. 三层集合（Runtime 视图）

```text
Allowlist     = AgentRef.SkillIds
                null → 兼容「未声明」：宿主模式建议宿主总是下发列表；若 null 则等价「Catalog 全名允许」（慎用）
                []   → 全关，不可激活
                非空 → 仅这些 Name 可进入 Loadable

InitialSkills = ∅（V1 默认）或 Agent 侧已有默认 skill 名 ∩ Allowlist（若材料提供）
ActivationSet = Run 内钩子累积
Loadable      = UniqueByName(Initial ∪ Activation) ∩ AllowlistNames
Advertise     = Loadable 短描述
```

同名遮蔽保持 05：`workspace > user-global > 显式（含 host-global）`。  
宿主 V1 Allowlist 来自 workspace+host-global 启用名 → 纯 user-global 独有名通常不在 SkillIds 中 → `FilterBySkillIds` 后本就不在有效快照，与宿主裁决一致。

**切断膨胀**：宿主模式 **禁止** `FilterBySkillIds(全 Allowlist) → 全部 MaterializedPaths → Provider(paths)` 的现网广告路径；改为 Source 只暴露 Loadable。

---

## 3. 同轮路径（反死锁）

```text
skill.search（宿主工具，Reverse RPC）
  → 宿主只返回 hits JSON（建议含完整 description；宿主侧限制 hits 条数）
  → Runtime 收 result【钩子】Activation ∪= hit.Name（∈ Allowlist，scope 写入内部）
  → 单次最多 8 条 allowlisted hits；Run 内 Loadable 总量 ≤ 24
  → 更新 Loadable；Advertise 优先 hit 完整描述（≤1024，超长句界截断）
  → 交模型（tool result 原文不二次截断）
load_skill(name)
  → name ∈ Loadable
  → Content：Catalog MaterializedPath 优先；否则按激活 scope / 绑定源根（workspace > user-global > 显式）惰性解析目录中的 SKILL.md（不信任模型绝对路径）
  → 返回完整 body；read_skill_resource 读 references/assets
```

禁止：宿主回调内同步 Runtime activation RPC。  
可选：`skills.activation.upsert` 仅用于 Run 创建下发 Initial（非 search 回调）。

---

## 4. Provider 与 MAF 探针（门禁）

1. 宿主模式 `CreateSkillsProvider` **不得**因 Loadable 空返回 null。  
2. 探针 A：空 skills 时 `load_skill` 工具是否仍注册。  
3. 探针 B：同一次 Agent 循环内，钩子激活后 `load_skill` 是否解析到新 name。  
4. 探针失败则改 InMemory 可追加实现，**同轮 search→load 为发布阻塞门禁**。

`DisableCaching=true` 保留；不作为探针 B 的唯一依赖。

---

## 5. load 校验

1. Name ∈ Loadable（且 ∈ 当前 Allowlist，防止中途 enablement 收回）。  
2. 路径仅来自 Catalog/物化/绑定源，**忽略**模型传入的绝对路径。  
3. 脚本仍 Gateway + Grant。  
4. 未激活 → 明确错误（提示先 skill.search）。

---

## 6. 实现清单

1. 宿主模式分支 + Provider 常驻  
2. `SkillActivationStore`（Run）  
3. skill.search tool-result 钩子  
4. HostMode Source / 可追加 Loadable  
5. Get* → MaterializedPath 优先  
6. MAF 探针 A/B  
7. 独立模式回归全绿  
8. 文档 `12-Skills系统.md` 宿主节与本文一致  

---

## 7. 禁止

- 独立模式行为漂移  
- Runtime embedding  
- 空激活 Provider=null  
- 回调路径 Host→Runtime 激活 RPC  
- 全 Allowlist 当 Advertise  
- 要求宿主提供 workspace 目录工具  

---

## 8. 验收

1. 独立测试零回归。  
2. 宿主：首轮 Advertise 无全库长 description；三工具存在。  
3. 同轮 search→load 通过探针 B。  
4. Allowlist 外无法 load。  
5. 无 workspace 目录元工具依赖。  

---

## 9. 变更记录

| 日期 | 说明 |
|------|------|
| 2026-09-07 | 初稿与评审修订 |
| 2026-09-07 | 严格复审：Runtime 自知 workspace/user-global；删除对 workspace 目录工具依赖；与宿主双根 Allowlist 对齐；爆炸半径与探针门禁写死 |

---

## 10. 发现策略 Direct（指针）

> **完整策划 + 代码推演**：同目录 `10-宿主模式Skills发现策略Direct.md`（含 R1–R10）。  
> 对端：Workstation F08 裁决 / 执行规格。

| 项 | 状态 |
|----|------|
| Progressive（本文 §1–§8） | **已实现** |
| `SkillDiscoveryMode=direct` | **已实现**（`AgentRef` / Context / Meeting JSON） |
| `skills.discoveryDirect` | **已实现**（initialize `true`） |

宿主关「技能按需发现」前仍应读 `skills.discoveryDirect=true`。

Direct 摘要：

- `Loadable` 名集 = Allowlist（短描述 Advertise）；**不**灌全文 paths  
- **不**占用 `DefaultMaxLoadableSkills=24` 激活名额  
- search 钩子 no-op；load 仅 Allowlist 门禁  
- §7「禁止全 Allowlist 当 Advertise」= 禁止当 **长正文/全 paths**；短描述名表在 direct 下允许

### 10.1 Progressive 现码锚点（防漂移）

| 锚点 | 符号 |
|------|------|
| Source | `HostModeLoadableSkillsSource` |
| 工厂 | `MafSkillsAgentFactory.CreateSkillsProvider` / `ResolveHostLoadableMaterializedPaths` |
| 激活 | `SkillActivationStore`（Max 24） |
| 钩子 | `SkillSearchActivationHook`（MaxHits 8） |
| 投影 | `HostModeSkillsSnapshot.ProjectAdvertiseEmpty` |
| 空 Loadable | 内联 `host-skills-guide` 保 MAF 工具注册 |
| 上下文 | `SkillsInvocationContext.IsHostMode` + maps |

---

## 11. skill.search hits 规约（宿主主责）

- hits：**完整 description**（非 body），宿主 **1024 + 边界截断**（可对齐 `BoundDescription`；Runtime 边界字符见 10 §R8）。  
- **topK ≤ 8**；宽查询宿主先收窄；Runtime 钩子防御性再限 8。  
- body 只经 `load_skill`。  
- 详见 `10` §9 与宿主 F08 裁决。

---

## 12. 变更记录（续）

| 日期 | 说明 |
|------|------|
| 2026-03-26 | §10 Direct 指针；详规见 `10` |
| 2026-03-26 | **代码推演**：标明 Direct 未落地；补 Progressive 锚点与 capabilities/AgentRef 缺口；hits 规约并入 §11 |
| 2026-03-26 | Direct 落地（见 `10`）；§10 状态改为已实现 |

