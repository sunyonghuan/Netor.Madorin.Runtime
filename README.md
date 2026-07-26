# Madorin.AI.Runtime

Madorin.AI.Runtime 是基于 .NET 10 的独立 AI Runtime、命令行工具和 .NET Client SDK。
它不依赖现有 Madorin 内部程序集，可独立安装、运行和升级，并通过版本化协议为 CLI、桌面程序、
服务程序及其他宿主提供 AI 执行能力。

当前解决方案包含 36 个项目：29 个源码项目、1 个参考宿主和 6 个测试项目，已经覆盖从
Provider 调用、三种工作模式、工具权限到会话持久化、Runtime IPC 和宿主集成的 V1 主闭环。

## 核心能力

- **CLI 与常驻 Runtime**：支持交互式 REPL、单次任务、持久服务、诊断、配置和在线控制。
- **三种工作模式**：Expert、Meeting 和 Work，统一使用 Session、Run、Selection 和事件协议。
- **Provider 适配**：OpenAI、Anthropic、OpenAI Compatible、DeepSeek、Kimi、Aliyun / DashScope、
  Gemini、GLM、Ollama，以及实验性的 Azure OpenAI。
- **可恢复执行**：支持流式事件、取消、超时、事件确认与重放、断线重连、Session 恢复和检查点。
- **工具系统**：提供受权限约束的内置文件、内容、进程、HTTP 和记忆工具，并支持宿主 / MCP
  工具通过反向 RPC 执行。
- **本地数据层**：SQLite + WAL 保存状态和索引，JSONL 保存权威消息正文，Blob Store 保存大型内容。
- **公开 Client SDK**：封装 Runtime 启动、连接、认证、事件消费、回调、恢复和生命周期管理。
- **工程质量**：启用可空引用、严格分析、警告即错误、确定性构建、中央包管理和 Native AOT。

## 快速开始

### 环境要求

- .NET 10 SDK；仓库的 [`global.json`](./global.json) 当前固定为 `10.0.301`。
- Windows 是当前发布和端到端验证的主要平台，Native AOT 发布目标为 `win-x64`。

### 构建并初始化

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
  --input "用三句话说明这个项目的职责"
```

`run` 还支持 `--session` 续接会话、`--input-file`、`--output-file`、`--no-stream`、
`--timeout`，以及 `text`、`json`、`jsonl` 三种输出格式。完整参数以命令帮助为准：

```powershell
dotnet run --project .\src\Madorin.AI.Runtime.Cli -- run --help
```

## CLI 命令

| 命令 | 用途 |
| --- | --- |
| `serve` | 启动持久 Runtime Server，配置实例、Pipe 前缀和最大并发 Run 数 |
| `run` | 执行一次任务或进入交互模式 |
| `version` | 查看程序、协议、运行时和平台版本 |
| `doctor` | 检查配置、Provider 访问、数据库和运行状态，可修复已识别问题 |
| `config` | 初始化、编辑、查看和验证独立配置 |
| `agent` | 列出、创建、编辑和删除 Agent 定义 |
| `memory` | 管理全局与项目记忆文件 |
| `session` | 列出、查看、导出、删除、压缩、归档和恢复 Session |
| `db` | 检查、修复、重建、压缩、备份和迁移 Runtime 数据库 |
| `storage` | 检查消息 / Blob 一致性并回收未引用 Blob |
| `ctl` | 查询运行实例、活动 Session / Run，取消 Run 和更新在线凭据 |

所有命令共享以下全局选项：

```text
--workspace <path>   指定业务工作区
--data-dir <path>    指定 Runtime 数据目录
--log-level <level>  Debug、Information、Warning 或 Error
--no-color           禁用彩色输出
--json               输出统一的机器可读 JSON 信封
```

使用 `--help` 查看完整命令树：

```powershell
dotnet run --project .\src\Madorin.AI.Runtime.Cli -- --help
```

## 配置与数据

独立 CLI 默认在用户目录保存配置：

```text
~/.madorin/
├── config.json       Provider、默认模型和默认 Agent 配置
└── agents/           独立 Agent 定义
```

独立 CLI 还会读取固定的 `<workspace>/.madorin/config.json` 作为可选的工作区选择配置。它只接受 `schemaVersion` 以及 `selection.provider/model/agent`，不保存 Provider 地址、API Key 或 Agent 正文，也不会向父目录扫描。最终选择优先级为：命令行覆盖 > Agent 绑定 > 工作区选择 > 全局默认；Agent 本身为命令行 > 工作区 > 全局。

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

使用 `config show --workspace <path>`、`config validate --workspace <path>` 或 `doctor --workspace <path>` 查看最终值及来源。不传 `--workspace` 的 `config show/validate` 保持只检查全局配置。

Runtime 默认将工作区数据保存到 `<workspace>/.madorin`；可以使用 `--data-dir` 覆盖：

```text
<workspace>/.madorin/
├── state.db          SQLite + WAL 状态、元数据和索引
├── messages/         Session 的 JSONL 消息正文及上下文投影缓存
├── blobs/            按内容哈希保存的大型内容
├── checkpoints/      可恢复检查点
├── staging/          未完成传输的暂存区
├── locks/            工作区写锁和实例租约
└── logs/             Runtime 日志
```

`.madorin` 是 Runtime 保留目录。宿主应通过 CLI 或 Client SDK 查询和维护其中的数据，
不应直接修改数据库、消息文件或 Blob。唯一的团队配置例外是上述工作区 `config.json`；如需提交它，可在仓库忽略规则中使用 `.madorin/*` 和 `!.madorin/config.json`，根 `.gitignore` 不会自动替团队修改。

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
docs/                    设计、命令规范、执行记录和使用指南备档
build/                   发布脚本
publish/                 Native AOT 运行时指令
```

## 架构边界

```text
Host / CLI
    │
    ├── Client SDK ── Contracts ── Transport.Abstractions
    │                              └── Transport.NamedPipes
    │
    └── Runtime Server（组合根）
         ├── Modes / Orchestration
         ├── Provider Adapters
         ├── Tools
         ├── Services
         └── Persistence
```

- `Entities` 和 `Contracts` 定义稳定的数据模型与公开协议，不依赖具体 Provider、存储或 UI。
- `Providers.Abstractions`、`Tools.Abstractions`、`Persistence.Abstractions` 和
  `Transport.Abstractions` 隔离扩展实现。
- `Server` 是 Runtime 具体实现的组合根；`Client` 只通过公开协议与传输抽象访问 Runtime。
- `SampleHost` 只引用公开 Client SDK 和 Contracts，不引用 Server、Provider 或 SQLite 实现。
- `Contracts` 和 `Core` 不得引用现有 Madorin 内部程序集，也不得直接绑定具体 Provider SDK、
  数据库实现或 UI 框架。

## 文档导航

- [总体方案与总需求](./docs/README.md)
- [实施方案 V1](./docs/方案设计/01-实施方案-V1.md)
- [架构修订 V1](./docs/方案设计/02-架构修订-V1.md)
- [实现框架参考](./docs/方案设计/03-实现框架参考.md)
- [Skills 加载与调用方案](./docs/方案设计/05-Skills加载与调用方案.md)
- [CLI 命令规范](./docs/命令规范/04-CLI命令规范.md)
- [CLI 命令矩阵](./docs/命令规范/09-CLI命令矩阵.md)
- [执行步骤与当前状态](./docs/执行步骤/README.md)
- [参考宿主说明](./samples/Madorin.AI.Runtime.SampleHost/README.md)
- [已完成使用指南与历史资料](./docs/备档/README.md)

设计文档存在冲突时，以架构修订和后续已批准的协议变更为准；实现状态以源码、测试和
执行步骤记录为准。

## 构建、测试与发布

```powershell
# 构建完整解决方案
dotnet build .\Madorin.AI.Runtime.slnx -c Debug

# 运行全部测试
dotnet test .\Madorin.AI.Runtime.slnx -c Debug --no-build

# 验证 CLI 命令树
dotnet run --project .\src\Madorin.AI.Runtime.Cli --no-build -- --help

# 发布 win-x64 Native AOT
dotnet publish .\src\Madorin.AI.Runtime.Cli `
  -c Release `
  -r win-x64 `
  --self-contained true
```

Release 可执行项目默认启用 Native AOT。发布必须保持零 IL 裁剪 / AOT 警告；协议 JSON
继续使用 `RuntimeJsonContext` 等源生成上下文，不得回退到反射序列化。
