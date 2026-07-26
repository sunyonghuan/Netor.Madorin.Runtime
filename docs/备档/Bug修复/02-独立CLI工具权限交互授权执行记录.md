# Bug 修复 02：独立 CLI 工具权限交互授权

> 文档状态：已完成
>
> 对应方案：[独立 CLI 工具权限交互授权修复方案](./02-独立CLI工具权限交互授权修复方案.md)
>
> 本专项不计入 V1 既有 13 阶段进度。

## 1. 修复目标

为独立交互 CLI 接入唯一的本地工具 Consent 权限权威，复用 Runtime 授权、Grant、审计和工具执行链；宿主 Reverse RPC 继续作为宿主模式唯一权限权威。非交互、机器输出和无法解析最小作用域的场景保持 fail closed。

## 2. 起始状态

- 工作树已有 CLI 欢迎语、文本样式、帮助和选择菜单等未提交修改，本专项只做局部补丁，不回退或覆盖。
- 起始 Debug 全量构建：0 警告、0 错误。
- 起始全量测试：960 通过、1 个无关 E2E 用例偶发失败、6 跳过；失败用例单独复跑通过。
- CodeMap baseline：`7460b11700b9a25fa8a3cce1e448ec35af0c0454`。
- 本专项使用 CodeMap workspace `session`；最终 overlay revision 为 `17`。

## 3. 实施批次

### 批次一：Runtime Consent 抽象与失败测试

- [√] 新增统一 `IToolConsentHandler`、富上下文和 Reverse RPC 适配器。
- [√] 保留 `ToolConsentCoordinator` 现有委托构造入口。
- [√] `LocalRuntimeOptions`、新/旧 Session 和恢复入口传递本地 handler。
- [√] 本地 handler 与 `controlPeer` 强制互斥。
- [√] 覆盖本地、Reverse RPC、双 handler 和无 handler 测试。

### 批次二：最小作用域与 Grant

- [√] 新增结构化 `ToolRequestedScope` 和解析结果。
- [√] 内置文件、Memory、Process、PowerShell、HTTP 无副作用解析。
- [√] 路径逃逸、UNC/设备路径、URL userinfo、动态目标和未知工具 fail closed。
- [√] `Allow once` 绑定 Run、Agent、Tool、Call。
- [√] `Allow for run` 只用于固定且可安全表达的作用域。

### 批次三：Consent 状态机与审计闭环

- [√] Coordinator 最多处理一次 permission 和一次 approval。
- [√] 每次 Consent 后重新经过 `ToolAuthorizationService`。
- [√] permission/approval 都持久化 pending 与最终 decision。
- [√] 无 handler、拒绝、错配、非法 Grant、异常和取消均终结 ToolIntent。
- [√] 取消先写入 `Cancelled`，再传播取消。
- [√] SQLite Schema v14 升级到 v15，新增 `request_kind`。

### 批次四：CLI 交互接入

- [√] `ICliTerminal.IsInteractive` 默认 `false`，生产终端严格识别真实控制台。
- [√] Policy 排除 JSON/JSONL、重定向、输出文件和非文本输出。
- [√] permission 只接受 `1/2/3`，approval 只接受 `Y/N`，空输入拒绝。
- [√] 非法输入最多三次，EOF 拒绝，Ctrl+C 传播取消。
- [√] Meeting/Work 并发提示串行且响应关联不串线。
- [√] 提示只输出规范化目标和安全摘要，不泄露正文、脚本、Header 或密钥。
- [√] `--yes` 不参与模型工具授权。

### 批次五：模式、文档与发布门禁

- [√] Expert 覆盖 Memory、文件 destructive 操作、Process、PowerShell、HTTP。
- [√] Meeting 覆盖并发 Agent 提示串行和 CallId/AgentId 隔离。
- [√] Work 覆盖子 Agent、父/根 Grant、委派交集和 Step 终态。
- [√] 更新安全运维、自动化排错和记忆阶段说明。
- [√] Debug 全量测试、Release 构建和 `win-x64` Native AOT 发布通过。
- [√] 真实交互终端允许/拒绝冒烟通过。
- [√] `git diff --check` 通过，CodeMap 调用点复核完成。

## 4. 测试记录

| 门禁 | 结果 | 备注 |
| --- | --- | --- |
| 定向单元/集成测试 | 通过 | 新增 43 项：作用域 12、CLI Policy/Grant/Handler 20、Coordinator 9、子 Agent 直接 Consent 1、双权限权威 1 |
| 模式、持久化与 Provider | 通过 | Modes 92/92、Persistence 172/172；最终 Provider 262 通过、6 跳过，既有 PowerShell 清理竞态本轮直接通过 |
| End-to-End / Reverse RPC | 通过 | End-to-End 327/327；Reverse RPC 权限、审批、热重连与恢复定向 8/8；Schema v15 迁移定向 11/11 |
| Debug 全量测试 | 通过 | 1036 通过、0 失败、6 跳过 |
| Release 构建 | 通过 | 0 警告、0 错误 |
| Native AOT `win-x64` | 通过 | 0 trimming/AOT 警告；发布版 `version --json` 成功 |
| 真实终端冒烟 | 通过 | stdin/stdout 均未重定向；permission/approval 允许与拒绝共 4 条，Grant 结果符合预期 |
| `git diff --check` | 通过 | 无空白错误；仅报告既有 LF→CRLF 提示 |

## 5. 风险与回退点

- 风险：最小作用域解析错误可能造成过宽授权。控制：未知或无法规范化时拒绝，不生成通配 Grant。
- 风险：本地与宿主双权限权威。控制：Run 启动前校验互斥，不做隐式回退。
- 风险：拒绝或取消留下 Pending Intent。控制：统一幂等终结入口，并使用有界独立持久化令牌清理。
- 风险：机器输出被提示污染。控制：只在真实文本终端创建本地 handler。
- 回退点：可整体撤销新增本地 handler 注入而恢复原 fail-closed 行为；Schema v15 仅新增带默认值列，不删除或重建旧库。

## 6. 完成记录

本专项于 2026-07-26 完成：Debug/Release 构建均为 0 警告、0 错误；全量测试 1036 通过、0 失败、6 个真实 Provider 用例按设计跳过；`win-x64` Native AOT、发布版执行和真实控制台 Consent 四分支均通过。CodeMap baseline 为 `7460b11700b9a25fa8a3cce1e448ec35af0c0454`，workspace `session` 最终 overlay revision 为 `17`。

已知限制保持设计约束：不提供永久、跨 Run 或配置自动授权；Memory append、破坏性文件操作、Process、PowerShell、HTTP 和动态/敏感目标只允许当前 Call；非交互独立 CLI 不提示并保持 fail closed。Schema v15 只新增带默认值的 `request_kind` 列，迁移失败不会重建或清空旧库。

方案与执行记录归档在 `docs/备档/Bug修复/`，不计入 V1 既有 13 阶段进度。
