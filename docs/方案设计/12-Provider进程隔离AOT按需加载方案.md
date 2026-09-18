# 12-Provider 进程隔离、AOT 拆分与按需加载方案

**日期：** 2026-09-18
**状态：** 已冻结，待实施。本文是后续编码、测试、发布脚本改造的唯一实施规格。
**读者：** 执行本改造的 AI / 开发者。不要凭训练记忆改协议或发布布局；与本文冲突时以本文为准，与源码冲突时先对照第 3 节现状再改源码。
**前置决策：** 方案 B。Runtime 与 CLI 必须 Native AOT，禁止反射加载托管 DLL。Provider 做成独立 AOT 可执行文件，由 Runtime 拉起为子进程。

## 0. 给执行者的硬约束

1. 本方案是工程改造规格，不是讨论稿。未写进「允许」的行为一律视为禁止。
2. 不要把 Host↔Runtime Named Pipe 协议套到 Provider Worker 上。
3. 不要在 Provider 进程里创建、关闭、恢复 Session，不要读写工作目录 `.madorin`。
4. 不要按 Session、Run、对话、模型名启动 Worker。一个 Runtime 实例对每个 **协议 slug** 最多 1 个 Worker 进程。
5. 不要为每个 BaseUrl/ApiKey 再开进程。同一协议进程内用 `IHttpClientFactory` 按请求携带连接信息，禁止共享可变 `HttpClient` 单例。
6. 主程序（CLI/Server）发布闭包中不得再 ProjectReference 任何 `Madorin.AI.Runtime.Providers.{Name}` 实现项目（Abstractions 除外）。
7. 所有 Provider Worker 与 `madorin-cli` 发布到 **同一个 bin 目录**，方便按相对路径启动。
8. Windows / Linux / macOS 都必须能发布。现有 `build/madorin-cli.publish.ps1` 要改；并新增 Linux、macOS 专用脚本。
9. 实现时保持现有 `IRuntimeProviderAdapter.CompleteStreamingAsync` 对编排层透明。Expert/Work/Meeting 不得感知子进程。
10. 编码、路径、脚本一律 UTF-8。Unix 脚本用 LF、`chmod +x`。
11. Worker 的父进程永远是 **该工作区的 Runtime**，不是 Host，不是机器级 daemon。N 个工作区 Runtime 使用同一协议时，允许存在 N 个同名 `madorin-provider-{slug}` 进程。禁止跨 Runtime 复用、禁止 Host 持有 Worker 再转发给多个 Runtime。

## 1. 冻结决策

| 编号 | 决策 | 说明 |
| --- | --- | --- |
| D1 | Native AOT 全程 | `madorin-cli`（CLI 与 Runtime 入口，同一可执行文件）与每个 Provider Worker 均为 `PublishAot=true`。 |
| D2 | Provider 无 Session | Session/Run/历史/锁只在 Runtime。Worker 是无状态翻译器。 |
| D3 | Worker 不操作 Session | Session 不变式见 01。本方案只约束 Worker：换 slug/连接时不创建、关闭或恢复 Session；旧 Worker 不关 Session。 |
| D4 | 进程键 = 协议 slug（**仅限同一 Runtime 实例内**） | 同一 Runtime 里所有 OpenAI Compatible 连接共用一个 `madorin-provider-openai-compatible` 子进程。不同 Runtime 之间不共用。 |
| D5 | 连接键 = ProviderId | BaseUrl 与 ApiKey 在每条 `invoke` 中传入。沿用现有 factory；本方案只追加多 connection 隔离。 |
| D6 | IPC = 父进程 stdin/stdout 长度前缀 JSON 帧 | 不用 Named Pipe 作为 Worker 传输。日志走 stderr。凭据不准进 argv/环境变量。 |
| D7 | 编排层零改语义 | `ProviderInvocationBoundary.CompleteStreamingAsync` 仍对 `IRuntimeProviderAdapter` 调用。 |
| D8 | 同目录发布 | `releases/Madorin-Cli/madorin-cli/bin/` 内同时放 CLI 与全部 Worker。 |
| D9 | Worker 归属 = 工作区 Runtime | 宿主每个工作目录一个 Runtime（现有 `WorkspaceWriteLock`）。每个 Runtime 自己 `Process.Start` 子进程。磁盘上的 Worker exe 全机一份（与 CLI 同 `bin/`）；**进程实例**按 Runtime 复制，不按机器去重。 |

## 2. 非目标

- 本方案不调整 Work/Meeting 边界，该边界以 11 为准。
- 不把 Host 直接连 Provider。
- 不做机器级 Provider daemon、不做跨工作区 Worker 进程池、不把 Worker 提升为 Named Pipe 服务供多个 Runtime attach。
- 不做工作目录文件交换、不做反射插件、不做双 AOT 运行时同进程加载。
- 不在本方案把 Microsoft Agents AI / Skills 迁出 Server。Server 仍可保留 `Microsoft.Agents.AI` 供 Skills；必须删除仅因 Provider 存在的 `Microsoft.Agents.AI.OpenAI` 直接引用。Worker 收 null Skills；禁止透传 `SkillsInvocationContext`，禁止在 Worker 内重建 Skills 工厂。
- 不按「需要才从网络下载 Worker」做在线商店。本阶段是 **拆分 + 同包发布 + 按协议懒启动**。本版本 `providers.manifest.json` 不写 `download` 字段；在线商店不在本文。

## 3. 现状（改造前，必须对照）

- CLI：`src/Madorin.AI.Runtime.Cli/Madorin.AI.Runtime.Cli.csproj`，`PublishAot=true`，`AssemblyName=madorin-cli`。
- CLI 引用 Server；Server 引用全部 10 个 Provider 实现项目（见 `Madorin.AI.Runtime.Server.csproj` 第 13–22 行）。因此 AOT 把全部厂商 SDK 链进 `madorin-cli`。
- 协议→适配器：`src/Madorin.AI.Runtime.Server/RuntimeProviderAdapterFactory.cs`。
- 独立 CLI 工厂：`src/Madorin.AI.Runtime.Cli/Config/StandaloneProviderFactory.cs`，按 **ProviderId** cache adapter。
- 连接配置：`ProviderConnectionConfig`（ProviderId、Protocol、BaseUrl、ApiKey 等）。
- HTTP：`ProviderHttpClientFactory` 共享 `SocketsHttpHandler`，`CreateClient` 返回短生命周期 `HttpClient`。
- 发布：`build/madorin-cli.publish.ps1`（`-Rid` 已含 win/linux/osx），入口 `build/madorin-cli.publish.cmd`。当前只把 CLI 发到 `releases/Madorin-Cli/madorin-cli/bin/`，**不发布 Worker**。没有独立的 `.sh` Linux/macOS 脚本。
- 已知协议字符串（必须保持大小写与空格）：`OpenAI`、`Anthropic`、`OpenAI Compatible`、`DeepSeek`、`Kimi`、`Aliyun`、`Azure OpenAI`、`Gemini`、`GLM`、`Custom`、`Ollama`。`Custom` 与 `OpenAI Compatible` 共用同一 Worker。

## 4. 目标进程拓扑

磁盘上的 Worker 可执行文件全机一份，与 CLI 同 `bin/`。进程实例按 Runtime 复制。

```
宿主进程（可同时打开多个工作目录）
  ├─ Named Pipe → Runtime A = madorin-cli（workspace-1，持有该目录 WorkspaceWriteLock）
  │     ├─ 该 Runtime 内多个 Session / Run
  │     └─ 子进程（仅 A 的监督器拥有；按 slug 懒启动，每 slug 最多 1 个）
  │           stdin/stdout 长度前缀帧
  │           madorin-provider-openai-compatible   ← 属于 A，PID 独立
  │           madorin-provider-anthropic           ← 仅当 A 用到 Anthropic 才启动
  ├─ Named Pipe → Runtime B = madorin-cli（workspace-2，另一把锁）
  │     └─ 子进程（B 自己 Process.Start，即使协议与 A 相同也是另一个 PID）
  │           madorin-provider-openai-compatible   ← 属于 B，不得 attach A 的 stdout
  └─ 尚未出站 HTTP 的 Runtime C：只有 madorin-cli，零个 Worker
```

单 Runtime 视角下可能出现的 Worker 文件名（按需，不是启动时全拉起）：

```
madorin-provider-openai[.exe]
madorin-provider-anthropic
madorin-provider-openai-compatible
madorin-provider-deepseek
madorin-provider-kimi
madorin-provider-aliyun
madorin-provider-azure-openai
madorin-provider-gemini
madorin-provider-glm
madorin-provider-ollama
```

独立 `madorin run` / REPL 与 `madorin serve` 使用同一套 Worker 监督器。LocalRuntime 与 RuntimeServer 必须走同一 `IProviderWorkerSupervisor`，禁止两套启动逻辑。

**计数公式（执行者必须按此实现与验收，不得自行优化成全局池）：**

- `Runtime进程数` = 已打开且持锁的工作区数（现有行为，本方案不改）。
- `某 slug 的 Worker 进程数` 健康态上界 = 至少对该 slug 发生过一次需出站 HTTP 的操作（含 `invoke` 与 doctor 真实探测）、且尚未 Dispose 的 Runtime 个数。故障态与已死未拉起不计入；崩溃后到下次出站 HTTP 前为 0。
- 不是 `工作区数 × 10`。未使用的协议不启动。同一 Runtime 内同一 slug 不得出现第二个进程。
- 同一台机器上允许进程列表出现多个 `madorin-provider-openai-compatible`，用父 PID 区分归属；禁止按映像名杀光或按映像名复用。

反例（错误）：Runtime A 配了 `openai A`、`openai A1`、`deepseek A2`、`deepseek B3`，Runtime B 配了 `openai B0`、`openai B1`、`deepseek B2`、`deepseek B3`，以为会拉起 8 个子进程。连接条目不是进程。即使 8 条连接的 BaseUrl/ApiKey 完全相同，也**不会**变成「4 个 openai 进程 + 4 个 deepseek 进程」。

正例（必须实现）：上述 8 条都是配置条目。协议若为 `OpenAI` 与 `DeepSeek`（slug 为 `openai`、`deepseek`），且两个 Runtime 都对该两协议发过 invoke：

- Runtime A：1 个 `madorin-provider-openai` + 1 个 `madorin-provider-deepseek`
- Runtime B：再 1 个 openai + 1 个 deepseek（父 PID 不同）
- 健康态合计 **4** 个子进程，不是 8。

同一 Runtime 内 `openai A` 与 `openai A1` 共用 A 的那一个 openai Worker；`invoke.connection` 带各自 ProviderId/BaseUrl/Key。BaseUrl/Key 相同不会再合并跨 Runtime 的进程，也不会在同一 Runtime 内再拆出新进程。

## 5. 四层身份（执行时不得合并）

| 层 | 键 | 谁持有 | 例子 |
| --- | --- | --- | --- |
| 协议 | 配置字段 `protocol`；进程键 `slug` | **每个 Runtime 内** 按 slug 一个 Worker 进程 + 全机一份 exe | `OpenAI Compatible` → `openai-compatible` |
| 连接 | `ProviderId` | 配置条目；每次 invoke 传入 | `deepseek`、`siliconflow`，各有 BaseUrl+Key |
| 会话 | `sessionId` / `runId` | 仅 Runtime | 与 Worker 进程数无关；Worker 不得按 Session/Run 启停或路由 |
| 调用 | `invocationId` | Runtime 生成并写入 IPC 帧；Worker **必须**按此路由 `invoke` / `cancel` / `event` / `error` | 与 `RuntimeProviderRequest.InvocationId` 相同；不是进程键、不是会话键 |

映射规则：

- Runtime 监督器字典键：`slug`（小写、连字符）。
- Worker 不得按 ProviderId 启停进程。
- 会话层只有 `sessionId` / `runId`，仅 Runtime 持有；不得把 `invocationId` 并进会话层。
- `invocationId` 是 IPC 调用键：同一 Worker 并发多 invoke 时必须按它把帧送到对应调用（见第 8.5 节）。argv 仍禁止传 sessionId。
- 同一 Compatible Worker 同时服务多个 ProviderId 时，每个 `invoke` 必须带完整 `connection`（见第 8 节）。不得使用进程级默认 BaseUrl/ApiKey 覆盖后续请求。

## 6. 发布目录（所有 RID 相同相对结构）

以 Windows 为例，Linux/macOS 去掉 `.exe`，二进制无扩展名。

```
releases/Madorin-Cli/madorin-cli/
  SKILL.md                  （现有 skill 文件，保持）
  bin/
    madorin-cli.exe         （Windows）或 madorin-cli
    e_sqlite3.dll           （仅 Windows 需要时保留现有原生依赖）
    providers.manifest.json
    madorin-provider-openai.exe
    madorin-provider-anthropic.exe
    madorin-provider-openai-compatible.exe
    madorin-provider-deepseek.exe
    madorin-provider-kimi.exe
    madorin-provider-aliyun.exe
    madorin-provider-azure-openai.exe
    madorin-provider-gemini.exe
    madorin-provider-glm.exe
    madorin-provider-ollama.exe
```

`providers.manifest.json` 由发布脚本生成，禁止手写进 git 作为唯一来源。本阶段不提交 `build/providers.manifest.template.json`；清单只由脚本生成。

Worker 查找顺序（Runtime 启动时解析一次，路径必须绝对路径）：

1. 环境变量 `MADORIN_PROVIDER_DIR`（若设置，必须是目录）。
2. `Path.GetDirectoryName(Environment.ProcessPath)`，即 CLI/Runtime 所在 bin。
3. 找不到对应 slug 的文件时，失败信息必须包含 protocol、slug、已搜索目录。对照第 9 节失败码表：未开 Run 时独立 CLI 用 09 退出码 `2`，宿主按 04「Protocol unsupported」类拒绝且不开新终态；已开 Run 时投影 `run.failed`。不得静默回退到进程内适配器，不得新增 Host 消息类型或 CLI 命令。

Unix：发布后对所有 `madorin-cli` 与 `madorin-provider-*` 执行 `chmod 755`。禁止依赖 NTFS 可执行位。

## 7. 协议与可执行文件对照表

实施时必须使用下表。`Custom` 不得单独做一个 exe。

| config.json `protocol` | slug | AssemblyName / 文件名 | Worker 项目 | 引用的实现项目 |
| --- | --- | --- | --- | --- |
| OpenAI | `openai` | `madorin-provider-openai` | `...Providers.OpenAI.Worker` | `Providers.OpenAI` |
| Anthropic | `anthropic` | `madorin-provider-anthropic` | `...Providers.Anthropic.Worker` | `Providers.Anthropic` |
| OpenAI Compatible | `openai-compatible` | `madorin-provider-openai-compatible` | `...Providers.OpenAICompatible.Worker` | `Providers.OpenAICompatible` |
| Custom | `openai-compatible` | 同上 | 同上 | 同上 |
| DeepSeek | `deepseek` | `madorin-provider-deepseek` | `...Providers.DeepSeek.Worker` | `Providers.DeepSeek` |
| Kimi | `kimi` | `madorin-provider-kimi` | `...Providers.Kimi.Worker` | `Providers.Kimi` |
| Aliyun | `aliyun` | `madorin-provider-aliyun` | `...Providers.Aliyun.Worker` | `Providers.Aliyun` |
| Azure OpenAI | `azure-openai` | `madorin-provider-azure-openai` | `...Providers.AzureOpenAI.Worker` | `Providers.AzureOpenAI` |
| Gemini | `gemini` | `madorin-provider-gemini` | `...Providers.Gemini.Worker` | `Providers.Gemini` |
| GLM | `glm` | `madorin-provider-glm` | `...Providers.GLM.Worker` | `Providers.GLM` |
| Ollama | `ollama` | `madorin-provider-ollama` | `...Providers.Ollama.Worker` | `Providers.Ollama` |

共 **10 个** Worker 可执行文件，不是 11 个。

## 8. Worker IPC 协议

### 8.1 传输

- 父进程 `ProcessStartInfo`：`RedirectStandardInput=true`、`RedirectStandardOutput=true`、`RedirectStandardError=true`、`UseShellExecute=false`、`CreateNoWindow=true`。
- 标准输入/输出均为 **二进制**。禁止把 stdout 当文本日志。
- 帧：`UInt32` **大端** 长度 + UTF-8 JSON 正文。长度不含这 4 字节。单帧上限 `Lmax = 16 * 1024 * 1024` 字节。`L ≤ Lmax` 合法（含等于）；`L > Lmax` 非法。
  - Worker 读到 `L > Lmax`：写 `error` 后**进程**退出码 `2`（只给监督器，**不是** 09 的 CLI 退出码 `2`，禁止原样透传）。
  - Runtime 读到 `L > Lmax`：不得再读该帧正文；该 slug 进行中 invoke 全部 `invocation_failed`（code `ProviderWorkerExited`），然后立即 Kill 该 Worker（不发 `shutdown`；Kill 后仍存活则再 Kill 一次）。退出码不透传为 CLI 码；已开 Run 按第 9 节表投影。
  - Runtime 写侧：序列化后 `L > Lmax` 则不得写帧；仅该次 invoke `invocation_failed`（code `InvokeFailed`），不杀进程。
- stderr：UTF-8 文本日志。Runtime 将 stderr 接到现有诊断日志，**必须打码 ApiKey**。禁止 Worker 把 Key、Authorization 完整值写入 stderr。
- argv：只允许 `--slug <slug>`。禁止传 BaseUrl、ApiKey、workspace、sessionId。
- 环境变量：禁止传 ApiKey。可设 `DOTNET_EnableDiagnostics=0`。
- 工作目录：设为 bin 目录，不要设成用户 workspace，避免 Worker 误写项目文件。

### 8.2 JSON 约定

- 源生成上下文：新建 `Madorin.AI.Runtime.Providers.WorkerHost` 内 `ProviderWorkerJsonContext`（`JsonSerializerContext`，AOT）。
- 所有帧有 `"type"` 字符串判别器。
- 时间戳不用。取消只靠 `cancel` 帧 + `InvocationId`。
- `RuntimeProviderRequest` / `RuntimeProviderEvent` 复用 `Providers.Abstractions` 现有类型。`CancellationToken`、`Skills` 等 `[JsonIgnore]` 字段不进帧。Worker 内 adapter 按现有构造方式接收 **null Skills**：禁止把 `SkillsInvocationContext` 做成可序列化后透传，禁止在 Worker 内重建 Skills 工厂（含 `MafSkillsAgentFactory` / `AgentSkillsProvider`）。该次 invoke 需要的 tools 由 Runtime 投影进 `RuntimeProviderRequest.Tools`，随 `invoke.request` 下发。Skills 宿主职责留在 Server 编排层；adapter 若仍强依赖 Skills SDK 才能工作，应把该依赖剥回 Server，不得把 `Microsoft.Agents.AI` Skills 宿主下沉到无状态 Worker。

### 8.3 Runtime → Worker 帧

`hello`（进程启动后父进程发送的第一帧，未完成 hello 不得 invoke）：

```json
{
  "type": "hello",
  "ipcVersion": 1,
  "protocol": "OpenAI Compatible",
  "slug": "openai-compatible",
  "runtimeVersion": "0.1.46"
}
```

`invoke`：

```json
{
  "type": "invoke",
  "invocationId": "<与 RuntimeProviderRequest.InvocationId 相同>",
  "connection": {
    "providerId": "siliconflow",
    "protocol": "OpenAI Compatible",
    "baseUrl": "https://api.siliconflow.cn/v1",
    "apiKey": "<secret>",
    "authenticationHeader": "Authorization",
    "authenticationScheme": "Bearer",
    "userAgent": null,
    "apiVersion": null,
    "models": ["deepseek-v3"],
    "configurationVersion": null,
    "capabilities": null,
    "probePath": "models",
    "maxTools": null
  },
  "request": { }
}
```

`request` 对象必须能反序列化为现有 `RuntimeProviderRequest`。`connection` 字段与 `ProviderConnectionConfig` 一一对应。

`cancel`：`{"type":"cancel","invocationId":"..."}`
`ping`：`{"type":"ping","id":"..."}` 复用探测见第 9 节。
`shutdown`：`{"type":"shutdown"}` 时限与 Kill 见第 9 节停止顺序。仅自行退出才要求退出码 0。

### 8.4 Worker → Runtime 帧

`hello.ok`：`{"type":"hello.ok","ipcVersion":1,"slug":"openai-compatible","protocol":"OpenAI Compatible"}`
`hello.rejected`：`{"type":"hello.rejected","code":"IpcVersionMismatch|SlugMismatch","message":"..."}`。hello 按 slug 与 `ipcVersion` 校验，不按 `protocol` 字符串全等。`Custom` 与 `OpenAI Compatible` 对 `openai-compatible` Worker 都合法。启动失败处理见第 9 节。
`pong`：`{"type":"pong","id":与 ping 相同}`。时限见第 9 节。
`event`：`{"type":"event","invocationId":"...","event":{...}}` 其中 `event` 为现有多态 `RuntimeProviderEvent`（已有 `type` 判别器：`text_delta` 等）。
`error`：进程级或该 invocation 级。`{"type":"error","invocationId":null或id,"code":"WorkerCrashed|ProtocolMismatch|InvokeFailed","message":"..."}`。`ProtocolMismatch` 用于 invoke 的 protocol 映射到非本进程 slug（`Custom` 与 `OpenAI Compatible` 同 slug，不触发）；不用于 `ipcVersion`。message 不得含 Key。
每个 `invoke` 必须恰好以一个终端 `RuntimeProviderEvent`（`invocation_completed` 或 `invocation_failed`）或 `error` 结束。流中途可以有任意多个非终端 event。

### 8.5 并发

一个 Worker 必须同时处理多个 `invoke`（多 Session/Run）。stdout 写帧必须串行化（单锁）。按 `invocationId` 路由。`cancel` 取消对应 `CancellationTokenSource`。禁止全局取消所有 invoke，除非 `shutdown`。

## 9. Worker 进程生命周期

类型名（必须使用，放在 Server 项目）：

- `IProviderWorkerSupervisor`
- `ProviderWorkerSupervisor`
- `ProviderWorkerProcess`（一个 slug 一个）
- `ProviderWorkerAdapter : IRuntimeProviderAdapter`（对编排层的代理）

启动：

1. `Resolve(protocol)` → slug → 监督器 `GetOrAdd(slug)`。
2. 已有进程且 stdin 可写 → 发 `ping`，等待对应 `id` 的 `pong`。本阶段不另做周期心跳；两次复用之间无最小间隔。收到 `pong` 且耗时 ≤ 3s（含等于）→ 复用。耗时 > 3s 无 `pong`、stdin 不可写、或进程已退出 → 不复用：立即 Kill 旧进程（不发 `shutdown`；Kill 后仍存活则再 Kill 一次，其后同停止顺序），再走步骤 3。该次请求不经新 Host 消息：新进程按步骤 4 hello，或已熔断则立即失败。
3. 否则 `Process.Start` 对应 exe。Windows 把子进程加入 Job Object（`KillOnJobClose`）仅作托底。Linux/macOS 跟踪 PID（可 `setpgid`）。
4. 父进程先发 `hello`，计时从写出该帧完成起。收到 `hello.ok` 且耗时 ≤ 3s（含等于）→ 启动成功。耗时 > 3s 仍无 `hello.ok`、或收到 `hello.rejected`、或进程先退出 → 启动失败：立即 Kill 该进程（不发 `shutdown`；Kill 后仍存活则再 Kill 一次，其后同停止顺序）。未完成 hello 不得 invoke。该次出站请求按下面失败码表与 `hello.rejected` 同行投影，不新开 Host 消息。
5. 仅第一次需要出站 HTTP 的操作才 `Process.Start`（含 `CompleteStreamingAsync`、doctor 真实探测）。列出协议、离线校验、`providers.upsert` 不启动。

失败：

- 进程意外退出：该 slug 的进行中 invoke 全部 `invocation_failed`（code `ProviderWorkerExited`）。下次出站 HTTP 再拉起。不得因此释放 `WorkspaceWriteLock`。
- 单 invoke 失败不影响其他 invoke、不影响其他 slug。
- 禁止自动无限重启。崩溃计入：非停止顺序导致的进程退出（意外退出、帧超限退出、hello 失败被杀、ping 失败被杀）。不计入：停止顺序中的 `shutdown` 自行退出或随后 Kill。滑动窗口 30s，两端闭合：对崩溃时刻 t，计入 `[t-30s, t]`（间隔恰好 30s 仍计入）。窗口内次数 ≥ 3（含第 3 次）→ 该 slug 故障态，直到 Runtime 重启不再 `Process.Start`。第 3 次当次：该 slug 进行中 invoke 全部 `invocation_failed`（code `ProviderWorkerExited`），不为该次请求重拉进程；之后的请求立即失败，按下面表「熔断」投影，不新开 Host 消息。窗口内次数 ≤ 2 仍允许下次出站 HTTP 再拉起。

失败码对照（禁止新表面）：Worker **进程退出码**与 IPC `error.code` 只用于 Runtime↔Worker。禁止新增 Host 消息类型或 CLI 命令；禁止把 Worker 退出码当作 09 CLI 退出码。

| 情况 | 既有 CLI（09） | 既有 Run 终态（04） |
| --- | --- | --- |
| 找不到 Worker / `hello.rejected` / hello 超时（> 3s 无 `hello.ok`） / slug 或 protocol 无法映射 | `2`（参数或配置错误）；`run`/`doctor` 主要失败码含 `2` | 未开 Run：JSON-RPC 拒绝（04「Protocol unsupported」类），不开新终态。已开 Run：`run.failed` |
| 已开 Run 期间进程退出、IPC `error`、熔断、invoke 失败 | 09 `run` 主要失败码为 `2/4/130`，无 Worker 专用项；不得新开 CLI 码 | `run.failed`（用户取消则为 `run.cancelled`） |
| Worker 进程退出码 `2`（帧超限） | 禁止把该进程退出码原样当作 CLI 退出码；已开 `run` 时由 Runtime 投影为 09 的 `run` 失败（`2/4/130` 中按 09 既有规则选，不得新开码） | 已开 Run → `run.failed` |
| 认证 401/403 | 不在本方案重定。09 固定码有 `3`，但 `run` 主要失败码未列 `3` | 04 既有 `credentials.refresh.request`；终态按 04/15 |
| 用户中断 | `130` | `run.cancelled` |

空闲：本阶段 **不** 做空闲退出。Worker 活到 Runtime dispose。避免来回冷启动。

停止（顺序唯一）：发 `shutdown` → 等待自行退出，时限 2s。耗时 ≤ 2s（含等于）已退出 → 视为自行退出，仅此时要求码 0。等到 2s 时仍在（等待时长 ≥ 2s 且进程存活）→ Kill。Kill 后仍存活：再 Kill 一次；若仍在，只写诊断日志，继续停下一个 Worker，不阻塞 Runtime Dispose，不新开 Host 消息。Job Object 只作父进程异常退出托底，不替代该顺序。Unix 不另走 1.5 秒 SIGTERM。

换模型：同一 slug、同一进程，`request.modelId` 不同即可。
换连接（同一协议不同厂商）：同一进程，不同 `connection.providerId/baseUrl/apiKey`。`credentials.update` 只改已登记 Key，不重启、不新开 Worker；新 Key 出现在下一帧 `invoke.connection`，不热改进行中 invoke 已下发的 connection。
换协议：另一 slug，可能启动第二个 Worker。旧 Worker 保持存活。不要「关闭旧协议上的 Session」。

多工作区（必须按此实现，禁止“优化掉”）：

1. 同一规范化工作区唯一写实例（现有 `WorkspaceWriteLock`）；Host 按工作区持有 Runtime。`AlwaysStart` 只是 04 三种 StartPolicy 之一，本方案不改锁与启动策略。
2. 每个 Runtime 构造 **自己的** `ProviderWorkerSupervisor`。监督器不得做成进程内静态单例跨 Runtime 共享（测试里两个 LocalRuntime 并行时也必须两套监督器、两套子进程）。
3. stdin/stdout 只能有一个父进程。Runtime A 不得把请求写进 Runtime B 的 Worker stdin。
4. Job Object / Unix 进程组按 Runtime 绑定：Runtime A 退出必须只杀掉 A 的 Worker，不得误杀 B。
5. 工作区 A 的 ApiKey 只出现在 A→其 Worker 的 `invoke.connection` 帧里。禁止为了少开进程而让 B 的请求进入 A 的 Worker。
6. 不要因为“同名 exe 已在运行”就 attach。查找进程、按映像名复用、按 slug 的全局 Named Pipe 一律禁止。
7. 本阶段不为跨工作区去重做空闲迁移或引用计数。Workspace 关闭 → Runtime Dispose → 只停该 Runtime 的 Worker。

## 10. Worker 内 HttpClient 规则

HTTP 管道（含 Handler 池化寿命）沿用现有 `ProviderHttpClientFactory` 与 15，本方案不重定。本方案只追加多连接隔离：

1. **禁止** 把 BaseUrl/ApiKey 存在进程级可变字段并在请求之间改写。
2. 每个 `invoke` 用本次 `connection` 出站（可对相同 ProviderId 做短缓存，缓存键必须含 BaseUrl 与 ApiKey 的哈希；Key 变更必须失效）。允许的实现：每次 invoke 按现有 factory 构造 adapter，用完不跨 invoke 复用 adapter 实例。
3. 凭据只放在本次请求；禁止写入共享 Handler 字段。
4. 现有 SDK Client 按实现项目现有方式构造，但必须每次 invoke 用本次 Key，不得使用静态 Client。

## 11. 新工程与必须修改的旧工程

### 11.1 新建类库

路径：`src/Providers/Madorin.AI.Runtime.Providers.WorkerHost/Madorin.AI.Runtime.Providers.WorkerHost.csproj`

- `TargetFramework` `net10.0`，`IsAotCompatible` 继承 Directory.Build.props。
- 引用：仅 `Providers.Abstractions`。本版本禁止引用 `Contracts`（`RuntimeProviderEvent` / `RuntimeProviderRequest` 已在 Abstractions）。**禁止**引用 Server、Cli、任意 Provider 实现、MAF。
- 职责：读帧、写帧、hello 校验、invoke 多路、cancel、把 `connection` 变成 `ProviderConnectionConfig` 的 DTO（DTO 放 WorkerHost 或把 `ProviderConnectionConfig` 从 Server 下沉到 Abstractions）。
- **必须把 `ProviderConnectionConfig` 从 `Madorin.AI.Runtime.Server` 移到 `Madorin.AI.Runtime.Providers.Abstractions`**，避免 Worker 引用 Server。同步修改命名空间与所有 using。

Worker 入口循环放在 WorkerHost：`ProviderWorkerHost.RunAsync(protocol, slug, adapterFactory, stdin, stdout, stderr, ct)`。每个 Worker `Program.cs` 只注册「本协议 → 现有 adapter 构造」再调用 `RunAsync`。

### 11.2 新建 10 个 Worker 可执行项目

路径模式：`src/Providers/Madorin.AI.Runtime.Providers.{Name}.Worker/Madorin.AI.Runtime.Providers.{Name}.Worker.csproj`

每个项目必须：

```xml
<OutputType>Exe</OutputType>
<AssemblyName>madorin-provider-{slug}</AssemblyName>
<PublishAot>true</PublishAot>
<InvariantGlobalization>true</InvariantGlobalization>
<RootNamespace>Madorin.AI.Runtime.Providers.{Name}.Worker</RootNamespace>
```

引用：`WorkerHost` + **仅一个** 对应实现项目。禁止一个 Worker 引用两个实现。`Program.cs` 用顶层语句，调用 `ProviderWorkerHost.RunAsync`。

加入 `Madorin.AI.Runtime.slnx` 的 `/src/` 文件夹。

### 11.3 修改 Server

文件：`src/Madorin.AI.Runtime.Server/Madorin.AI.Runtime.Server.csproj`

- **删除** 全部 10 个 `Providers.*` 实现 ProjectReference。
- **删除** `PackageReference Microsoft.Agents.AI.OpenAI`（若删除后 Server 编译失败，把残留类型用法迁回 Provider Worker 或删除；不得为编译通过再加回该包）。
- **保留** `Microsoft.Agents.AI` 仅当 Skills 代码仍直接使用；否则一并删除。
- 新增代码：`ProviderWorkerSupervisor`、`ProviderWorkerAdapter`、协议→slug 表（从现有 `RuntimeProviderAdapterFactory.KnownProtocols` 改造，**删除** 所有 `new XxxProviderAdapter`）。
- `RuntimeProviderAdapterFactory.Create` 改为仅供 **Worker 进程**使用：把该类移到 WorkerHost 不可行（它引用实现类型）。做法：删除 Server 中的 Create 实现；在每个 Worker 的 Program 里写本协议的 `BuildAdapter(ProviderConnectionConfig)`；Server 只保留 `TryMapProtocolToSlug`。

`StandaloneProviderFactory`（Cli）：`GetOrAdd(providerId)` 改为返回 `supervisor.CreateAdapter(protocol, connection)`，即 `ProviderWorkerAdapter`。禁止在 CLI 进程 `new OpenAIProviderAdapter`。

`HostProviderRegistry.GetOrCreateAdapter`：同样改为监督器代理。Host 注入的 by-value 配置仍按 ProviderId 存储，调用时把 `HostProviderConfig` 的连接字段写入 `invoke.connection` 对应项；`Capabilities` / `ProbePath` / `MaxTools` 不从宿主配置写入。`providers.upsert` 只登记 Registry，不 `Process.Start`。`credentials.update` 只更新已登记 Key，不新开、不重启 Worker；新 Key 走下一帧 `invoke.connection`。

`LocalRuntime` / `RuntimeServer`：在 `StartAsync` 创建 **一个** `ProviderWorkerSupervisor`（绑定 `Environment.ProcessPath` 目录），`DisposeAsync` 按第 9 节停止顺序关闭全部 Worker。不得每个 Run 创建监督器。

`Cli/Config/StandaloneProviderProtocolCatalog.cs`：删除各 `BuildXxx` 中对实现类型的引用。该文件今天 `using` 了全部 Provider 实现命名空间，改造后只保留协议元数据（DisplayName、DefaultBaseUrl 等）。删除 `BuildAdapter` 委托，不得改为调用监督器。监督器入口只留 `StandaloneProviderFactory`。

Doctor / 配置探测：真实 HTTP 探测走 Worker。启动时机见第 9 节。

### 11.4 修改 CLI 项目

`Madorin.AI.Runtime.Cli.csproj`：不要直接引用任何 Provider 实现。继续引用 Server 即可。确认 `PublishAot`、`InvariantGlobalization` 不变。AOT 裁剪后主 exe 不得再包含 `OpenAI`、`Anthropic` 等 SDK 程序集。验收用 `dotnet publish` 后检查 `madorin-cli` 的 ILC 映射或发布目录：bin 里不应再有厂商 SDK 的独立 dll（AOT 单文件则用体积与 `trimming` 日志：主项目不得有对 `Madorin.AI.Runtime.Providers.OpenAI` 的编译引用）。

### 11.5 测试项目

- `tests/Madorin.AI.Runtime.Provider.Tests`：**继续直接引用实现项目**，单测进程内 adapter，不强制走 Worker。这是协议映射与 HTTP 覆写的单元测试。
- `tests/Madorin.AI.Runtime.EndToEnd.Tests`：至少新增一类测试，用真实拉起的 Worker 可执行文件（Debug 可用非 AOT `dotnet run` 的 Worker 项目，或测试输出目录复制 Worker）。覆盖：同一 Compatible Worker 连续两个不同 BaseUrl 的 invoke 不串 Key；多 invocation 并发；未触发第 9 节熔断时，Worker 被 Kill 后下一轮可重新拉起且工作区锁仍被 Runtime 持有。
- 禁止为了测试把实现项目引用加回 Server。

现有 EndToEnd 若通过 `LocalRuntime` 打真实协议，改造后会自动走 Worker；测试夹具必须能找到 Worker exe。规定测试宿主把 Worker 发布/复制到 `AppContext.BaseDirectory`，或设置 `MADORIN_PROVIDER_DIR`。

## 12. 跨平台启动差异

| 项目 | Windows | Linux | macOS |
| --- | --- | --- | --- |
| 文件名 | `madorin-provider-{slug}.exe` | `madorin-provider-{slug}` | 同 Linux |
| 可执行位 | 不需要 | 发布脚本 `chmod 755` | 同 Linux |
| 子进程回收 | 顺序见第 9 节。Job Object（`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`）仅托底 | 同一套第 9 节停止顺序（`shutdown` → 等 2s → Kill → 再 Kill）；**无 Job Object**；跟踪 PID（可 `setpgid`）作托底；**无** 1.5s SIGTERM | 同 Linux（同样无 Job Object） |
| 路径 | 与 CLI 同目录 | 同左，注意 `ProcessPath` 在 Unix 可用 | 同左 |
| RID | `win-x64`、`win-arm64` | `linux-x64`、`linux-arm64` | `osx-x64`、`osx-arm64` |
| sqlite 旁路 | 保留现有 `e_sqlite3.dll` 拷贝行为 | 保留现有 `.so` 行为（若 CLI 仍依赖） | `.dylib` |
| 代码签名 | 本阶段不强制 | 不强制 | 本阶段不强制 notarize；脚本不得假设签名成功 |

`ProcessStartInfo.FileName` 必须是绝对路径。Unix 禁止依赖 PATH 里的同名文件。

## 13. 发布脚本改造

### 13.1 共享清单

新建 `build/provider-workers.json`，UTF-8，作为脚本与 Runtime 对照的机器可读来源（Runtime 内再有一份 C# 常量表，必须与此文件字段一致；CI 或测试应用 JSON 断言 slug 列表相同）。格式：

```json
{
  "workers": [
    { "protocol": "OpenAI", "slug": "openai", "project": "src/Providers/Madorin.AI.Runtime.Providers.OpenAI.Worker/Madorin.AI.Runtime.Providers.OpenAI.Worker.csproj" }
  ]
}
```

必须列出第 7 节全部 10 项。`Custom` 不出现在 workers 数组。

### 13.2 修改 `build/madorin-cli.publish.ps1`

在现有 CLI publish 成功之后、打包 zip 之前：

1. 读取 `provider-workers.json`。
2. 对每个 worker 项目 `dotnet publish -c Release -r $Rid --self-contained true -o $BinDir`，属性与 CLI 相同：`RestoreConfigFile`、`RestorePackagesPath`、`MadorinRuntimeContractsVersion`。
3. 输出文件名必须等于 `madorin-provider-{slug}`（+ `.exe` 当 `$Rid` 以 `win` 开头）。
4. 生成 `$BinDir/providers.manifest.json`：version、rid、workers[{protocol,slug,fileName}]。本版本不写 `download` 字段。
5. 清理 Worker 的 `*.pdb`、`*.xml`、`*.deps.json`（与现有 CLI 清理规则一致）。若 AOT 单文件仍带 native 旁路文件，只保留运行需要的。
6. zip 步骤已打包整个 `madorin-cli` skill 目录，Worker 在 bin 下会自动进 zip。验收 zip 内必须含 1 个 CLI + 10 个 Worker + manifest。
7. 步骤计数从 `[1/6]` 改为能覆盖 Worker 发布的序号（例如 `[1/7]`），日志必须打印每个 Worker 体积。

参数保持：`-Rid`、`-Rebuild`、`-NoBump`、`-Bump`。一次调用发布 **该 RID 的 CLI + 全部 Worker**。禁止只发 CLI 的默认路径；若需调试可加 `-SkipProviders`，默认 false，正式发布不得使用。

### 13.3 新建 Linux 脚本

路径：`build/madorin-cli.publish.linux.sh`

- shebang `#!/usr/bin/env bash`，`set -euo pipefail`。
- 用法：`./build/madorin-cli.publish.linux.sh [--rid linux-x64|linux-arm64] [--rebuild] [--no-bump] [--bump patch|minor|major]`
- 默认 RID=`linux-x64`。
- 必须调用同一套逻辑：优先 `pwsh -File build/madorin-cli.publish.ps1 -Rid ...`（Linux 上有 PowerShell 7 时）。**若 `pwsh` 不存在，shell 脚本必须自己实现完整发布**（bump 版本、pack Contracts、publish CLI、循环 publish 10 个 Worker、chmod、写 manifest、zip、sha256sum），不得以「请安装 pwsh」结束而不发布。
- zip 文件名保持 `Madorin.Cli-v{version}-{rid}.zip`，与 Windows 一致，写入 `releases/`。
- `chmod 755` 全部 bin 内无扩展名可执行文件。

### 13.4 新建 macOS 脚本

路径：`build/madorin-cli.publish.macos.sh`

- 与 Linux 脚本结构相同，默认 RID=`osx-arm64`，允许 `osx-x64`。
- 同样：有 pwsh 则委托 `madorin-cli.publish.ps1`，无 pwsh 则原生 bash 完整实现。
- 不得调用 Windows 的 `Compress-Archive`；无 pwsh 时用 `zip -r` 或 `ditto`。
- sha256 用 `shasum -a 256`。

### 13.5 Windows 入口

`build/madorin-cli.publish.cmd` 保持转发 ps1。文档与根 README 增加 Linux/macOS 调用示例。

## 14. 实施顺序（必须按序，完成前一节再开始下一节）

1. **下沉配置类型**：`ProviderConnectionConfig` 移到 Abstractions；全解决方案编译通过。
2. **WorkerHost 类库**：帧编解码、host 循环、JSON context。单测放 `Provider.Tests`：长度前缀往返、超限、hello ipcVersion 拒绝。
3. **先做一个协议打通**：`OpenAICompatible.Worker` + Server 监督器 + CLI 工厂改代理。用假 HTTP 或现有 Compatible 单测证明：主项目不再引用 Compatible 实现，进程可拉起，流式 event 回到 Expert。
4. **补其余 9 个 Worker**：每个 Program 只映射本协议。Server `RuntimeProviderAdapterFactory` 的 `new XxxAdapter` 全部删除。
5. **进程回收**：第 9 节停止顺序；Job Object 托底。Dispose 测试。
6. **串线测试**：同一 Compatible Worker 交错两个 connection 的 invoke。
7. **发布脚本**：json 清单、ps1、`build/madorin-cli.publish.linux.sh`、`build/madorin-cli.publish.macos.sh`。本地至少 `win-x64` 发通；linux/osx 脚本必须在对应 OS 或文档标明需在该 OS 执行，但脚本内容必须完整可运行。
8. **AOT 验收**：`madorin-cli` 体积应下降（相对改造前同 RID 记录）；bin 含 10 Worker；zip 可解压运行 `madorin --help` 与至少一次 Compatible 配置（无 Key 时失败信息清晰）。
9. **文档**：15 只补注册方式 / Worker 包装 / 清单 / slug，不改 HTTP、工具名、凭据、能力、向导的权威归属。更新 `docs/README.md` 导航到本文。

禁止并行先改 10 个 Worker 再写 WorkerHost：先 Compatible 闭环。

## 15. 验收标准（全部满足才算完成本方案）

1. `Madorin.AI.Runtime.Server.csproj` 与 `Madorin.AI.Runtime.Cli.csproj` 的 XML 中不出现 `Providers.OpenAI` 等实现项目路径（Abstractions、WorkerHost 除外；Server 不应引用 Worker 可执行项目）。
2. `rg` 在 `src/Madorin.AI.Runtime.Server` 与 `src/Madorin.AI.Runtime.Cli` 中找不到 `using Madorin.AI.Runtime.Providers.OpenAI;` 这类实现命名空间（Worker 项目除外）。
3. Release AOT 发布目录：`madorin-cli` + 10 个 `madorin-provider-*` + `providers.manifest.json`。
4. 启动 Runtime 后，尚未发生需要出站 HTTP 的操作前，进程列表只有 CLI/Runtime，没有 Provider Worker。
5. 第一次需要出站 HTTP 的操作之后，该 Runtime 下仅对应 slug 出现 **一个** Worker 进程。
6. 同一 Runtime 内同一协议两个不同 ProviderId 连续对话，只有一个 Worker 进程；请求打到各自 BaseUrl（用测试服务器或抓包/mock 证明，禁止只靠代码审查）。
7. 换到不同 slug 后出现第二个 Worker，第一个仍可存活；同 slug 换 protocol 字符串（`Custom` ↔ `OpenAI Compatible`）不新增进程。Session 侧见 01；本条只验收 Worker 进程数。
8. 未触发第 9 节熔断时：Kill Worker 进程后，进行中 Run 失败可观测；下一 Run 自动拉起新 Worker；工作区锁文件仍被 Runtime 持有。触发熔断后不再拉起，直到 Runtime 重启。
9. Worker 命令行与环境变量不含 ApiKey。
10. `build/madorin-cli.publish.linux.sh` 与 `build/madorin-cli.publish.macos.sh` 存在、可执行、参数与第 13 节一致。
11. 现有 Provider 单元测试与 EndToEnd 主线在改造后全绿（允许测试夹具增加 `MADORIN_PROVIDER_DIR`）。
12. 两个独立工作区 Runtime 先后对同一协议发消息后，进程列表必须出现 **两个** 同名 Worker，父 PID 分别等于两个 Runtime；关掉其中一个 Runtime 后，只少一个 Worker，另一个仍存活。

## 16. 禁止事项（违反即不合格）

- 在 Worker 内读写 `workspace`、`.madorin`、`state.db`、`messages/*.jsonl`。
- 在 Worker 内重建 Skills 工厂、序列化或透传 `SkillsInvocationContext`，或把 Skills 宿主职责下沉到 Worker。
- 为每个 Session 或每个 ProviderId 启动新进程。
- 按映像名/slug 在全机复用已有 Worker，或启动机器级 Provider daemon / 全局 Named Pipe Worker 服务。
- 把 `ProviderWorkerSupervisor` 做成静态跨 AppDomain/进程共享，或让 Host 持有 Worker 再转发给多个 Runtime。
- 宿主 Named Pipe 直接连 Worker。
- `Assembly.Load` / 插件 DLL / 双 AOT 运行时同进程。
- 把厂商原始响应写进工作目录再翻译。
- 在 Server 保留「进程内 fallback adapter」开关默认打开。测试可用 Internals 或独立测试工厂，生产路径只有 Worker。
- 发布时把 10 个 Worker 放到不同文件夹或按 RID 分子目录而不放进与 CLI 相同的 `bin/`。
- 文档未更新就声称完成。
- 为 Worker 失败新增 Host 消息类型或 CLI 命令，或把 Worker 进程退出码当作 09 CLI 退出码透传。

## 17. 本方案追加的新增 Provider 手续（改造完成后）

适配器实现、HTTP、工具名、凭据、能力、向导仍按 15。做完 15 之后，本方案只追加：

1. `src/Providers/Madorin.AI.Runtime.Providers.{Name}.Worker/` AOT exe，`AssemblyName=madorin-provider-{slug}`。
2. `build/provider-workers.json` 增加一条（`Custom` 仍不单独占 exe）。
3. Server `TryMapProtocolToSlug` 增加 protocol → slug。
4. `StandaloneProviderProtocolCatalog` 只加元数据，无实现 using。
5. 15 的协议表补 slug/Worker 一行；不改 15 的 HTTP / 工具名 / 凭据 / 能力 / 向导章节。
6. Provider.Tests 增加 protocol→slug 映射测。

不把实现项目加回 Server/Cli。不把 15 的适配器步骤抄进本节。

## 18. 与现有文档的关系

正文短编号按下表唯一解析（仓库内同号文档多个，不以文件名前缀猜路径）。

| 编号 | 路径 |
| --- | --- |
| 01 | `docs/开发文档/01-概念与导航.md` |
| 04 | `docs/开发文档/04-宿主集成指南.md` |
| 09 | `docs/命令规范/09-CLI命令矩阵.md` |
| 11 | `docs/方案设计/11-工作与会议编排范围决策.md` |
| 15 | `docs/开发文档/15-Provider适配器开发指南.md` |

- 本文覆盖「如何把 Provider 移出 AOT 主程序」中的 **注册方式 / 进程包装 / 清单 / slug**。
- Host↔Runtime 协议以 04 为准；命令契约以 09 为准；Session 不变式以 01 为准。
- 与 15 冲突时，**仅**上述四项以本文为准。15 的 HTTP 管道、工具名策略、凭据刷新、能力声明、配置向导仍以 15 为准。
- §17 只追加本方案手续，不改写 15 的适配器实现步骤。第 14 节第 9 项更新 15 时只补这四项，不覆盖 15 其余章节。

## 19. 执行者检查清单（开始写代码前勾选理解）

- 已理解 Worker 不创建/关闭/恢复 Session；换 Provider 的 Session 语义见 01。
- 已理解进程键是 slug 不是 ProviderId；slug 去重范围是单个 Runtime，不是整台机器。
- 已理解 Host 多工作区 = 多 Runtime = 每 Runtime 自己的 Worker 子进程；exe 文件共享，进程不共享。
- 已理解沿用现有 factory，本方案只追加多 connection 隔离，禁止进程级改 Key。
- 已理解 IPC 是 stdin/stdout 长度前缀，不是 Named Pipe。
- 已理解 Server/Cli 不得引用实现项目。
- 已理解 10 个 Worker 与 CLI 发到同一 `bin/`。
- 已理解必须新增 `build/madorin-cli.publish.linux.sh` 与 `build/madorin-cli.publish.macos.sh`。
- 已理解先打通 OpenAI Compatible，再复制其余 9 个 Worker。

## 20. 关键类型与文件落点速查

| 符号或文件 | 位置 |
| --- | --- |
| `ProviderConnectionConfig` | 迁到 `src/Providers/Madorin.AI.Runtime.Providers.Abstractions/ProviderConnectionConfig.cs` |
| `ProviderWorkerJsonContext` | `...WorkerHost/ProviderWorkerJsonContext.cs` |
| `ProviderWorkerHost.RunAsync` | `...WorkerHost/ProviderWorkerHost.cs` |
| `IProviderWorkerSupervisor` | `src/Madorin.AI.Runtime.Server/ProviderWorkers/IProviderWorkerSupervisor.cs` |
| `ProviderWorkerSupervisor` | `src/Madorin.AI.Runtime.Server/ProviderWorkers/ProviderWorkerSupervisor.cs` |
| `ProviderWorkerProcess` | `src/Madorin.AI.Runtime.Server/ProviderWorkers/ProviderWorkerProcess.cs` |
| `ProviderWorkerAdapter` | `src/Madorin.AI.Runtime.Server/ProviderWorkers/ProviderWorkerAdapter.cs` |
| `TryMapProtocolToSlug` | Server 内协议→slug（从 `RuntimeProviderAdapterFactory.KnownProtocols` 改造，见第 11.3 节）。禁止另立未定义的 `ProtocolSlugMap` 类型 |
| Worker `Program.cs` | 各 `*.Worker/Program.cs` |
| `provider-workers.json` | `build/provider-workers.json` |
| Linux 发布 | `build/madorin-cli.publish.linux.sh` |
| macOS 发布 | `build/madorin-cli.publish.macos.sh` |
| 本规格 | `docs/方案设计/12-Provider进程隔离AOT按需加载方案.md` |

帧 `type` 枚举（写 JSON context 时必须全部注册）：`hello`、`hello.ok`、`hello.rejected`、`invoke`、`event`、`cancel`、`ping`、`pong`、`shutdown`、`error`。







