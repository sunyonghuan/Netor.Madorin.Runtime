# 独立 CLI 使用指南

> 详细介绍 `madorin` 命令行工具的独立使用方式

本文档面向直接使用 `madorin` CLI 的用户，涵盖配置、命令、工作区管理和高级特性。

## 概述

独立 CLI 模式允许你在命令行直接使用 Runtime，无需编写代码或启动额外服务。适用场景：

- 快速原型验证和实验
- 命令行自动化脚本
- 本地开发和调试
- CI/CD 流程集成

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

# 列出所有会话
madorin session list

# 查看会话详情
madorin session get <session-id>
```

### 输出格式

```bash
# 文本格式（默认，人类可读）
madorin run --input "你好" --format text

# JSON 格式（单个 JSON 对象，包含完整结果）
madorin run --input "你好" --format json

# JSONL 格式（流式事件，每行一个 JSON）
madorin run --input "你好" --format jsonl
```

## 三种运行模式

### Expert 模式（默认）

深度分析和复杂决策，适合技术问题和深入研究。

```bash
madorin run --mode expert --input "分析这个系统的性能瓶颈"
```

### Meeting 模式

多 Agent 主持会议：主持人（Host）调度发言、工具收口。独立 CLI 默认 `HostDriven` + `FinalOnly`。  
独立 CLI 当前**未**挂 Meeting HITL 控制台处理器，因此默认 `RequireEndConfirmation=false`；宿主/SDK 可显式打开结束确认并处理 `meeting.hitl.request`。

```bash
# 单轮（非交互）
madorin run --mode meeting --input "讨论跨境推广预算与退款风险"

# 交互 REPL（推荐联调）
madorin run --mode meeting
```

#### Meeting 交互命令与暂停续跑

| 操作 | 说明 |
| --- | --- |
| 普通输入 | 新开会或在当前 Session 上续一轮 |
| `Ctrl+C`（运行中一次） | **协作式暂停**：下一安全点 `Meeting=Paused`，当前 Run=`Interrupted` |
| `Ctrl+C`（暂停请求后再按） | 硬取消当前 Run |
| `/pause` | 对当前 Meeting Session 请求协作式暂停 / 查看是否已 Paused |
| `/interrupt <文本>` | 入队老板插话，下一安全点或续跑 Host 轮吸收 |
| `/resume <sessionId>` | 切回会话；若 Meeting 已 Paused，校验 `ReadyForContinuation` |
| 暂停后下一条输入 | 自动 `run.existing_session` Continuation Run（Host 恢复轮优先） |

```text
开会讨论…
  → 运行中 Ctrl+C
  → 提示「会议已在安全点暂停」
  → /interrupt 预算上限改为 50 元
  → 输入「请主持人吸收变更后继续」
  → Continuation Run：Host 先处理插话，再调度参会者
  → 结束确认 HITL 后归档
```

配置要求：全局 `~/.madorin/config.json` 至少 2 个可用 Agent（参会者）；可选 `meeting-host` / `summarizer` 角色 id 以拆分 Host 与参会者。

### Work 模式

日常任务执行和流程编排，适合自动化工作流。

```bash
madorin run --mode work --input "检查代码风格并生成报告"
```

## 会话管理

### 列出会话

```bash
# 列出所有会话
madorin session list

# 限制数量
madorin session list --limit 10

# 按时间过滤
madorin session list --since 2026-09-01
```

### 查看会话

```bash
# 查看会话元数据
madorin session get <session-id>

# 列出会话消息
madorin session messages <session-id>

# 读取完整历史
madorin session history <session-id>
```

### 导出会话

```bash
# 导出为 JSONL
madorin session export <session-id> --output conversation.jsonl

# 导出为 JSON（单个对象）
madorin session export <session-id> --format json --output conversation.json
```

### 恢复会话

```bash
# 从检查点恢复
madorin session resume <session-id>
```

### 删除会话

```bash
# 删除单个会话
madorin session delete <session-id>

# 删除多个会话
madorin session delete <session-id-1> <session-id-2>

# 删除所有会话（危险操作，会提示确认）
madorin session delete --all
```

### 压缩和归档

```bash
# 压缩会话（减少存储空间）
madorin session compact <session-id>

# 归档会话（移动到归档目录）
madorin session archive <session-id>
```

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
madorin agent create --name code-reviewer \
  --instruction "你是一个代码审查专家，关注性能和安全"
```

### 编辑 Agent

```bash
madorin agent edit <agent-name>
```

### 删除 Agent

```bash
madorin agent delete <agent-name>
```

### 使用 Agent

```bash
# 在 run 时指定
madorin run --agent code-reviewer --input "审查这段代码"

# 或设为默认（修改 config.json 的 selection.agent）
madorin config edit
```

## 记忆管理

Runtime 支持全局和项目级记忆文件。

### 列出记忆

```bash
# 全局记忆
madorin memory list --scope global

# 项目记忆
madorin memory list --scope project
```

### 创建记忆

```bash
# 全局记忆
madorin memory create --scope global --name "user-preference" \
  --content "用户偏好使用 Python 和 TypeScript"

# 项目记忆
madorin memory create --scope project --name "project-context" \
  --content "这是一个 .NET 10 项目，使用 C# 和 Native AOT"
```

### 编辑记忆

```bash
madorin memory edit --scope global --name "user-preference"
```

### 删除记忆

```bash
madorin memory delete --scope global --name "user-preference"
```

## 数据管理

### 检查数据库

```bash
# 检查完整性
madorin db check

# 检查并自动修复
madorin db check --repair
```

### 数据库维护

```bash
# 重建索引
madorin db rebuild

# 压缩数据库
madorin db compact

# 备份数据库
madorin db backup --output backup-2026-09-18.db
```

### 存储管理

```bash
# 检查 Blob 存储
madorin storage check

# 回收未引用的 Blob
madorin storage gc
```

## 常驻服务

对于需要长期运行或多客户端连接的场景，可以启动常驻 Runtime Server。

### 启动服务

```bash
# 启动默认实例
madorin serve

# 指定实例名称
madorin serve --instance main

# 指定数据目录
madorin serve --data-dir /path/to/data

# 限制并发数
madorin serve --max-concurrent 5
```

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
```

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
| `2` | 配置错误 |
| `3` | 网络错误 |
| `4` | 认证失败 |
| `5` | 数据库错误 |
| `6` | Provider 不支持 |
| `7` | 会话不存在 |
| `8` | 工作区锁定失败 |

详见 [自动化与故障排查](../开发文档/06-自动化与故障排查.md)。

## 全局选项

所有命令支持的全局选项：

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
| `madorin config` | 管理配置 |
| `madorin agent` | 管理 Agent |
| `madorin memory` | 管理记忆 |
| `madorin session` | 管理会话 |
| `madorin db` | 数据库维护 |
| `madorin storage` | 存储管理 |

完整命令参考见 [CLI 命令矩阵](../命令规范/09-CLI命令矩阵.md)。

## 高级用法

### 非交互模式

适用于脚本和 CI/CD：

```bash
# 禁用所有交互式提示
madorin run --input "任务" --no-interactive

# 使用 JSON 输出便于解析
madorin run --input "任务" --json
```

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
madorin ctl cancel --run-id <run-id>
```

## 最佳实践

1. **配置管理**：在项目根目录创建 `.madorin/config.json`，便于团队共享配置
2. **版本控制**：`.madorin/config.json` 可以提交，但其他数据目录应加入 `.gitignore`
3. **安全**：不要在配置文件中硬编码 API Key，使用环境变量或密钥管理工具
4. **定期维护**：定期运行 `madorin db compact` 和 `madorin storage gc` 清理空间
5. **备份**：重要会话使用 `madorin session export` 备份

## 下一步

- **集成到应用**：阅读 [SDK 集成指南](./03-SDK集成指南.md)
- **了解协议**：阅读 [协议规范](./04-协议规范/README.md)
- **完整命令参考**：查看 [CLI 命令矩阵](../命令规范/09-CLI命令矩阵.md)
- **深入概念**：阅读 [概念与导航](../开发文档/01-概念与导航.md)
