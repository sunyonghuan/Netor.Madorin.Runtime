# Madorin.AI.Runtime.Cli 深度介绍

> 一个面向 .NET 的完整 AI Runtime 平台、命令行工作台与 Client SDK

今天我们来深入认识一个超越传统 AI Agent 外壳的工程平台：**Madorin.AI.Runtime**。

## 先说结论

Madorin.AI.Runtime 不是一个更快、更聪明的模型，也不只是一个命令行聊天工具。它更准确的定位，是一套**面向 .NET 10 的生产级 AI 执行底座、多模型统一运行时和完整的持久化工作台**。

如果把大模型比作发动机，那么 Madorin.AI.Runtime 不是另一台发动机。它更像底盘、方向盘、仪表盘、变速箱、油箱、行车记录仪和维修保养系统的完整组合。

这也是理解 Madorin.AI.Runtime 的第一把钥匙。

---

## 一、Madorin.AI.Runtime 到底是什么？

很多人第一次看到这个项目，会把几个不同层次的东西混在一起。

其实 Madorin.AI.Runtime 是一个完整的工程项目家族，核心层次分别是：

### 1. **Madorin.AI.Runtime.Cli**
独立命令行程序，既是交互式 REPL，也是持久化 Runtime Server 的启动器和控制工具。用户直接运行的 `madorin` 命令就在这一层。

### 2. **Madorin.AI.Runtime.Server**
真正的 AI 执行运行时，负责 Session 管理、Run 编排、三种工作模式（Expert / Meeting / Work）、工具调用、Provider 适配、状态持久化和事件流。

### 3. **Madorin.AI.Runtime.Client**
.NET Client SDK，封装 Runtime 的启动、连接、认证、事件消费、回调、恢复和生命周期管理，供外部宿主（桌面应用、服务端、自动化脚本）集成。

### 4. **Multi-Provider 适配层**
统一 11 家模型供应商的接口差异，包括 OpenAI、Anthropic、OpenAI Compatible、DeepSeek、Kimi、Aliyun/DashScope、Gemini、GLM、Ollama 和 Azure OpenAI。

### 5. **本地数据层**
SQLite + WAL 状态数据库、JSONL 消息存储、Blob Store、Checkpoints、Locks、Logs，全都围绕可控、可追踪和可恢复设计。

### 6. **三种工作模式**
- **Expert**：单 Agent 深度分析与复杂决策
- **Meeting**：多 Agent 协作、会议纪要和信息整理
- **Work**：Manager-Worker 编排的日常任务执行

所以，别人说"我在用 Madorin Runtime"，可能有四种完全不同的意思：

- 第一种，是把它当成一个现成的 CLI 工作台，直接在终端跑任务；
- 第二种，是用 Client SDK 把 Runtime 嵌进自己的桌面应用或服务；
- 第三种，是作为 `serve` 常驻进程，由服务管理器托管；
- 第四种，是只拿其中一层（如 Provider 适配或 Persistence），嵌进自己的 Agent 架构。

Madorin.AI.Runtime 既是一个可以直接使用的 CLI 工具，也是一套可以拆开集成的 .NET 运行时组件。

这正是它和很多同类产品最大的不同。

---

## 二、Madorin.AI.Runtime 有什么用？

### 1. 直接当 AI 工作台用

运行 `madorin` 进入交互模式，就可以：

- 让 AI 读代码、写文件、修改文件、执行命令
- 支持流式输出、实时进度和工具调用状态
- Session 自动保存，随时恢复、压缩、导出
- 内置 `/model`、`/provider`、`/agent`、`/session`、`/resume`、`/export`、`/history` 等 REPL 命令
- 支持工作区切换（`/cd`）和多工作区隔离

它内置受权限约束的文件、内容、进程、HTTP 和记忆工具，并支持宿主/MCP 的反向 RPC 执行。一个能读写文件、修改代码、运行命令的模型，已经可以完成绝大多数本地编程任务。

Madorin 支持 **11 家模型供应商**：

- OpenAI (GPT)
- Anthropic (Claude)
- OpenAI Compatible (通用兼容接口)
- DeepSeek
- Moonshot Kimi
- Aliyun / DashScope (通义千问)
- Google Gemini
- GLM (智谱)
- Ollama (本地模型)
- Azure OpenAI
- 以及更多即将支持的 Provider

需要速度时用快模型，需要攻坚时再切强模型，不必换一套 Agent 外壳。只需在 REPL 中：

```
/provider OpenAI
/model gpt-4
```

### 2. 作为持久化 Runtime 常驻运行

```powershell
madorin serve --workspace E:\Projects\demo --max-runs 4
```

启动后：

- 通过 Named Pipe 提供 RPC 接口
- 支持多个 Client 并发连接
- 持有工作区写锁，保证状态一致性
- 自动保存 Session、消息、Blob 和 Checkpoints
- 支持在线凭据更新、Tool Catalog 替换和 Provider 热更新

这让 Runtime 不只是"会说话"，而是能真正作为长期运行的 AI 执行服务。

### 3. 嵌进自己的应用

如果你用 .NET，可以直接通过 Client SDK 创建 `RuntimeClient`：

```csharp
var options = new RuntimeClientOptions(
    PipeName: "my-app",
    HostInstanceId: Guid.NewGuid().ToString("N"))
{
    RuntimeExecutablePath = @"C:\Program Files\Madorin\madorin-cli.exe",
    WorkspaceDirectory = @"E:\Projects\demo",
    StartPolicy = RuntimeStartPolicy.StartIfMissing
};

await using var client = await RuntimeClient.CreateAsync(options, ct);
var runId = await client.StartNewSessionRunAsync(request, ct);

await foreach (var evt in client.ReadEventsAsync(lastGsn, ct))
{
    // 处理事件、更新 UI、投影状态
    await client.AcknowledgeEventsAsync(evt.Gsn, ct);
}
```

这意味着你不必从零开始处理：

- 模型流式输出
- 工具循环
- Session 恢复
- 上下文压缩
- 事件订阅
- 断线重连
- 检查点保存

Madorin Runtime 已经把最麻烦的那层骨架搭好了。

### 4. 做成自己的专用 Agent

Madorin Runtime 真正有意思的地方，不是它默认做了多少，而是它允许你自己决定要加什么：

- 经常重复的一套做事方法，写成 **Skill**（存放在 `<workspace>/.madorin/skills` 或全局目录）
- 需要定制 Agent 行为，写 **Agent Config**（支持自定义 System Prompt、绑定 Provider/Model）
- 想把这一整套能力发给别人，打包成可复用的配置和工作区模板

比如，你可以把 Madorin Runtime 改造成：

- 代码审查 Agent（配合 `/export` 生成审查报告）
- 文档生成 Agent（配合 Skill 和记忆系统）
- 运维 Agent（配合 bash 工具和日志分析）
- 研究 Agent（配合 Meeting 模式和多 Agent 协作）

---

## 三、为什么有人会觉得 Madorin Runtime 工程质量高？

这里一定要把"质量"拆开。

一个 AI Runtime 的工程质量，大致由这几部分组成：

### 1. **代码规范严格**
- 可空引用检查（Nullable Enable）
- 严格分析（TreatWarningsAsErrors）
- 确定性构建
- 集中包管理（Central Package Management）
- Native AOT 就绪（PublishAot=true）

### 2. **架构边界清晰**
```
Host / CLI
    ├── Client SDK ── Contracts ── Transport.Abstractions
    │                           └── Transport.NamedPipes
    └── Runtime Server（组合根） ── Modes / Orchestration
                                   ├── Provider Adapters
                                   ├── Tools
                                   ├── Services
                                   └── Persistence
```

- `Entities` 和 `Contracts` 定义稳定的数据模型与公开协议
- `Abstractions` 层隔离扩展实现
- `Server` 是具体组合根，`Client` 只通过公开协议访问
- 宿主不引用 Provider 实现、SQLite 或 Server 内部

### 3. **完整的数据层**
```
<workspace>/.madorin/
├── state.db          SQLite + WAL 状态、元数据和索引
├── messages/         Session 的 JSONL 消息正文和上下文缓存
├── blobs/            按内容哈希保存的大型内容
├── checkpoints/      可恢复检查点
├── staging/          未完成传输的暂存区
├── locks/            工作区写锁和实例租约
└── logs/             Runtime 日志
```

### 4. **可恢复执行**
- 支持流式事件、取消、超时、确认、重放
- 断线重连后从上次确认的 GSN 继续
- Session 恢复（`/resume`）
- 检查点保存与回滚
- 上下文压缩（`/compact`）

### 5. **完整的 CLI 命令**
28 个命令文件，涵盖：

| 命令 | 用途 |
|------|------|
| `serve` | 启动持久化 Runtime Server |
| `run` | 执行任务或进入交互模式 |
| `version` | 查看版本信息 |
| `doctor` | 检查配置、Provider、数据库 |
| `config` | 管理配置 |
| `agent` | 管理 Agent 定义 |
| `provider` | 管理 Provider |
| `memory` | 管理记忆文件 |
| `session` | Session 管理（列出、导出、压缩、归档） |
| `db` | 数据库维护 |
| `storage` | Blob 一致性检查 |
| `ctl` | 查询运行实例、在线凭据更新 |
| `skills` | Skill 管理 |

### 6. **工程规模**

截至 2026-08-07：

| 指标 | 数量 |
|------|-----:|
| C# 源码文件 | 650 |
| C# 有效代码行数 | 127,403 |
| 文档与脚本（中文） | 263,552 字符 |
| Provider 数量 | 11 个 |
| 测试项目 | 6 个 |

但必须说清楚：

**Madorin Runtime 的工程质量，是因为它面向生产交付、长期运维和实际项目，而不是一次性 demo。**

---

## 四、Madorin Runtime 和 Pi、Claude Code、Codex 有什么区别？

### Pi：给你一副骨架

Pi 的核心观念是：不替用户规定工作流。

它默认没有 MCP、没有子 Agent、没有计划模式、没有内置待办，也没有每执行一步都弹出来的权限确认。需要这些能力，通过 Skill、Extension 或第三方 Package 自己加。

优点是轻、透明、可改造；代价是很多事情要自己负责。

### Claude Code：给你一套成熟的 Anthropic 编程工作台

Claude Code 更像一个完成度很高的成品。权限控制、MCP、计划模式、自动化接口等能力已经形成自己的工作方式，和 Claude 模型的结合也更紧。

如果你只想装好就用，不想自己设计 Agent，成品工具通常更省心。

### Codex：给你一套 OpenAI 的完整 Agent 工作环境

Codex 不只是终端工具，还覆盖桌面端、IDE、云端任务、Skills、MCP、子 Agent、沙箱和审批等能力。它更强调完整工作流、安全边界和跨环境协作。

### **Madorin Runtime：给你一套 .NET 的生产级 AI 执行底座**

Madorin Runtime 的定位在于：

1. **完整的运行时能力**：不只是模型调用，而是 Session、Run、Mode、Tool、Persistence、Event 的完整编排
2. **.NET 生态原生**：Native AOT、集中包管理、严格可空检查、确定性构建
3. **多 Provider 统一**：11 家模型供应商，统一抽象
4. **可恢复执行**：断线重连、检查点、Session 恢复、上下文压缩
5. **可交付性**：面向 `win-x64` Native AOT 发布，适合做稳定的本地交付物
6. **Client SDK**：不只是 CLI，还提供 .NET SDK 供外部宿主集成

**Pi** 更像"我来组装自己的车"，**Claude Code** 和 **Codex** 更像"整车、道路救援和车队系统一起交付"，**Madorin Runtime** 更像"给 .NET 开发者的完整 AI 执行平台和可嵌入运行时"。

---

## 五、Madorin Runtime 最常见的错误用法

### 错误一：把 Madorin Runtime 当成一个更快、更聪明的模型

Madorin Runtime 不是模型。回答质量首先取决于你连接的模型（OpenAI / Anthropic / DeepSeek / ...），其次才是上下文、工具和 Agent 设计。

换成 Madorin Runtime，不等于模型自动升级。

### 错误二：在敏感目录里裸奔

Madorin Runtime 的内置工具（`bash`、文件读写）会继承启动它的用户权限。虽然有 `ToolConsentHandler` 机制，但默认不是沙箱隔离。

对于重要项目和陌生代码，应该：

- 使用容器、虚拟机、受限用户
- 配置工作区级别的 `.madorin/config.json` 约束行为
- 通过 `StandaloneToolConsentPolicy` 启用交互式审批

### 错误三：直接操作 `.madorin` 目录

`.madorin` 是 Runtime 保留目录。宿主应通过 CLI 或 Client SDK 访问和维护其中的数据，不要直接修改：

- `state.db`（SQLite 数据库）
- `messages/`（JSONL 消息文件）
- `blobs/`（内容寻址存储）

这些文件有严格的格式、锁和一致性约束。

### 错误四：忽略版本兼容性

Madorin Runtime 使用协议版本（当前 1.1）和 Client SDK 版本（0.1.26+）。升级 Runtime 或 Client 时：

- 检查 `ExpectedRuntimeVersion`
- 阅读 [迁移指南](./docs/开发文档/)
- 使用 `madorin doctor` 检查兼容性

---

## 六、Madorin Runtime 正确应该怎么用？

### 第一步：先用最小配置跑起来

```powershell
# 初始化配置（交互式向导）
madorin config init

# 检查配置和环境
madorin doctor

# 进入交互模式
madorin
```

或者一次性执行任务：

```powershell
madorin run --provider OpenAI --model gpt-4 --mode expert --input "用三句话说明这个项目的价值"
```

### 第二步：配置 Provider 和 Agent

编辑 `~/.madorin/config.json`：

```json
{
  "schemaVersion": 1,
  "providers": [
    {
      "name": "openai",
      "type": "OpenAI",
      "apiKey": "sk-...",
      "models": ["gpt-4", "gpt-5"]
    },
    {
      "name": "anthropic",
      "type": "Anthropic",
      "apiKey": "sk-ant-...",
      "models": ["claude-opus-4", "claude-sonnet-4"]
    }
  ],
  "defaultModel": "gpt-5",
  "selection": {
    "provider": "openai",
    "model": "gpt-5",
    "agent": "default"
  }
}
```

创建自定义 Agent（`~/.madorin/agents/code-reviewer.json`）：

```json
{
  "id": "code-reviewer",
  "name": "Code Reviewer",
  "systemPrompt": "你是一个严格的代码审查专家...",
  "provider": "anthropic",
  "model": "claude-opus-4"
}
```

### 第三步：使用 Session 和恢复

```powershell
# 列出最近的 Session
/resume

# 恢复特定 Session
/resume abc123

# 查看当前 Session 状态
/session

# 查看上下文统计
/context

# 压缩历史消息
/compact

# 导出为 Markdown
/export ~/Desktop/session-export.md
```

### 第四步：集成到自己的应用

```csharp
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;

// 创建 Client（SDK 会自动启动或连接 Runtime）
var options = new RuntimeClientOptions(
    PipeName: "my-app",
    HostInstanceId: Guid.NewGuid().ToString("N"))
{
    RuntimeExecutablePath = @"C:\path\to\madorin-cli.exe",
    WorkspaceDirectory = @"E:\Projects\demo",
    StartPolicy = RuntimeStartPolicy.StartIfMissing
};

await using var client = await RuntimeClient.CreateAsync(options, ct);

// 启动新 Session
var request = new NewSessionRunRequest(
    SessionIdempotencyKey: Guid.NewGuid().ToString("N"),
    RunIdempotencyKey: Guid.NewGuid().ToString("N"),
    RuntimeMode.Expert,
    nextTurnSelection,
    [new TextContentBlock("请分析这段代码...")]);

var runId = await client.StartNewSessionRunAsync(request, ct);

// 消费事件流
long lastGsn = 0;
await foreach (var evt in client.ReadEventsAsync(lastGsn, ct))
{
    // 更新 UI
    Console.WriteLine($"[{evt.EventType}] {evt.Gsn}");
    
    // 持久化投影
    await SaveToDatabaseAsync(evt);
    
    // 确认消费
    await client.AcknowledgeEventsAsync(evt.Gsn, ct);
    lastGsn = evt.Gsn;
}
```

### 第五步：生产部署

```powershell
# 作为 Windows 服务部署
sc.exe create MadorinRuntime `
  binPath= "C:\Program Files\Madorin\madorin-cli.exe serve --workspace C:\Data\workspace" `
  start= auto

# 启动服务
sc.exe start MadorinRuntime

# 或使用 NSSM（推荐）
nssm install MadorinRuntime "C:\Program Files\Madorin\madorin-cli.exe"
nssm set MadorinRuntime AppParameters "serve --workspace C:\Data\workspace"
nssm set MadorinRuntime AppDirectory "C:\Program Files\Madorin"
nssm set MadorinRuntime AppStdout "C:\Data\workspace\.madorin\logs\stdout.log"
nssm set MadorinRuntime AppStderr "C:\Data\workspace\.madorin\logs\stderr.log"
nssm start MadorinRuntime
```

---

## 七、Madorin Runtime 适合谁，不适合谁？

### Madorin Runtime 很适合这几类人：

- **想研究 AI Runtime 到底怎么运行的 .NET 开发者**
- **想自己掌控模型、工具、提示词和持久化的人**
- **想快速做一个专用 Agent 原型并可交付的人**
- **不希望被单一模型厂商锁死的人**
- **需要把 AI 能力嵌进 .NET 桌面应用或服务的人**
- **需要长期运行、可恢复、可运维的 AI 执行环境的人**

### 但如果你更看重：

- 开箱即用、默认安全、完整图形界面
- 官方云端协作
- 跨语言支持（非 .NET）
- 移动端 App
- 不想维护自己的 Runtime 和数据库

那么 Claude Code、Codex、OpenCode，或者直接使用在线 AI 服务，可能更合适。

**Madorin Runtime 给你的不是"什么都配好了"，而是"完整的 .NET AI 执行底座，你可以完全掌控"。**

---

## 八、几个容易混淆的名词

- **模型（Model）**：真正生成文字、代码和工具调用决策的大脑
- **Agent**：能够围绕目标多轮思考并调用工具完成任务的系统
- **Runtime**：把模型、工具、上下文、Session、Persistence 和执行循环接起来的运行平台
- **Tool**：Agent 可以调用的具体动作，例如读文件、执行命令
- **Skill**：告诉 Agent 某类任务应该怎样完成的操作说明
- **Mode**：Runtime 支持的三种工作模式（Expert / Meeting / Work）
- **Session**：一次完整的对话会话，可保存、恢复、压缩、导出
- **Run**：Session 内的一次执行单元，包含用户输入和 Agent 响应
- **Provider**：模型供应商的统一适配层
- **Compaction**：上下文太长时，对旧对话做有损摘要，给后续任务腾空间
- **GSN (Global Sequence Number)**：事件的全局序列号，用于断线重连和幂等消费
- **Checkpoint**：可恢复检查点，用于长时间运行的 Session

---

## 最后

调研完 Madorin.AI.Runtime，我明白了它为什么值得深入了解。

它没有急着替所有人定义 AI Runtime 应该长什么样，而是：

1. **把运行时能力做完整**：Session、Run、Mode、Tool、Persistence、Event 一应俱全
2. **把架构边界拉清晰**：Contracts、Abstractions、Server、Client 职责分明
3. **把工程质量做扎实**：可空检查、严格分析、Native AOT、集中包管理
4. **把可交付性做到位**：CLI、SDK、常驻服务、宿主集成全覆盖
5. **把多 Provider 做统一**：11 家模型供应商，一套抽象
6. **把持久化做透彻**：SQLite、JSONL、Blob、Checkpoint、Lock、Log

这种完整性，带来了生产级的可靠性、可运维性和可扩展性，也把架构、实现和运维的责任交还给了你。

**Madorin Runtime 真正厉害的地方就是给你一个完整的 .NET AI 执行底座。**

**剩下的，你自己决定。**

---

## 附录：快速开始

### 系统要求

- **操作系统**：Windows 10/11 或 Windows Server 2019+
- **架构**：x64
- **无需安装 .NET Runtime**：Native AOT 编译，所有依赖已打包

### 安装

1. **下载发布版本**
   
   从 [Releases](https://github.com/your-org/Netor.Madorin.Ai.Runtime/releases) 下载最新的 `madorin-cli-win-x64.zip`

2. **解压到目标目录**
   
   ```powershell
   # 推荐安装路径
   Expand-Archive madorin-cli-win-x64.zip -DestinationPath "C:\Program Files\Madorin"
   ```

3. **添加到 PATH（可选）**
   
   ```powershell
   # 添加到用户环境变量
   $env:PATH += ";C:\Program Files\Madorin"
   [Environment]::SetEnvironmentVariable("PATH", $env:PATH, "User")
   ```

### 首次使用

```powershell
# 初始化配置（交互式向导）
madorin config init

# 检查配置和环境
madorin doctor

# 进入交互模式
madorin
```

### 基本命令

```powershell
# 查看版本
madorin version

# 执行单次任务
madorin run --provider OpenAI --model gpt-4 --input "分析这段代码"

# 启动持久化服务
madorin serve --workspace E:\Projects\demo

# 查看帮助
madorin --help
madorin run --help
```

### 配置目录说明

```text
~/.madorin/
├── config.json       Provider、默认模型和默认 Agent 配置
└── agents/           独立 Agent 定义

<workspace>/.madorin/config.json    工作区配置（可选）
```

优先级规则是：命令行覆盖 > Agent 绑定 > 工作区选择 > 全局默认。

### 文档导航

- [总体方案与总需求](./README.md)
- [宿主集成指南](./开发文档/04-宿主集成指南.md)
- [事件协议参考](./开发文档/11-事件协议参考.md)
- [参考宿主说明](../samples/Madorin.AI.Runtime.SampleHost/README.md)

### 开发者资源

如果你是开发者，想要参与贡献或从源码构建：

```powershell
# 克隆仓库
git clone https://github.com/your-org/Netor.Madorin.Ai.Runtime.git
cd Netor.Madorin.Ai.Runtime

# 需要 .NET 10 SDK（以 global.json 为准）
dotnet restore .\Madorin.AI.Runtime.slnx
dotnet build .\Madorin.AI.Runtime.slnx -c Debug

# 运行测试
dotnet test .\Madorin.AI.Runtime.slnx -c Debug

# 从源码构建 Native AOT 发布版
dotnet publish .\src\Madorin.AI.Runtime.Cli -c Release -r win-x64 --self-contained true
```

---

**树懒的技术观察 · 2026-09-06**

*如果你对 Madorin.AI.Runtime 感兴趣，欢迎 Star、Fork 和贡献代码。*

*这是一个真正面向生产、面向交付、面向长期运维的 .NET AI Runtime 平台。*
