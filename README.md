# Madorin.AI.Runtime

Madorin.AI.Runtime 是一个面向 .NET 10 的 AI 运行时平台、命令行工作台和 .NET Client SDK。它不是只会发请求的聊天壳，而是一套把模型接入、任务执行、工具调用、会话恢复、状态持久化和发布交付全部串起来的完整工程底座。

它的目标很直接：让 AI 能力真正进入 CLI、桌面程序、服务端和宿主流程里，并且可以长期运行、可恢复、可扩展、可交付。

与常见的脚本式 Agent、单一 Provider 封装或只提供 API 的轻量 SDK 相比，它更强调三件事：

- 运行时能力完整，不只是模型调用
- 执行链路可恢复，不只是一次性对话
- 本地状态可治理，不只是临时缓存

## 核心优势

- **一体化 Runtime**：CLI、常驻 Server、Client SDK、Provider 适配器、工具系统和持久化层统一设计。
- **多 Provider 接入**：OpenAI、Anthropic、OpenAI Compatible、DeepSeek、Kimi、阿里云 / DashScope、Gemini、GLM、Ollama，以及 Azure OpenAI。
- **三种工作模式**：Expert、Meeting、Work，统一使用 Session、Run、Selection 与事件协议。
- **可恢复执行**：支持流式事件、取消、超时、确认、重放、断线重连、Session 恢复和检查点。
- **本地数据层**：SQLite + WAL、JSONL、Blob Store、Checkpoints、Locks、Logs，全都围绕可控和可追踪设计。
- **开放 Client SDK**：支持启动、连接、认证、事件消费、回调、恢复和生命周期管理。
- **工程质量优先**：可空引用、严格分析、警告即错误、确定性构建、集中包管理、Native AOT。

## 核心功能

### 1. CLI 与常驻 Runtime

Madorin.AI.Runtime 同时提供交互式 REPL、单次任务执行、持久化服务和在线控制能力。它既能快速跑通一个命令，也能作为长期运行的 AI 执行宿主。

### 2. 三种工作模式

- `Expert`：更偏深度分析和复杂决策
- `Meeting`：更适合会议纪要、协作和信息整理
- `Work`：更贴近日常任务执行和流程编排

### 3. Provider 适配层

统一抽象不同模型供应商的差异，让上层只关注“怎么做”，而不是“每家接口怎么拼”。

### 4. 断点恢复与事件协议

支持流式事件、取消、超时、确认、重放、断线重连、Session 恢复和检查点。
这意味着任务不是跑完就散，而是可以接着跑、回头看、继续跑。

### 5. 工具系统

内置受权限约束的文件、内容、进程、HTTP 和记忆工具，并支持宿主 / MCP 的反向 RPC 执行。
这让 Runtime 不只是“会说话”，而是能真正动手处理工作。

### 6. 本地数据层

状态、消息、二进制内容和恢复点都落在本地，结构清晰，适合长期运行、排障和运维。

### 7. Client SDK

对外封装 Runtime 的启动、连接、认证、事件消费、回调、恢复和生命周期管理，方便把同一套能力嵌进外部应用。

### 8. 工程化交付

面向可发布、可升级、可诊断的实际项目，而不是一次性 demo。发布目标默认面向 `win-x64` Native AOT，适合做稳定的本地交付物。

## 与常见方案对比

| 方案 | 常见短板 | Madorin.AI.Runtime |
| --- | --- | --- |
| 只做 Prompt 拼接的脚本 | 不能恢复、不能治理状态、不能长期运行 | 完整 Runtime + 持久化 + 检查点 |
| 只提供 Provider SDK | 只能调模型，缺少执行编排 | CLI / Server / Tools / Storage 一体化 |
| 临时 Agent 壳 | 能跑一次，但难以交付和维护 | 可升级、可发布、可诊断 |

## 快速开始

### 环境要求

- .NET 10 SDK，以仓库 `global.json` 为准
- Windows 是当前发布和端到端验证的主要平台
- Native AOT 默认面向 `win-x64`

### 构建与初始化

```powershell
dotnet restore .\Madorin.AI.Runtime.slnx
dotnet build .\Madorin.AI.Runtime.slnx -c Debug

# 交互式创建 ~/.madorin/config.json
dotnet run --project .\src\Madorin.AI.Runtime.Cli -- config init

# 检查配置、Provider 访问和 Runtime 数据
dotnet run --project .\src\Madorin.AI.Runtime.Cli -- doctor
```

### 运行任务

不带命令启动时进入单行 REPL：

```powershell
dotnet run --project .\src\Madorin.AI.Runtime.Cli --
```

执行一次任务：

```powershell
dotnet run --project .\src\Madorin.AI.Runtime.Cli -- run `
  --provider OpenAI `
  --model gpt-5 `
  --mode expert `
  --input "用三句话说明这个项目的价值"
```

`run` 还支持 `--session` 续接会话、`--input-file`、`--output-file`、`--no-stream`、`--timeout`，以及 `text`、`json`、`jsonl` 三种输出格式。完整参数以命令帮助为准：

```powershell
dotnet run --project .\src\Madorin.AI.Runtime.Cli -- run --help
```

## CLI 命令

| 命令 | 用途 |
| --- | --- |
| `serve` | 启动持久化 Runtime Server，配置实例、Pipe 前缀和并发量 |
| `run` | 执行一次任务或进入交互模式 |
| `version` | 查看程序、协议、运行时和平台版本 |
| `doctor` | 检查配置、Provider 访问、数据库和运行状态 |
| `config` | 初始化、编辑、查看和验证独立配置 |
| `agent` | 列出、创建、编辑和删除 Agent 定义 |
| `memory` | 管理全局与项目记忆文件 |
| `session` | 列出、查看、导出、删除、压缩、归档和恢复 Session |
| `db` | 检查、修复、重建、压缩、备份和迁移 Runtime 数据库 |
| `storage` | 检查消息 / Blob 一致性并回收未引用 Blob |
| `ctl` | 查询运行实例、活动 Session / Run，获取和更新在线凭据 |

所有命令共享这些全局选项：

```text
--workspace <path>   指定任务工作区
--data-dir <path>    指定 Runtime 数据目录
--log-level <level>  Debug、Information、Warning 或 Error
--no-color           禁用彩色输出
--json               输出统一的机器可读 JSON
```

## 配置与数据

独立 CLI 默认在用户目录保存配置：

```text
~/.madorin/
├── config.json       Provider、默认模型和默认 Agent 配置
└── agents/           独立 Agent 定义
```

它还会读取工作区内的可选配置：

```text
<workspace>/.madorin/config.json
```

优先级规则是：命令行覆盖 > Agent 绑定 > 工作区选择 > 全局默认。Agent 本身也会优先于工作区和全局配置。

示例配置：

```json
{
  "schemaVersion": 1,
  "selection": {
    "provider": "openai",
    "model": "gpt-5",
    "agent": "default"
  }
}
```

Runtime 默认把工作区数据保存到 `<workspace>/.madorin`：

```text
<workspace>/.madorin/
├── state.db          SQLite + WAL 状态、元数据和索引
├── messages/         Session 的 JSONL 消息正文和上下文缓存
├── blobs/            按内容哈希保存的大型内容
├── checkpoints/      可恢复检查点
├── staging/          未完成传输的暂存区
├── locks/            工作区写锁和实例租约
└── logs/             Runtime 日志
```

`.madorin` 是 Runtime 保留目录。宿主应通过 CLI 或 Client SDK 访问和维护其中的数据，不要直接修改数据库、消息文件或 Blob。

团队如果需要提交工作区配置，唯一建议保留的是上面的 `<workspace>/.madorin/config.json`。在仓库里可以用 `.madorin/*` 配合 `!.madorin/config.json` 进行精确保留。

## 项目结构

```text
Madorin.AI.Runtime.slnx
src/
├── Madorin.AI.Runtime.Entities
├── Madorin.AI.Runtime.Contracts
├── Madorin.AI.Runtime.Core
├── Madorin.AI.Runtime.Persistence.{Abstractions,Files,Sqlite}
├── Madorin.AI.Runtime.Tools.{Abstractions,Builtin}
├── Madorin.AI.Runtime.Orchestration.Abstractions
├── Madorin.AI.Runtime.Modes.{Expert,Meeting,Work}
├── Madorin.AI.Runtime.Transport.{Abstractions,NamedPipes}
├── Providers/
│   ├── Madorin.AI.Runtime.Providers.Abstractions
│   └── Madorin.AI.Runtime.Providers.*
├── Madorin.AI.Runtime.Services
├── Madorin.AI.Runtime.Server
├── Madorin.AI.Runtime.Client
└── Madorin.AI.Runtime.Cli
samples/
└── Madorin.AI.Runtime.SampleHost
tests/
├── Madorin.AI.Runtime.Core.Tests
├── Madorin.AI.Runtime.Protocol.Tests
├── Madorin.AI.Runtime.Persistence.Tests
├── Madorin.AI.Runtime.Provider.Tests
├── Madorin.AI.Runtime.Modes.Tests
└── Madorin.AI.Runtime.EndToEnd.Tests
```

## 项目规模统计

统计日期：2026-08-07（当前工作区快照）

| 指标 | 数量 |
| --- | ---: |
| 纳入统计的文本文件 | 957 |
| 文本总行数 | 175,242 |
| 文本字数（非空白字符） | 4,790,924 |
| 中文字符数 | 263,552 |
| 英文/标识符词数 | 457,037 |
| 源码文件 | 655 |
| 源码物理行数 | 142,144 |
| 有效代码行数 | 127,783 |
| C# 源码文件 | 650 |
| C# 物理行数 | 141,679 |
| C# 有效代码行数 | 127,403 |

统计包含当前工作区的源码、脚本、配置和文档文本；排除版本控制、缓存、运行时数据及构建产物目录（如 `.git`、`.madorin`、`bin`、`obj`、`artifacts`、`dist`、`publish` 和 `releases`）。有效代码行不计空行和纯注释行；含实际代码及行尾注释的行计为代码行。

## 架构边界

```text
Host / CLI
    ├── Client SDK ── Contracts ── Transport.Abstractions
    │                           └── Transport.NamedPipes
    └── Runtime Server（组合根） ── Modes / Orchestration
                                   ├── Provider Adapters
                                   ├── Tools
                                   ├── Services
                                   └── Persistence
```

- `Entities` 和 `Contracts` 定义稳定的数据模型与公开协议，不依赖具体 Provider、存储或 UI。
- `Providers.Abstractions`、`Tools.Abstractions`、`Persistence.Abstractions` 和 `Transport.Abstractions` 隔离扩展实现。
- `Server` 是 Runtime 的具体组合根，`Client` 只通过公开协议和传输抽象访问 Runtime。
- `SampleHost` 只引用公开 Client SDK 和 Contracts，不引用 Server、Provider 或 SQLite 实现。
- `Contracts` 和 `Core` 不应直接绑定现有内部程序集、具体 Provider SDK、数据库实现或 UI 框架。

## 文档导航

- [总体方案与总需求](./docs/README.md)
- [执行步骤与当前状态](./docs/执行步骤/README.md)
- [参考宿主说明](./samples/Madorin.AI.Runtime.SampleHost/README.md)
- [历史资料与备档](./docs/备档/README.md)

设计文档与实现发生冲突时，以架构修订、代码、测试和执行记录为准。

## 构建、测试与发布

```powershell
# 构建完整解决方案
dotnet build .\Madorin.AI.Runtime.slnx -c Debug

# 运行全部测试
dotnet test .\Madorin.AI.Runtime.slnx -c Debug --no-build

# 验证 CLI 命令树
dotnet run --project .\src\Madorin.AI.Runtime.Cli --no-build -- --help

# 发布 win-x64 Native AOT（CLI + 10 个 Provider Worker，同 bin）
.\build\madorin-cli.publish.cmd -Rid win-x64
# 或：pwsh -File .\build\madorin-cli.publish.ps1 -Rid win-x64
```

Linux / macOS（在对应 OS 上执行；有 `pwsh` 时委托同一套 `.ps1`，否则走脚本内原生 bash 发布）：

```bash
./build/madorin-cli.publish.linux.sh --rid linux-x64
./build/madorin-cli.publish.macos.sh --rid osx-arm64
```

产物在 `releases/Madorin-Cli/madorin-cli/bin/`：`madorin-cli`、10 个 `madorin-provider-*`、`providers.manifest.json`。Worker 清单见 `build/provider-workers.json`。方案见 [12-Provider 进程隔离 AOT 按需加载](./docs/方案设计/12-Provider进程隔离AOT按需加载方案.md)。

Release 版本默认启用 Native AOT。发布时需要保持零 IL 裁剪 / AOT 警告，并继续使用源生成上下文来保证协议 JSON 的稳定性。
