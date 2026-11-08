# Madorin.AI.Runtime

面向 .NET 10 的独立 AI 运行时、命令行工作台与 Client SDK。

可执行、可恢复、可集成，而不只是一次模型请求。技术基线是 .NET 10、Native AOT、Expert 模式，以及 Windows、Linux、macOS 三平台发布。

[快速开始](#快速开始) · [使用文档](./docs/使用文档/README.md) · [命令矩阵](./docs/命令规范/09-CLI命令矩阵.md) · [方案设计](./docs/方案设计/01-实施方案-V1.md)

---

Madorin.AI.Runtime 是 Madorin（马得令）的 AI 执行底座。三个平台使用同一个命令名 `madorin`；Windows 发布文件是 `madorin-cli.exe`，Linux 和 macOS 是 `madorin-cli`。它同时是：

- 可直接使用的交互式 CLI 与单次任务入口。
- 通过 Named Pipe 对外提供协议的常驻 Runtime Server。
- 供桌面程序、服务和自动化宿主接入的 .NET Client SDK。
- 按协议隔离的 Provider Worker，以及本地可治理的会话与状态存储。

Runtime 负责模型访问、Expert 执行、工具调用、流式事件，以及对话历史和恢复状态的权威持久化。CLI 只是入口之一；宿主程序通过 Client SDK 走同一套协议。

## 多平台

多平台是当前交付的重点。Windows、Linux 和 macOS 不是三套产品，而是同一套 CLI、Runtime Server、Client SDK 和 Provider Worker 的三个发布目标。命令、配置、Session、Skills 和 `.madorin` 数据布局保持一致；已发布的 Native AOT 二进制运行时不需要安装 .NET SDK。

| 平台 | RID | 发布文件 |
| --- | --- | --- |
| Windows x64 | `win-x64` | `madorin-cli.exe` 与同目录 Provider Worker |
| Linux x64 | `linux-x64` | `madorin-cli`，要求 glibc 2.35 |
| macOS Apple Silicon | `osx-arm64` | `madorin-cli` |

默认执行 `build/madorin-cli.publish.ps1` 会同时发布这三个平台，产物分别位于：

```text
releases/Madorin-Cli/win-x64/madorin-cli/bin/
releases/Madorin-Cli/linux-x64/madorin-cli/bin/
releases/Madorin-Cli/osx-arm64/madorin-cli/bin/
```

每个 `bin` 目录都包含该平台的 CLI、对应的 Provider Worker 和 `providers.manifest.json`。需要单平台时再指定 `win`、`linux` 或 `mac`。当前发布矩阵不包含 Intel 版 macOS，也不把 Linux ARM64 列为正式目标。

## 当前能力

当前源码中的运行模式只有 **Expert**。V1 实施方案曾经规划 Expert、Meeting、Work 三种模式；Meeting 与 Work 已从运行时移除。多 Agent 协作改由 Expert 内的委派、并行作业和小型计划承担。历史三模式描述只解释设计来源，不能当作当前契约。

| 能力 | 现在做什么 |
| --- | --- |
| Expert 执行 | 单个主 Agent 连续规划、调用工具并完成多轮任务 |
| 子 Agent | `mcli_delegate_agent` 同步委派；`mcli_agent_job_*` 后台并行作业 |
| 小型计划 | `mcli_expert_*` 管理 2–15 步计划、步骤状态与放弃恢复 |
| 请求控制 | 温度、TopP、最大长度、种子、停止序列、JSON 输出和流式开关 |
| Provider | OpenAI、Anthropic、OpenAI Compatible、DeepSeek、Kimi、阿里云 / DashScope、Gemini、GLM、Custom、Ollama；Azure OpenAI 为实验协议，不出现在初始化向导 |
| 进程隔离 | 正式 Provider 以独立 Worker 进程按需启动，通过 stdin/stdout 长度前缀 JSON 帧通信 |
| 工具 | 受授权约束的文件、附件、记忆、HTTP 与进程工具，并支持宿主反向 RPC |
| Skills | 本地技能目录的列出、查看、校验和刷新 |
| 恢复 | 流式事件、取消、超时、确认、断线重放、Session 续接和检查点 |
| 数据 | SQLite + WAL 保存状态与索引，JSONL 保存权威消息，Blob Store 保存大内容 |
| 集成 | `serve` + `Madorin.AI.Runtime.Client`，宿主按 Session 注入 Provider 与 Agent |

内置工具覆盖文件检查、列举、读取、附件、写入、追加、搜索、行级编辑、复制、移动、删除和建目录，以及记忆读写、HTTP、受控命令执行、Agent 委派和小型计划。工具权限以 Runtime 为权威，工作区内默认可按授权执行，工作区外必须经过明确授权。

## 快速开始

### 环境

- .NET 10 SDK，以仓库 `global.json` 为准（当前为 10.0.301，允许同主版本向前滚动）。
- 构建需要 SDK；已发布的 Native AOT 二进制运行时不需要安装 SDK。
- 发布目标是 `win-x64`、`linux-x64` 和 `osx-arm64`，详见[多平台](#多平台)。
- 至少准备一个模型服务的 API Key；Ollama 可走本地地址。

### 构建并检查

```powershell
dotnet restore .\Madorin.AI.Runtime.slnx
dotnet build .\Madorin.AI.Runtime.slnx -c Debug
dotnet run --project .\src\Madorin.AI.Runtime.Cli -- config init
dotnet run --project .\src\Madorin.AI.Runtime.Cli -- doctor
```

`config init` 会引导选择 Provider、填写 API Key、选择默认模型，并可选配置代理。独立 CLI 的个人配置默认写入 `~/.madorin/config.json`。

### 运行

不带参数进入单行 REPL：

```powershell
dotnet run --project .\src\Madorin.AI.Runtime.Cli --
```

执行一次任务：

```powershell
dotnet run --project .\src\Madorin.AI.Runtime.Cli -- run `
  --provider DeepSeek `
  --model deepseek-chat `
  --input "用三句话说明这个运行时负责什么"
```

`run` 还支持 `--session` 续接会话、`--input-file`、`--output-file`、`--no-stream`、`--timeout`，以及 `text`、`json`、`jsonl` 输出。完整参数以 `run --help` 和[命令矩阵](./docs/命令规范/09-CLI命令矩阵.md)为准。

已发布二进制位于 `releases/Madorin-Cli/madorin-cli/bin/`。安装后用 `madorin` 代替上面的 `dotnet run`。

## 命令

| 命令 | 用途 |
| --- | --- |
| `serve` | 启动常驻 Runtime Server |
| `run` | 执行一次任务，或在没有输入时进入交互模式 |
| `version` | 查看程序、协议、运行时和平台版本 |
| `doctor` | 检查配置、Provider、数据库和运行状态 |
| `config` | `init`、`edit`、`show`、`validate` |
| `provider` | `protocols`，列出支持的 Provider 协议 |
| `agent` | 列出、创建、编辑和删除 Agent 定义 |
| `skill` | 列出、查看、校验和刷新本地 Skills |
| `memory` | 初始化、查看、追加、编辑和清空记忆 |
| `session` | 列出、查看、导出、压缩、删除、归档和取消归档 |
| `db` | 检查、修复、重建索引、压缩、备份和迁移 |
| `storage` | 检查消息与 Blob，回收长期未被引用的内容 |
| `ctl` | 查询实例、活动 Session / Run，取消 Run，更新在线凭据，请求关闭 |

全局选项：

```text
--workspace <path>   指定任务工作区
--data-dir <path>    指定 Runtime 数据目录
--log-level <level>  Debug、Information、Warning 或 Error
--no-color           禁用彩色输出
--json               输出机器可读 JSON
```

## 配置与数据

优先级是：命令行覆盖 > Agent 绑定 > 工作区选择 > 全局默认。宿主集成不读取 `~/.madorin` 个人配置，连接、凭据和选择由宿主按每个 Session 注入。

```text
~/.madorin/
├── config.json       Provider、默认模型和默认 Agent
└── agents/           独立 Agent 定义
```

工作区还可以放置可选的 `<workspace>/.madorin/config.json`。Runtime 的保留数据区是：

```text
<workspace>/.madorin/
├── state.db          SQLite + WAL 状态、元数据和索引
├── messages/         Session 的 JSONL 正文和上下文缓存
├── blobs/            按内容哈希保存的大型内容
├── checkpoints/      可恢复检查点
├── staging/          未完成传输的暂存区
├── locks/            工作区写锁和实例租约
└── logs/             Runtime 日志
```

不要直接改 `state.db`、`messages/` 或 `blobs/`。查询、压缩、备份、修复和清理都走 CLI 或 Client SDK。需要纳入版本库的工作区文件，只建议保留 `.madorin/config.json`。

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
├── Madorin.AI.Runtime.Modes.Expert
├── Madorin.AI.Runtime.Transport.{Abstractions,NamedPipes}
├── Providers/
│   ├── Madorin.AI.Runtime.Providers.Abstractions
│   ├── Madorin.AI.Runtime.Providers.WorkerHost
│   └── Madorin.AI.Runtime.Providers.<Protocol>[.Worker]
├── Madorin.AI.Runtime.Services
├── Madorin.AI.Runtime.Server
├── Madorin.AI.Runtime.Client
└── Madorin.AI.Runtime.Cli
tests/
├── samples/Madorin.AI.Runtime.SampleHost
└── *.Tests
docs/
├── 使用文档/          当前使用、协议和 Provider 开发说明
├── 开发文档/          概念、安装、集成、安全和运维
├── 方案设计/          V1 方案、架构修订和专项设计
├── 命令规范/          CLI 契约与命令矩阵
├── 执行步骤/          仍在使用的执行与验收记录
└── 备档/              已完成或已废弃材料
```

工程版本见 `Directory.Build.props` 的 `MadorinCliVersion`。当前工程基线是 .NET 10、C# 14、可空引用、中央包管理、警告即错误、确定性构建，以及面向发布的 Native AOT。

## 架构边界

```text
Host / CLI
    ├── Client SDK ── Contracts ── Transport.Abstractions
    │                           └── Transport.NamedPipes
    └── Runtime Server
            ├── Modes.Expert / Orchestration
            ├── Provider Worker（按协议独立进程）
            ├── Tools / Skills
            ├── Services
            └── Persistence（SQLite、JSONL、Blob）
```

- `Entities` 与 `Contracts` 定义稳定模型和公开协议，不依赖具体 Provider、数据库或界面。
- Provider、工具、持久化和传输都先有 Abstractions，再放具体实现。
- `Server` 是 Runtime 组合根。`Client` 只通过公开协议和传输抽象访问 Runtime。
- 参考宿主在 `tests/samples/Madorin.AI.Runtime.SampleHost`，只引用公开 Client SDK 与 Contracts。
- Provider Worker 与主程序同目录发布，由 `providers.manifest.json` 声明；主程序不把全部供应商 SDK 编进同一个 AOT 二进制。

Host 与 Runtime 使用 Named Pipe JSON 消息，带认证、心跳和事件流。Runtime 与 Provider Worker 使用大端 UInt32 长度前缀帧，帧类型包括 `hello`、`invoke`、`event`、`cancel`、`shutdown` 和 `error`。

## 文档

文档冲突时，按这个顺序判断：

1. 当前源码、测试和已冻结的机器快照。
2. [CLI 命令矩阵](./docs/命令规范/09-CLI命令矩阵.md)。
3. 仍在执行的[执行步骤](./docs/执行步骤/README.md)。
4. [架构修订 V1](./docs/方案设计/02-架构修订-V1.md)。
5. [实施方案 V1](./docs/方案设计/01-实施方案-V1.md) 和其他方案设计。
6. [备档](./docs/备档/README.md)。

| 想做什么 | 从这里开始 |
| --- | --- |
| 看文档地图和权威顺序 | [文档入口](./docs/README.md) |
| 5 分钟上手 | [快速开始](./docs/使用文档/01-快速开始.md) |
| 使用独立 CLI | [独立 CLI 使用指南](./docs/使用文档/02-独立CLI使用指南.md) |
| 把 Runtime 嵌进应用 | [SDK 集成指南](./docs/使用文档/03-SDK集成指南.md) |
| 理解 Expert、委派和计划 | [Expert 模式完整指南](./docs/使用文档/Expert模式完整指南.md) |
| 查概念、权限和数据区 | [概念与导航](./docs/开发文档/01-概念与导航.md) |
| 开发 Provider | [Provider 开发指南](./docs/使用文档/05-Provider开发指南/README.md) |
| 看 Provider 进程隔离方案 | [AOT 按需加载方案](./docs/方案设计/12-Provider进程隔离AOT按需加载方案.md) |
| 看参考宿主 | [SampleHost](./tests/samples/Madorin.AI.Runtime.SampleHost/README.md) |

## 构建、测试与发布

```powershell
dotnet build .\Madorin.AI.Runtime.slnx -c Debug
dotnet test .\Madorin.AI.Runtime.slnx -c Debug --no-build
dotnet run --project .\src\Madorin.AI.Runtime.Cli --no-build -- --help

.\build\madorin-cli.publish.ps1
```

上面的默认命令同时发布 Windows、Linux 和 macOS。只发一个平台时：

```bash
./build/madorin-cli.publish.ps1 win
./build/madorin-cli.publish.linux.sh --rid linux-x64
./build/madorin-cli.publish.macos.sh --rid osx-arm64
```

产物包含 `madorin-cli`、各 `madorin-provider-*` 和 `providers.manifest.json`。Worker 清单见 `build/provider-workers.json`。Release 发布保持零 IL 裁剪 / AOT 警告，协议 JSON 继续使用源生成上下文。

## 开源协议与版权

Madorin 产品与 SDK 的授权说明以 [Netor.Madorin README](../Netor.Madorin/README.md) 为准，其中声明采用 MIT License。

本运行时由藏将科技出品，产品标识为 Madorin & Streamx。

© 2026 madorins.com. All rights reserved.

## 联系方式

- 网站：https://madorins.com
- 邮箱：[service@madorins.com](mailto:service@madorins.com)
- 微信：

![微信](../Netor.Madorin/Res/images/wechat.jpg)

## 关于作者

**白明**，道号白三明。

正一·天师道·三山滴血派弟子，青城山天医派传承人，内丹术修习者，现隐居于福建。

软件工程师、架构师、企业家、创业顾问。20 年从业经验，行业先驱，曾多次荣获国家级创新奖励。

致力于修行与科技的融合探索，坚信科学修行、文明飞升的理念。业余时间投身于前沿科技研究，将道家智慧与现代技术相结合。

**人生信条**：人只要还有一口气在，就应当自强不息。

科技进步、社会发展是大势所趋。社会变革对有准备的人是机遇，对故步自封者是淘汰。唯有不断学习、积极进取，才能在时代浪潮中立足。

发扬道家文化，弘扬华夏精神。
