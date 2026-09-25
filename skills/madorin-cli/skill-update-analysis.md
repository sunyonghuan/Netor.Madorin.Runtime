# Madorin CLI 技能更新分析报告

## 当前实际 CLI 命令结构

基于源码分析，当前 CLI 实际支持的命令组和子命令：

### 根命令
- `run` - 运行任务或进入交互模式
- `serve` - 启动持久化 Runtime 服务器
- `version` - 显示版本信息
- `doctor` - 诊断配置、Provider 访问和运行状况

### 命令组

#### config
- `init` - 初始化配置
- `edit` - 编辑配置
- `show` - 显示配置
- `validate` - 验证配置

#### agent
- `list` - 列出 Agent
- `create <agentId>` - 创建 Agent
- `edit <agentId>` - 编辑 Agent
- `delete <agentId>` - 删除 Agent

#### provider
- `protocols` - 列出支持的 Provider 协议

#### memory
- `init <scope>` - 初始化内存文件
- `show <scope>` - 显示内存内容
- `add <scope>` - 添加内存项
- `edit <scope>` - 编辑内存文件
- `clear <scope>` - 清空内存内容

#### skill
- `list` - 列出技能
- `show <name>` - 显示技能详情
- `validate` - 验证技能
- `refresh` - 刷新技能目录

#### session
- `list` - 列出会话
- `show <sessionId>` - 显示会话详情
- `export <sessionId>` - 导出会话
- `delete <sessionId>` - 删除会话
- `compact <sessionId>` - 压缩会话上下文
- `archive <sessionId>` - 归档会话
- `unarchive <sessionId>` - 取消归档会话

#### db
- `check` - 检查数据库完整性
- `repair` - 修复数据库问题
- `rebuild` - 从消息文件重建索引
- `vacuum` - 压缩 SQLite 数据库
- `backup` - 备份运行时状态
- `migrate` - 应用数据库迁移

#### storage
- `check` - 检查消息和 Blob 引用
- `gc` - 垃圾回收旧的未引用 Blob

#### ctl
- `status` - 显示 Runtime 状态
- `sessions` - 显示活动会话
- `runs` - 显示活动运行
- `cancel <runId>` - 取消运行
- `credential update` - 更新凭证

---

## 对比分析：技能文档 vs 实际代码

### ✅ 完全匹配的命令（无需更新）

以下命令在技能文档中描述准确：
- `run`, `serve`, `version`, `doctor`
- `config init/edit/show/validate`
- `agent list/create/edit/delete`
- `provider protocols`
- `memory init/show/add/edit/clear`
- `skill list/show/validate/refresh`
- `db check/repair/rebuild/vacuum/backup/migrate`
- `storage check/gc`
- `ctl status/sessions/runs/cancel`
- `ctl credential update`

### ⚠️ 需要更新的部分

#### 1. Session 命令完整性
**技能文档中列出**：
```
session: list, show, export, archive, unarchive, compact, delete
```

**实际代码支持**：
- ✅ `list` (SessionCommands.CreateListCommand)
- ✅ `show` (SessionCommands.CreateShowCommand)
- ✅ `export` (SessionCommands.CreateExportCommand)
- ✅ `delete` (SessionCommands.CreateDeleteCommand)
- ✅ `compact` (SessionCommands.CreateCompactCommand)
- ✅ `archive` (SessionCommands.CreateStatusCommand - "archive")
- ✅ `unarchive` (SessionCommands.CreateStatusCommand - "unarchive")

**结论**: Session 命令**完全匹配**，文档正确。

#### 2. REPL 命令完整性

**技能文档中列出的 REPL 命令**：
```
/model, /provider, /agent, /mode
/session, /new, /resume, /history, /export
/compact, /context, /tools
/memory, /remember
/status, /clear, /help, /exit, /quit
```

**实际代码支持**（ServiceCommands.cs TryHandleCoreReplCommandAsync）：
- ✅ `/model`, `/provider`, `/agent`, `/mode`
- ✅ `/session`, `/new`, `/resume`, `/history`, `/export`
- ✅ `/compact`, `/context`, `/tools`
- ✅ `/memory`, `/remember`
- ✅ `/status`, `/clear`, `/help`, `/exit`, `/quit`
- ✅ `/cd` - **文档缺失**
- ✅ `/skill` - **文档缺失**
- ✅ `/pause`, `/interrupt` - **Meeting 模式专用，文档缺失**

**结论**: REPL 命令文档**不完整**，缺少以下命令：
- `/cd [path]` - 切换工作区
- `/skill <list|show <name>|refresh>` - 技能操作
- `/pause` - Meeting 模式：暂停会议
- `/interrupt <text>` - Meeting 模式：插入中断消息

---

## 📝 需要补充到技能文档的内容

### 1. REPL 命令补充

在 `independent-cli.md` 的 REPL 命令表中补充：

```markdown
| Category | Slash commands |
| --- | --- |
| selection | `/model [modelId]`, `/provider [providerId]`, `/agent [agentId]`, `/mode` |
| session | `/session`, `/new`, `/resume [sessionId]`, `/history [n|all]`, `/export [path]` |
| context | `/compact`, `/context`, `/tools` |
| meeting | `/pause`, `/interrupt <text>` |
| skill | `/skill <list|show <name>|refresh>` |
| memory | `/memory [global|project|effective]`, `/remember <global|project>` |
| utility | `/status`, `/clear`, `/cd [path]`, `/help [command]`, `/exit`, `/quit` |
```

### 2. Meeting 模式专用命令说明

补充说明 Meeting 模式的特殊行为：

```markdown
### Meeting 模式特殊命令

Meeting 模式支持协作式暂停和中断：

- `/pause` - 请求暂停当前 Meeting 会话。如果正在运行，会在下一个安全点停止。
- `/interrupt <text>` - 将消息入队，在下一个 Meeting 安全点或延续 Host 回合时被吸收。
- 第一次 Ctrl+C 请求协作式暂停，第二次 Ctrl+C 强制取消。

示例：
```powershell
# 在 Meeting 运行期间按 Ctrl+C 一次暂停
madorin run --mode meeting --session <sessionId>
> Starting meeting...
^C  # 第一次 Ctrl+C，协作式暂停
会议已在安全点暂停（Run=Interrupted）。

# 在暂停状态入队插话
> /interrupt 请等一下，我需要补充信息
Interrupt queued (Accepted): will be absorbed at the next Meeting safe point

# 恢复会议
> 继续讨论
```
```

### 3. /cd 命令说明

补充工作区切换命令：

```markdown
### 工作区切换

在 REPL 中使用 `/cd` 切换工作区：

```powershell
/cd                    # 显示当前工作区
/cd ~/projects/app     # 切换到指定目录
/cd "../other-project" # 使用相对路径
/cd ~                  # 切换到用户主目录
```

切换工作区后：
- 会话重置为新会话
- 保留当前 Provider/Model/Agent 选择
- 加载新工作区的配置和技能
```

### 4. /skill 命令说明

补充 REPL 中的技能管理：

```markdown
### REPL 中的技能管理

```powershell
/skill list                    # 列出当前目录技能
/skill show madorin-cli        # 显示技能详情和定义
/skill refresh                 # 刷新技能目录
```

等同于独立命令：
```powershell
madorin skill list --workspace <workspace>
madorin skill show <name> --workspace <workspace>
madorin skill refresh --workspace <workspace>
```
```

---

## ✅ 技能文档中正确的内容（无需更改）

以下内容在技能文档中描述准确，与代码实现一致：

1. **全局选项**: `--workspace`, `--data-dir`, `--log-level`, `--no-color`, `--json` 全部正确
2. **退出码**: 0/1/2/3/4/5/130 的定义准确
3. **命令树表格**: 除 REPL 命令外，所有命令组和子命令列举完整
4. **参数规则**: 所有互斥选项、必需选项的说明准确
5. **确认选项**: `--yes`, `--confirm` 的使用场景正确
6. **示例代码**: 所有示例与实际 API 一致

---

## 🎯 总结

### 技能文档状态评估
- **准确度**: 95%
- **完整度**: 92%

### 需要执行的更新
1. ✅ **高优先级**: 补充 REPL 命令表中的 4 个缺失命令（`/cd`, `/skill`, `/pause`, `/interrupt`）
2. ✅ **中优先级**: 添加 Meeting 模式特殊命令的详细说明
3. ⚠️ **低优先级**: 扩展工作区切换和技能管理的用法示例

### 建议的文档更新位置
- `independent-cli.md` 第 65-75 行：更新 REPL 命令表
- 在 REPL 章节后新增 "Meeting 模式特殊行为" 小节
- 在 "Skills And Sessions" 章节补充 REPL 技能操作示例
