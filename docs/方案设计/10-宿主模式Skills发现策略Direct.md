# 10 · 宿主模式 Skills 发现策略 Direct（策划）

> **状态**：定稿（2026-03-26）  
> **前置**：`09-宿主模式Skills渐进发现与激活.md`（Progressive 基线）  
> **对端宿主**：Workstation  
> - `docs/F08-插件技能与MCP/备档/裁决-渐进发现双开关-20260326.md`  
> - `docs/F08-插件技能与MCP/执行/F08-渐进发现开关-完整流程规格.md`  
> **原则**：独立模式零回归；Direct 只扩展宿主模式 Loadable 初值与契约，不改脚本 Grant / Catalog 基线

---

## 1. 问题

宿主任务级「技能按需发现」关闭后：宿主不再注入 `skill.search` / `skill.dir.host_global`。  
若 Runtime 仍按 09 Progressive（Loadable 初始可空、靠 search 钩子激活）→ **技能名不可见 = 假死**。

故 Runtime 必须识别 **SkillDiscoveryMode=direct**，在 Run 边界将 Loadable 置为全 Allowlist。

---

## 2. 契约（宿主 → Runtime）

每 Run / Agent 材料（扩展现有宿主集成路径，字段名可落 AgentRef 或等价）：

| 字段 | 类型 | 语义 |
|------|------|------|
| `SkillIds` | `string[]` \| null | Allowlist（与 09 相同） |
| `SkillDiscoveryMode` | `progressive` \| `direct` | 缺省 / 未知 = **progressive**（旧宿主兼容） |

**禁止**：Source / load 热路径反向 RPC 问宿主开关。  
**真相**：Run 开始时冻结 mode + SkillIds。

### 2.1 能力声明（供宿主门闩）

Runtime 在 capabilities / 版本面声明支持 direct，例如：

- `skills.discoveryModes = ["progressive","direct"]`  
  或布尔 `skills.discoveryDirect = true`

宿主关技能按需前必须确认；否则 UI 禁止或回滚（防假死）。

---

## 3. 双策略（宿主模式内）

在 **宿主集成** 判定成立后：

| | progressive（默认） | direct |
|--|---------------------|--------|
| Allowlist | SkillIds | 同左 |
| Loadable 初值 | (Initial∪Activation)∩Allowlist；Activation 多由 search 钩子 | **= Allowlist 全部 Name**（启用全集） |
| Advertise | Loadable 短 description | 同左（一次可见全启用名） |
| skill.search 钩子 | 生效 | **no-op**（宿主通常已不注入该工具） |
| load_skill | name∈Loadable | name∈Allowlist（=Loadable） |
| resource/script | 09 不变 | **不变**（Grant/Consent 不降级） |
| Provider | 常驻（空也建） | 常驻 |

```text
# progressive（09 原式）
Loadable = Unique(Initial ∪ Activation) ∩ Allowlist

# direct（本策划）
Loadable = Unique(Allowlist)   // 仍 ∩ Catalog 有效名；无 Catalog 条目则 load 时明确失败
Advertise = Loadable 短描述
# 仍禁止：Allowlist → 全量 MaterializedPaths 长正文灌 Provider
```

**切断膨胀仍成立**：Direct 只扩大**名表短描述**，不把 SKILL 正文全量进 system/Provider paths 广告。

---

## 4. HostMode Source 行为

自定义 / 宿主模式 Source（与 09 同一组件，分支 mode）：

| 方法 | progressive | direct |
|------|-------------|--------|
| 列举/Advertise | Loadable | Allowlist 全名短描述 |
| Get skill 正文 | Catalog path 优先；∈Loadable | 同；∈Allowlist |
| Get resource | 已 load 技能树 | 同 |
| Get/run script | Gateway + Grant | **同，不放松** |

路径解析权威 = Runtime Catalog + 绑定源根（workspace / user-global / host-global），**不**另建「只拿 Name 再向宿主要路径」的第二权威。

---

## 5. 与宿主开关时序

```text
用户关「技能按需发现」（任务）
  → 宿主：持久化 SkillDiscoveryMode=direct
  → 宿主：工具目录去掉 skill meta（立即）
  → 用户下次发送
  → 宿主 Materializer：SkillIds + SkillDiscoveryMode=direct
  → Runtime：Direct Loadable
```

已在途 Run **不回溯** mode。

---

## 6. 实现清单（Runtime）

1. 解析 `SkillDiscoveryMode`（缺省 progressive）  
2. HostMode 工厂 / Source：direct 分支 Loadable 初值  
3. search 钩子：direct 下 no-op  
4. capabilities 声明 direct  
5. 探针：direct 下无 search 亦可 load 启用名；Allowlist 外拒绝  
6. 独立模式回归全绿  
7. 回写 `开发文档/12-Skills系统.md` 宿主节  

---

## 7. 禁止

- 独立模式行为漂移  
- Direct = 全量长 description / 全文 SKILL 进 prompt  
- Direct 放松脚本 Grant  
- 无能力声明时静默接受 direct 导致宿主已摘 meta  

---

## 8. 验收

| # | 场景 | 期望 |
|---|------|------|
| D1 | mode 缺省 | 等同 progressive |
| D2 | direct + SkillIds=[a,b] | Advertise 含 a,b 短描述 |
| D3 | direct + load(a) | 成功 |
| D4 | direct + load(未启用) | 拒绝 |
| D5 | direct + 无 skill.search | 仍可 D3 |
| D6 | progressive 同轮 search→load | 09 探针 B 仍过 |
| D7 | 独立 CLI | 零回归 |
| D8 | capabilities | 宿主可读到 direct 支持 |

---

## 9. 变更记录

| 日期 | 说明 |
|------|------|
| 2026-03-26 | 初稿：Direct 契约、Loadable 初值、Source 分支、能力门闩；对端 F08 双开关裁决 |