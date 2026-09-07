# Skills 系统

> 当前生效版本：Madorin.AI.Runtime V1
>
> 本文描述 Skills 系统的架构、技能定义格式、加载机制和宿主集成方式。Skills 让模型在调用时获得额外的专业知识和可执行脚本。

## 1. 概述

Skills 是带有结构化定义的扩展包，每个 Skill 包含一份 `SKILL.md` 定义文件和可选的资源/脚本。Runtime 在 Invocation 开始时加载当前目录快照，将 Skill 内容注入 Agent 上下文或暴露为工具。

> **宿主集成模式（2026-09-07，严格复审）**：宿主提供 `skill.search` 与 **`skill.dir.host_global`**（仅安装侧全局技能根）；工作区技能根由本进程 `WorkspaceDirectory` 解析，**无** workspace 目录元工具。激活 = Runtime tool-result 钩子；`load_skill` 本地物化。独立 CLI 以本节为准。详见 [`../方案设计/09-宿主模式Skills渐进发现与激活.md`](../方案设计/09-宿主模式Skills渐进发现与激活.md)。

核心特性：

- **目录扫描**：从工作区和全局目录自动发现技能
- **内容物化**：技能文件被复制到 Runtime 管理的物化目录，Invocation 引用物化路径
- **热刷新**：文件系统监控 + 按需刷新，Invocation 边界自动拉取最新版本
- **目录版本**：基于所有技能内容哈希计算的版本号，变化时发出 `skill.catalog.changed` 事件

## 2. 技能来源

| 来源 ID | 类型 | 路径 | 说明 |
| --- | --- | --- | --- |
| `workspace` | Directory | `<workspace>/.madorin/skills/` | 工作区技能 |
| `user-global` | Directory | `~/.madorin/skills/` | 用户全局技能 |
| （自定义） | Directory | 宿主指定 | 宿主通过 Client SDK 传入的显式来源 |

来源类型目前仅支持 `Directory`。`SkillSourceKind` 枚举中虽已定义 `Mcp`，但尚未实现，使用时会产生 `SkillSourceKindNotSupported` 错误（另见 §10 能力声明中 `sourceKinds` 仅含 `Directory`）。

来源解析优先级：

1. 工作区来源（`workspace`）
2. 全局来源（`user-global`）
3. 宿主显式来源

同名技能按来源顺序遮蔽（先到先得），被遮蔽的技能产生 `SkillShadowed` 诊断警告。同一来源内同名技能全部禁用并产生 `SkillDuplicateInSource` 错误。

> 注：`SkillShadowed` 在源码中以字符串字面量发出（而非 `RuntimeErrorCodes` 常量），`SkillDuplicateInSource` 则通过 `RuntimeErrorCodes` 常量发出，属命名一致性细节，不影响诊断行为。

自动来源（`workspace`、`user-global`）目录不存在时静默跳过；显式来源目录不存在时刷新失败。

## 3. 技能定义格式

每个技能是一个目录，目录名必须与 `name` 字段一致（不区分大小写）。

### 3.1 目录结构

```text
skills/
└── code-review/
    ├── SKILL.md           ← 必须有，定义和内容
    ├── references/
    │   ├── style-guide.md ← 资源文件
    │   └── patterns.json
    └── scripts/
        └── lint.ps1       ← 脚本文件
```

### 3.2 SKILL.md 格式

```markdown
---
name: code-review
description: Reviews code for style, correctness, and performance issues.
---

# Code Review Skill

## Instructions

When reviewing code, follow these steps:
1. Check formatting against the style guide
2. Look for common anti-patterns
3. Verify error handling

## References

- [Style Guide](references/style-guide.md)
- [Common Patterns](references/patterns.json)
```

Frontmatter 字段：

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `name` | 是 | 技能名称，格式 `^[a-z0-9]+(-[a-z0-9]+)*$` |
| `description` | 是 | 技能描述 |

Frontmatter 之后是 Markdown 正文，作为 Skill 的主要指令内容。

### 3.3 文件类型

| 目录 | 文件类型 | 说明 |
| --- | --- | --- |
| 根目录 | `SKILL.md` | 技能定义，必须有且只有一个 |
| `scripts/` | `.ps1`、`.sh`、`.py`、`.js`、`.cs`、`.csx` | 可执行脚本 |
| 其他位置 | `.md`、`.txt`、`.json`、`.yaml`、`.xml`、`.html`、`.css`、`.js`、`.ts`、`.png`、`.jpg`、`.svg` 等 | 资源文件 |

不在支持列表中的扩展名会被跳过并产生诊断警告。

## 4. 限制

| 约束 | 值 |
| --- | --- |
| 目录中技能总数上限 | 512 |
| 单个技能文件数上限 | 256 |
| SKILL.md 大小上限 | 1 MB |
| 单个文件上限 | 8 MB |
| 单个技能所有文件合计上限 | 16 MB |
| 符号链接 / 目录联接 | 禁止 |

## 5. 物化与版本

刷新时，Runtime 将所有技能文件复制到 `<data-dir>/skill-catalog/instances/<instance-key>/` 下的物化目录。Invocation 使用物化路径而非原始路径，确保运行期间文件稳定。

目录版本号基于所有物化文件内容哈希计算。任何技能内容变化（新增、修改、删除）都会产生新版本号。

## 6. 热刷新机制

- 初始化时扫描所有来源目录并建立 `FileSystemWatcher`。
- 文件变化触发 250ms 防抖，标记目录为 stale。
- 下一次 Invocation 开始前，如果目录 stale，自动执行 `RefreshAsync`。
- 刷新失败时不使已有目录失效，Invocation 继续使用上一次的快照。
- 版本变化时发出 `skill.catalog.changed` 事件。

## 7. CLI 操作

| 命令 | 说明 |
| --- | --- |
| `madorin skill list` | 列出当前目录中的所有技能 |
| `madorin skill show <name>` | 查看指定技能的详细信息 |
| `madorin skill validate` | 验证技能定义格式 |
| `madorin skill refresh` | 手动触发目录刷新 |

## 8. 宿主集成

宿主通过 Client SDK 操作 Skills：

| API | 说明 |
| --- | --- |
| `GetSkillCatalogAsync()` | 获取当前技能目录摘要 |
| `RefreshSkillCatalogAsync()` | 请求 Runtime 刷新目录 |

初始化连接时通过 `RuntimeClientOptions` 传入显式来源：

```csharp
var options = new RuntimeClientOptions
{
    SkillSources = [
        new SkillSourceDescriptor("custom", SkillSourceKind.Directory, @"C:\skills")
    ]
};
```

`skill.catalog.changed` 事件通知宿主目录已变化，宿主可以据此更新 UI 或日志。

## 9. 脚本执行

`scripts/` 目录下的脚本可以通过 `AgentFileSkillScriptToolExecutor` 执行。脚本执行受以下约束：

- 脚本必须在技能的 `scripts/` 目录内
- 执行受技能授权策略（`SkillScriptAuthorization`）控制
- 执行环境受 `SkillScriptEnvironmentPolicy` 约束
- 脚本工具定义由 `SkillScriptToolDefinition` 生成

## 10. 能力声明

Skills 系统在 `RuntimeCapabilities` 中声明：

```json
{
  "skills": {
    "enabled": true,
    "sourceKinds": ["Directory"],
    "resourcesSupported": true,
    "scriptsSupported": true,
    "hotRefreshSupported": true
  }
}
```

当前版本只支持 `Directory` 来源。`Mcp` 来源在 `SkillSourceKind` 枚举中已定义但尚未实现，使用时会产生 `SkillSourceKindNotSupported` 错误。

## 11. 诊断

刷新产生的诊断通过 `SkillCatalogSummary.Diagnostics` 返回：

| Code | 严重性 | 说明 |
| --- | --- | --- |
| `SkillSourceDuplicate` | warning | 来源目录重复，已跳过 |
| `SkillShadowed` | warning | 技能被其他来源遮蔽 |
| `SkillDuplicateInSource` | error | 同一来源内技能名重复 |
| `SkillDefinitionInvalid` | error | SKILL.md 格式或内容无效 |
| `SkillResourceBlocked` | warning / error | 资源文件被阻止。视场景而定：资源扩展名不支持时以 warning 发出；文件/总量超限、路径逃逸、符号链接或目录联接等场景为 error |
| `SkillScriptUnsupported` | warning | 脚本扩展名不支持 |
| `SkillSourceUnavailable` | error | 来源不可用。触发场景：源目录无法枚举、来源 ID 为空或重复、来源与物化目录重叠、来源为符号链接、有效技能总数超过 512 上限 |
| `SkillSourceKindNotSupported` | error | 来源类型不支持 |
| `SkillSourceNotFound` | error | 显式来源目录不存在 |
