# 独立 CLI 使用指南

> 详细介绍 `madorin` 命令行工具的独立使用方式

本文档面向直接使用 `madorin` CLI 的用户，涵盖配置、命令、工作区管理和高级特性。

## 概述

独立 CLI 模式允许你在命令行直接使用 Runtime，无需编写代码或启动额外服务。适用场景：

- 快速原型验证和实验
- 命令行自动化脚本
- 本地开发和调试
- CI/CD 流程集成

独立 CLI 不传对话语言。Runtime 使用当前操作系统的界面语言，并在 Expert 模式的系统提示词末尾写入操作系统、语言、本地日期、CLI 名称和版本、工作区路径。宿主若已保存语言设置，应在 `run.new_session` / `run.existing_session` 的 `language` 字段传入；不传或 `null` 时同样回退到操作系统界面语言。

## 安装与环境

### 获取可执行文件

从发布包获取对应平台的二进制文件：

```bash
# Windows
madorin-cli.exe

# Linux / macOS
madorin-cli
```

### 添加到 PATH（推荐）

**Windows:**
```powershell
$env:PATH += ";C:\path\to\madorin\bin"
# 或永久添加到系统环境变量
```

**Linux / macOS:**
```bash
export PATH="$PATH:/path/to/madorin/bin"
# 写入 ~/.bashrc 或 ~/.zshrc 永久生效
```

### 验证安装

```bash
madorin version
```

## 配置管理

### 配置文件位置

| 位置 | 优先级 | 说明 |
| --- | --- | --- |
| `<workspace>/.madorin/config.json` | 最高 | 工作区配置，覆盖全局 |
| `~/.madorin/config.json` | 全局 | 用户级全局配置 |
| 命令行参数 | 覆盖 | `--provider`、`--model` 等参数 |

### 初始化配置

```bash
# 交互式配置向导
madorin config init

# 查看当前配置
madorin config show

# 编辑配置文件
madorin config edit

# 验证配置
madorin config validate
```

### 配置文件结构

```json
{
  "schemaVersion": 1,
  "providers": [
    {
      "id": "my-openai",
      "protocol": "OpenAI",
      "apiKey": "sk-...",
      "baseUrl": "https://api.openai.com/v1"
    },
    {
      "id": "my-anthropic",
      "protocol": "Anthropic",
      "apiKey": "sk-ant-...",
      "baseUrl": "https://api.anthropic.com"
    },
    {
      "id": "deepseek",
      "protocol": "DeepSeek",
      "apiKey": "sk-...",
      "baseUrl": "https://api.deepseek.com"
    }
  ],
  "selection": {
    "provider": "my-openai",
    "model": "gpt-4o",
    "agent": "default"
  },
  "proxy": {
    "enabled": true,
    "url": "http://proxy.company.com:8080"
  }
}
```

### 支持的 Provider

| Provider | Protocol 值 | 说明 |
| --- | --- | --- |
| OpenAI | `OpenAI` | OpenAI 官方 API |
| Anthropic | `Anthropic` | Claude 系列 |
| DeepSeek | `DeepSeek` | DeepSeek API |
| Kimi | `Kimi` | Moonshot Kimi |
| 阿里云百炼 | `Aliyun` | DashScope API |
| Gemini | `Gemini` | Google Gemini |
| GLM | `GLM` | 智谱 GLM |
| Ollama | `Ollama` | 本地 Ollama 服务 |
| Azure OpenAI | `Azure OpenAI` | Azure OpenAI Service（实验性） |
| 自定义兼容 | `OpenAI Compatible` | 兼容 OpenAI 协议的服务 |

## 运行任务

### 交互式 REPL

不带任何命令启动进入单行 REPL：

```bash
madorin
```

输入你的问题，然后按 `Ctrl+D`（Linux/macOS）或 `Ctrl+Z` + `Enter`（Windows）发送。

### 单次执行

```bash
# 使用默认配置
madorin run --input "分析这段代码"

# 指定 Provider 和模型
madorin run --provider my-anthropic --model claude-4-opus --input "你好"

# 指定运行模式
madorin run --mode expert --input "深度分析系统架构"

# 从文件读取输入
madorin run --input-file task.txt

# 输出到文件
madorin run --input "总结内容" --output-file result.txt

# 非流式输出（等待完整响应）
madorin run --input "计算结果" --no-stream

# 设置超时
madorin run --input "长时间任务" --timeout 300
```

### 续接会话

```bash
# 继续已有会话
madorin run --session <session-id> --input "继续刚才的话题"

# 列出最近 20 条活动会话（默认状态 active）
madorin session list

# 指定数量，并过滤归档状态、更新时间或标题
madorin session list --limit 10
madorin session list --status all --since 2026-09-01T00:00:00+08:00
madorin session list --search "订单分析"

# 默认最多返回 200 条；查询归档会话需指定状态
madorin session list --status archived --limit 200

# 查看会话详情与消息摘要
madorin session show <session-id> --messages

# 在交互 REPL 内列出最近 10 个会话，按时间从新到旧
/resume

# 指定列出数量（范围 1 到 100）
/resume 10

# 恢复所选会话，在 REPL 内继续对话
/resume <session-id>
```

`/resume` 列表时间使用本地时间并精确到秒。列表中的完整 Session ID 可直接传给 `/resume <session-id>`，也可在退出程序后使用 `madorin run --session <session-id>` 恢复。

### 输出格式

```bash
# 文本格式（默认，人类可读）
madorin run --input "你好" --output-format text

# JSON 格式（单个 JSON 对象，包含完整结果）
madorin run --input "你好" --output-format json

# JSONL 格式（流式事件，每行一个 JSON）
madorin run --input "你好" --output-format jsonl
```

`--json` 是全局机器可读输出选项；`--output-format` 用于选择 `text`、`json` 或 `jsonl` 运行结果格式。`--input` 与 `--input-file` 不能同时使用。

## 三种运行模式

### Expert 模式（当前运行模式）

深度分析和复杂决策，适合技术问题和深入研究。支持子 Agent 委派、工具调用和精细化请求参数控制。

```bash
# 基础对话
madorin run --input "分析这个系统的性能瓶颈"

# 带请求参数控制（温度、最大Token等）
madorin run --input "生成创意方案" --temperature 1.5 --max-tokens 4000

# 指定模型和 Provider
madorin run --provider my-openai --model gpt-4o --input "分析代码"
```

#### ExpertRequestControl 参数

Expert 模式支持以下请求控制参数，用于精细化调整模型行为：

| 参数 | 说明 | 有效范围 |
| --- | --- | --- |
| `--temperature` | 采样温度，控制创造性 | 0.0-2.0（默认1.0） |
| `--max-tokens` | 最大输出 Token 数 | 1-模型上限 |
| `--top-p` | 核采样阈值 | 0.0-1.0（默认1.0） |
| `--top-k` | Top-K 采样 | ≥1 |
| `--frequency-penalty` | 频率惩罚，减少重复 | -2.0-2.0（默认0） |
| `--presence-penalty` | 存在惩罚，鼓励新话题 | -2.0-2.0（默认0） |

这些参数会覆盖 Provider/Agent 默认值，仅对当前请求生效。

## 会话管理

### 列出会话

```bash
# 列出所有会话
madorin session list

# 指定数量（默认 20，最多 200）
madorin session list --limit 10

# 按状态过滤：active（默认）、archived 或 all
madorin session list --status archived
madorin session list --status all

# 按 Expert 模式、更新时间、标题关键词过滤
madorin session list --mode expert
madorin session list --since 2026-09-01T00:00:00+08:00
madorin session list --search "项目复盘"

# 继续游标分页
madorin session list --limit 20 --cursor <next-cursor>
```

### 查看会话

```bash
# 查看会话详情
madorin session show <session-id>

# 同时包含消息元数据摘要
madorin session show <session-id> --messages
```

### 导出会话

```bash
# 导出为 Markdown（默认）
madorin session export <session-id> --output conversation.md

# 导出为纯文本或 JSONL
madorin session export <session-id> --format txt --output conversation.txt
madorin session export <session-id> --format jsonl --output conversation.jsonl

# 按需包含思考和工具调用细节
madorin session export <session-id> --include-reasoning --include-tool-calls
```

### 压缩会话上下文

```bash
# 预览摘要压缩方案，不调用模型、不写缓存
madorin session compact <session-id> --dry-run

# 使用默认 summary 策略压缩
madorin session compact <session-id>

# 使用滑动窗口并设置保留 Token 预算
madorin session compact <session-id> --strategy sliding-window --keep-last-tokens 12000

# 强制重新生成当前投影
madorin session compact <session-id> --force
```

压缩生成上下文投影，原始历史仍保留；它与删除会话、恢复检查点是不同操作。

### 删除和归档会话

```bash
# 删除必须显式确认
madorin session delete <session-id> --confirm

# 同时清理未被其他会话引用的 Blob
madorin session delete <session-id> --confirm --include-blobs

# 归档和取消归档
madorin session archive <session-id>
madorin session unarchive <session-id>
```

删除命令每次只接受一个 Session ID，不支持批量 ID 或 `--all`。

## 交互式 REPL 命令

运行 `madorin` 或 `madorin run` 且未提供单次输入时进入 REPL。直接输入内容开始一轮对话，以 `/` 开头的输入作为交互命令。默认帮助为中文；使用 `/help en` 切换英文，使用 `/help zh` 切回中文。输入 `/help <命令名>` 查看单个命令，也支持中文别名，例如 `/help 历史`。

| 命令 | 用途 |
| --- | --- |
| `/help [命令]` | 列出交互命令或查看单个命令；语言别名支持 `/help en`、`/help english`、`/help zh`、`/help cn`、`/help chinese`、`/help 中文` |
| `/exit`、`/quit` | 保存当前会话并退出 |
| `/provider [名称]`、`/model [编号]`、`/agent [编号]` | 选择下一轮的 Provider、模型或 Agent |
| `/session` | 查看当前 Session 和下一轮选择 |
| `/new` | 保留旧会话，并在下一次发送内容时新建会话 |
| `/resume [条数 \| 会话编号]` | 列出最近会话或恢复指定会话；默认 10 条，条数范围 1–100 |
| `/history [条数 \| all]` | 查看当前会话最近 5 条消息、指定数量或全部消息 |
| `/export [路径]` | 将当前会话导出为 Markdown；省略路径时输出到屏幕 |
| `/compact`、`/context` | 压缩当前会话上下文，或查看完整历史与模型实际上下文估算 |
| `/cd [目录]` | 查看或切换工作区；目标目录必须存在 |
| `/status`、`/mode` | 查看 Runtime 状态和当前运行模式 |
| `/clear` | 清屏，不删除会话和历史 |
| `/memory [global \| project \| effective]` | 查看全局、项目或合并后的记忆；省略参数等同 `effective` |
| `/remember <global \| project>` | 输入一行内容，追加到指定记忆文件 |
| `/tools`、`/skill <list \| show 名称 \| refresh>` | 查看工具权限或管理本地 Skills |
| `/permission`、`/instructions` | 查看或切换本次 CLI 进程的工具权限和工作区说明加载方式 |

离开已有会话时，REPL 会显示 Session ID 和两种恢复命令：程序内使用 `/resume <session-id>`，退出后使用 `madorin run --session <session-id>`。状态行显示 `working 3s`、`thinking 5s` 或 `tool:<toolname> 8s` 等当前状态和耗时；每轮结束后会显示上下文大小，例如 `1.25k`。

旧的 `/pause` 和 `/interrupt` 命令已移除。正在执行的 Run 按一次 Ctrl+C 会取消当前 Run 并返回 REPL；再次按 Ctrl+C 会退出。`/help` 中列出的命令可以按中文别名搜索，例如 `/help 历史`；帮助语言也支持 `english`、`cn`、`chinese` 和 `中文` 别名。

## 全局选项和 `run` 选项

`--workspace`、`--data-dir`、`--log-level`、`--no-color`、`--json` 是可用的全局选项。`run` 还支持：

| 选项 | 用途 |
| --- | --- |
| `--provider`、`--model`、`--agent`、`--mode` | 选择模型执行配置和 Expert 模式 |
| `--session` | 续接已有 Session |
| `--input`、`--input-file` | 提供任务内容，二者不能同时指定 |
| `--output-file`、`--output-format`、`--no-stream` | 控制结果目的地、格式和流式行为 |
| `--timeout`、`--lock-wait` | 设置任务超时和工作区锁等待时间 |
| `--allow-tools`、`--permission-mode` | 配置工具白名单和权限模式 |
| `--instructions-mode` | 设置工作区 `AGENTS.md` 自动加载方式 |
| `--temperature`、`--top-p`、`--top-k`、`--max-tokens` | 控制模型采样和输出长度 |
| `--frequency-penalty`、`--presence-penalty`、`--seed`、`--stop` | 控制惩罚项、随机种子和停止序列 |
| `--response-format` | 选择文本或 JSON 结构化输出 |

交互权限模式为 `ask-all`、`auto-tools` 和 `auto-all`。`auto-tools` 自动批准工具请求，但工作流审批仍会询问；`auto-all` 自动批准工具和工作流请求。权限模式仅在当前 CLI 进程内有效。

这些是当前源码中 `run` 的参数。Provider 对请求控制字段的支持有差异，详见[请求控制参数详解](./请求控制参数详解.md)。

## Agent 管理

Agent 定义了 AI 的行为、角色和初始指令。

### 列出 Agent

```bash
madorin agent list
```

### 创建 Agent

```bash
# 交互式创建
madorin agent create

# 指定名称和指令
madorin agent create code-reviewer \
  --name "Code Reviewer" \
  --system-prompt "你是一个代码审查专家，关注性能和安全"
```

### 编辑 Agent

```bash
madorin agent edit <agent-name>
```

### 删除 Agent

```bash
madorin agent delete <agent-name>
```

Agent ID 是位置参数；创建和编辑时可使用 `--name`、`--description`、`--system-prompt`、`--provider`、`--model` 和 `--temperature` 配置 Agent。创建 ID 的格式必须是安全文件名，删除默认 Agent 时还需指定 `--replacement`。JSON 模式下交互式 Agent 创建/编辑不可用。

### 使用 Agent

```bash
# 在 run 时指定
madorin run --agent code-reviewer --input "审查这段代码"

# 或设为默认（修改 config.json 的 selection.agent）
madorin config edit
```

## 记忆管理

Runtime 支持全局和项目级记忆文件。

### 初始化和查看记忆

```bash
# 初始化全局记忆文件
madorin memory init global

# 初始化项目记忆文件
madorin memory init project

# 查看全局、项目或合并后的有效记忆
madorin memory show global
madorin memory show project
madorin memory show effective
```

### 追加和编辑记忆

```bash
# 追加一条全局记忆，命令从标准输入读取一行内容
echo "用户偏好使用 Python 和 TypeScript" | madorin memory add global

# 追加一条项目记忆
echo "这是一个 .NET 10 项目，使用 C# 和 Native AOT" | madorin memory add project
```

### 编辑和清空记忆

```bash
# 编辑记忆文件；没有可用编辑器时回退到交互输入
madorin memory edit global
madorin memory edit project

# 清空项目记忆，默认需要确认
madorin memory clear project

# 脚本或 JSON 模式下跳过确认
madorin memory clear project --yes
```

记忆命令使用位置参数 `global` 或 `project`；`effective` 只用于 `memory show`，表示合并后的有效内容。

## 数据管理

### 检查数据库

```bash
# 检查完整性
madorin db check

# 生成并执行数据库修复
madorin db repair
```

### 数据库维护

```bash
# 压缩数据库
madorin db vacuum

# 备份数据库
madorin db backup --output backup-2026-09-18.db --include-messages

# 预览数据库迁移，或迁移到最新支持版本
madorin db migrate --dry-run
madorin db migrate

# 从权威消息文件重建索引和状态库；先预览，再明确确认
madorin db rebuild --dry-run
madorin db rebuild --confirm
```

`db repair` 用于修复可识别的 JSONL 尾部损坏和消息索引漂移；`db rebuild` 会根据权威消息文件重建现有状态数据库。重建前应先执行 `--dry-run` 检查计划。

### 存储管理

```bash
# 检查 Blob 存储
madorin storage check --verify-hashes

# 预览待回收的未引用 Blob
madorin storage gc --dry-run

# 回收默认至少 7 天未引用的 Blob，需要确认
madorin storage gc --confirm

# 自定义未引用保留天数
madorin storage gc --older-than 30 --confirm
```

## 常驻服务

对于需要长期运行或多客户端连接的场景，可以启动常驻 Runtime Server。

### 启动服务

```bash
# 生成本地示例密钥；生产环境应从密钥管理服务注入
export MADORIN_HANDSHAKE_SECRET="$(openssl rand -base64 32)"

# 启动服务：workspace 必填，密钥通过 stdin 第一行传入
printf '%s\n' "$MADORIN_HANDSHAKE_SECRET" | madorin serve --workspace /path/to/project --instance main
```

服务还支持 `--data-dir`、`--config-dir`、`--log-dir`、`--pipe-prefix`、`--max-runs`、`--http-port` 和 `--log-level`。这些是同一条 `serve` 命令的可选参数，例如：

```bash
printf '%s\n' "$MADORIN_HANDSHAKE_SECRET" | madorin serve \
  --workspace /path/to/project \
  --data-dir /path/to/data \
  --instance main \
  --max-runs 5 \
  --log-dir /path/to/logs \
  --http-port 5080
```

服务会从 stdin 第一行读取握手密钥；连接该实例的客户端必须使用相同密钥。不要把实际密钥写入命令参数、脚本、历史记录或日志。

### 控制服务

```bash
# 查询运行状态
madorin ctl status --instance main

# 列出活动会话
madorin ctl sessions --instance main

# 列出运行中的 Run
madorin ctl runs --instance main

# 停止服务（优雅关闭）
madorin ctl shutdown --instance main

# 取消指定的活动 Run
madorin ctl cancel <run-id> --reason "用户取消"

# 为等待凭据的 Run 更新凭据；API Key 从环境变量读取
madorin ctl credential update --run <run-id> --provider <provider-id> --api-key-env MADORIN_API_KEY

# 可选指定 Provider profile 和带时区的过期时间
madorin ctl credential update --run <run-id> --provider <provider-id> --profile production --api-key-env MADORIN_API_KEY --expires-at 2026-12-31T23:59:59+08:00

# 只查看指定 Session 的活动 Run
madorin ctl runs --session <session-id> --instance main
```

`ctl credential update` 只适用于等待凭据的 Run。不要把 API Key 直接写进命令行参数；使用 `--api-key-env` 指向已设置的环境变量。

## 工作区与数据隔离

### 工作区概念

每个目录可以独立维护：

```
my-project/
├── .madorin/
│   ├── config.json         # 工作区配置（可选）
│   ├── state.db            # SQLite 数据库
│   ├── messages/           # JSONL 消息
│   ├── blobs/              # 大型内容
│   ├── checkpoints/        # 恢复点
│   └── logs/               # 日志
├── src/
└── README.md
```

### 指定工作区

```bash
# 使用当前目录
cd /path/to/project
madorin run --input "分析这个项目"

# 显式指定工作区
madorin run --workspace /path/to/project --input "分析项目"
```

### 数据目录

```bash
# 默认使用 <workspace>/.madorin
madorin run --input "任务"

# 显式指定数据目录
madorin run --data-dir /custom/data --input "任务"
```

## 诊断与故障排查

### 环境检查

```bash
madorin doctor
```

输出示例：

```
✓ 配置文件: C:\Users\xxx\.madorin\config.json
✓ Provider: my-openai (OpenAI)
✓ 默认模型: gpt-4o
✓ 数据目录: .\.madorin (3 sessions, 12 messages)
✓ 数据库: state.db (正常)
✓ 网络连接: 可访问 https://api.openai.com
✗ Provider 访问: 认证失败（检查 API Key）
```

### 日志级别

```bash
# 详细日志
madorin run --log-level Debug --input "任务"

# 仅错误
madorin run --log-level Error --input "任务"
```

### 退出码

| 退出码 | 说明 |
| --- | --- |
| `0` | 成功 |
| `1` | 通用错误 |
| `2` | 参数或配置错误 |
| `3` | 认证失败 |
| `4` | 工作区或数据错误 |
| `5` | Runtime 连接失败 |
| `130` | 用户中断，例如 Ctrl+C |

## 全局选项

常用全局选项如下；以具体命令的 `--help` 输出为准：

```bash
--workspace <path>   # 指定工作区目录
--data-dir <path>    # 指定数据目录
--log-level <level>  # Debug | Information | Warning | Error
--no-color           # 禁用彩色输出
--json               # 输出 JSON 格式
```

## 命令速查表

| 命令 | 说明 |
| --- | --- |
| `madorin` | 启动交互式 REPL |
| `madorin run` | 执行单次任务 |
| `madorin serve` | 启动常驻服务 |
| `madorin ctl` | 控制运行实例 |
| `madorin version` | 查看版本信息 |
| `madorin doctor` | 检查环境状态 |
| `madorin provider protocols` | 列出可用 Provider 协议 |
| `madorin config` | 管理配置 |
| `madorin agent` | 管理 Agent |
| `madorin skill` | 查看、校验和刷新 Skills |
| `madorin memory` | 管理记忆 |
| `madorin session` | 管理会话 |
| `madorin db` | 数据库维护 |
| `madorin storage` | 存储管理 |

完整命令参考见 [CLI 命令矩阵](../命令规范/09-CLI命令矩阵.md)。

本指南提供常用操作和 REPL 命令说明；完整参数、确认要求和命令行为以命令矩阵及具体命令的 `--help` 为准。

## 高级用法

### 非交互模式

适用于脚本和 CI/CD：

```bash
# 使用 JSON 输出便于解析
madorin run --input "任务" --json
```

`run` 当前没有 `--no-interactive` 选项；脚本应使用 `--input` 或 `--input-file` 提供输入，并结合 `--json`、`--output-format json/jsonl` 或 `--output-file` 获取机器可读结果。

### 批量处理

```bash
# 从文件列表批量处理
cat tasks.txt | while read task; do
  madorin run --input "$task" --output-file "result-$task.txt"
done
```

### 超时和取消

```bash
# 设置超时（秒）
madorin run --input "任务" --timeout 60

# 在另一个终端取消运行
madorin ctl cancel <run-id> --reason "操作员取消"
```

当前 CLI 退出码：`0` 表示成功，`1` 通用错误，`2` 参数或配置错误，`3` 认证失败，`4` 工作区或数据错误，`5` Runtime 连接失败，`130` 用户中断。旧版部署脚本如仍按退出码 6–8 分类，应同步迁移。

## 最佳实践

1. **配置管理**：在项目根目录创建 `.madorin/config.json`，便于团队共享配置
2. **版本控制**：`.madorin/config.json` 可以提交，但其他数据目录应加入 `.gitignore`
3. **安全**：不要在配置文件中硬编码 API Key，使用环境变量或密钥管理工具
4. **定期维护**：按需运行 `madorin db vacuum` 和 `madorin storage gc --dry-run` 检查、维护存储
5. **备份**：重要会话使用 `madorin session export` 备份

## 下一步

- **集成到应用**：阅读 [SDK 集成指南](./03-SDK集成指南.md)
- **了解协议**：阅读 [协议规范](./04-协议规范/README.md)
- **完整命令参考**：查看 [CLI 命令矩阵](../命令规范/09-CLI命令矩阵.md)
- **深入概念**：阅读 [概念与导航](../开发文档/01-概念与导航.md)
