# Provider 进程隔离 AOT 按需加载 : 100%

规格：`docs/方案设计/12-Provider进程隔离AOT按需加载方案.md` 第 14 节。必须按序，完成前一节再开始下一节。

Skills 归属（与规格 D2/D7 一致，不另开步骤）：Worker 不提供 `load_skill` / `read_skill_resource` / `run_skill_script`；Runtime 通过 `SkillRuntimeTools` 把三工具写入目录并投影进 `invoke.request.Tools`；Host 仍只提供 `skill.search`。

评审修补（对照方案 12 代码评审）：Kill/Reap/hello/ping 失败先 FailAll；stderr 打码入诊断日志；Worker 封 Console.Out；写侧超 `Lmax` 只失败该次 invoke；IPC `error.code` 原样投影；doctor 不再把声明探测写成 HTTP reachable；缺 exe/hello 超时投影为 `ProviderWorkerUnavailable`。

## Step 1 下沉 ProviderConnectionConfig : 100%
- [√] 类型迁到 `Providers.Abstractions`，命名空间改为 `Madorin.AI.Runtime.Providers.Abstractions`
- [√] 删除 Server 原文件，同步全部 using / 限定名
- [√] CLI / Server / Provider.Tests / EndToEnd.Tests 编译通过（MConsole 缺 Entities 引用为既有问题，与本步无关）

## Step 2 WorkerHost 类库 : 100%
- [√] 新建 `Providers.WorkerHost`（仅引用 Abstractions）
- [√] 帧编解码、hello 校验、invoke 多路、JSON context
- [√] Provider.Tests：长度前缀往返、超限、hello ipcVersion 拒绝（3 通过）

## Step 3 先打通 OpenAI Compatible : 100%
- [√] 新建 `OpenAICompatible.Worker`（`madorin-provider-openai-compatible`）
- [√] Server `ProviderWorkerSupervisor` / `ProviderWorkerProcess` / `ProviderWorkerAdapter`
- [√] CLI `run` / session 与 Host Registry 对 Compatible/Custom 走监督器
- [√] 进程 hello + shutdown 单测通过
- [√] Server.csproj 已删除 Compatible 实现引用；`Create` 不再进程内构造
- [√] Worker 收 null Skills，不重建 MAF 工厂
- [√] 假 HTTP：Worker 流式 event 回到 `ProviderWorkerAdapter`（含双 connection 不串 Key）

## Step 4 补其余 9 个 Worker : 100%
- [√] 9 个 Worker 项目，Program 只映射本协议
- [√] Server 删除全部 `new XxxAdapter`；只留 `TryMapProtocolToSlug` / `IsKnownProtocol`
- [√] Server/Cli 去掉实现项目 ProjectReference 与实现命名空间 using
- [√] 生产路径（factory / Registry / doctor / wizard）一律走监督器；离线校验不拉起 Worker

## Step 5 进程回收 : 100%
- [√] 第 9 节停止顺序：shutdown → 2s → Kill → 再 Kill
- [√] Windows Job Object 托底；Unix 跟踪 PID / setpgid
- [√] 崩溃滑动窗口熔断（30s 内 ≥ 3 次）
- [√] 会话切换不杀 Worker
- [√] Dispose / Job / 熔断测试

## Step 6 串线测试 : 100%
- [√] 同一 Compatible Worker 交错两个 connection 的 invoke 不串 Key
- [√] 多 invocation 并发；Kill 后可重拉且工作区锁仍持有
- [√] 两个监督器拉起两个同 slug Worker

## Step 7 发布脚本 : 100%
- [√] `build/provider-workers.json`
- [√] 改造 `madorin-cli.publish.ps1`
- [√] 新建 linux.sh / macos.sh
- [√] 根 README 补充 Windows / Linux / macOS 调用示例

## Step 8 AOT 验收 : 100%
- [√] win-x64 发通：`build/madorin-cli.publish.ps1 -Rid win-x64 -NoBump`（Windows PowerShell；本机无 pwsh）
- [√] bin：`madorin-cli.exe` + 10 Worker + `providers.manifest.json` + `e_sqlite3.dll`（无厂商 SDK dll）
- [√] CLI ILC 不再 `--root:Microsoft.Agents.AI.OpenAI`；`AgentFrameworkRootProfile=WithoutAgentsOpenAI`
- [√] 主 exe 36.6431 → 28.19 MiB（−8.45 MiB）；zip 因 10 Worker 增至 79.28 MiB
- [√] `madorin-cli --help` / `version` / `provider protocols` 正常；Compatible 无 Key 时 doctor 明确报 `providers[0].apiKey: A value is required.`

## Step 9 文档 : 100%
- [√] 15 只补注册 / Worker 包装 / 清单 / slug
- [√] `docs/README.md` 导航到方案 12

## 本轮修改的文件

- `src/Madorin.AI.Runtime.Cli/Madorin.AI.Runtime.Cli.csproj`
- `src/Providers/Madorin.AI.Runtime.Providers.OpenAI.Worker/Madorin.AI.Runtime.Providers.OpenAI.Worker.csproj`
- `src/Madorin.AI.Runtime.Services/Skills/SkillRuntimeTools.cs`
- `src/Madorin.AI.Runtime.Services/Skills/SkillRuntimeToolExecutor.cs`
- `src/Madorin.AI.Runtime.Services/Skills/SkillRuntimeToolExecutor.Prepare.cs`
- `src/Madorin.AI.Runtime.Server/RuntimeExecutionCore.cs`
- `src/Madorin.AI.Runtime.Server/ProviderWorkers/ProviderWorkerProcess.cs`
- `src/Madorin.AI.Runtime.Server/ProviderWorkers/ProviderWorkerProcess.IO.cs`
- `src/Madorin.AI.Runtime.Server/ProviderWorkers/ProviderWorkerProcess.Lifecycle.cs`
- `src/Madorin.AI.Runtime.Server/ProviderWorkers/ProviderWorkerProcess.Reader.cs`
- `src/Madorin.AI.Runtime.Server/ProviderWorkers/ProviderWorkerSupervisor.cs`
- `src/Madorin.AI.Runtime.Server/ProviderWorkers/ProviderWorkerAdapter.cs`
- `src/Madorin.AI.Runtime.Server/ProviderWorkers/ProviderWorkerLog.cs`
- `src/Madorin.AI.Runtime.Server/LocalRuntime.cs`
- `src/Madorin.AI.Runtime.Server/RuntimeServer.cs`
- `src/Providers/Madorin.AI.Runtime.Providers.WorkerHost/ProviderWorkerFrameTooLargeException.cs`
- `src/Providers/Madorin.AI.Runtime.Providers.WorkerHost/ProviderWorkerFrameCodec.cs`
- `src/Providers/Madorin.AI.Runtime.Providers.WorkerHost/ProviderWorkerHost.Invoke.cs`
- `src/Providers/Madorin.AI.Runtime.Providers.WorkerHost/ProviderWorkerStreams.cs`
- `src/Providers/Madorin.AI.Runtime.Providers.WorkerHost/ProviderWorkerJsonContext.cs`
- `src/Madorin.AI.Runtime.Tools.Builtin/BuiltinToolConsentScopeResolver.cs`
- `src/Madorin.AI.Runtime.Cli/Commands/DoctorCommand.cs`
- `tests/Madorin.AI.Runtime.Provider.Tests/SkillRuntimeToolsTests.cs`
- `tests/Madorin.AI.Runtime.Provider.Tests/ProviderWorkerIsolationTests.cs`
- `tests/Madorin.AI.Runtime.Provider.Tests/ProviderWorkerFrameCodecTests.cs`
- `src/Madorin.AI.Runtime.Server/ProviderWorkers/ProviderWorkerCrashFuse.cs`
- `src/Madorin.AI.Runtime.Server/ProviderWorkers/ProviderWorkerProcessGroup.cs`
- `src/Madorin.AI.Runtime.Server/ProviderWorkers/ProviderWorkerProcessGroup.Windows.cs`
- `src/Madorin.AI.Runtime.Server/ProviderWorkers/ProviderWorkerProcessGroup.Unix.cs`
- `src/Madorin.AI.Runtime.Server/ProviderWorkers/ProviderWorkerProcess.cs`
- `src/Madorin.AI.Runtime.Server/ProviderWorkers/ProviderWorkerProcess.Lifecycle.cs`
- `src/Madorin.AI.Runtime.Server/ProviderWorkers/ProviderWorkerProcess.Reader.cs`
- `src/Madorin.AI.Runtime.Server/ProviderWorkers/ProviderWorkerProcess.IO.cs`
- `src/Madorin.AI.Runtime.Server/ProviderWorkers/ProviderWorkerSupervisor.cs`
- `src/Madorin.AI.Runtime.Server/Madorin.AI.Runtime.Server.csproj`
- `tests/Madorin.AI.Runtime.Provider.Tests/ProviderWorkerCrashFuseTests.cs`
- `tests/Madorin.AI.Runtime.Provider.Tests/ProviderWorkerLifecycleTests.cs`
- `tests/Madorin.AI.Runtime.Provider.Tests/ProviderWorkerIsolationTests.cs`
- `tests/Madorin.AI.Runtime.Provider.Tests/ProviderWorkersManifestTests.cs`
- `tests/Madorin.AI.Runtime.Provider.Tests/Madorin.AI.Runtime.Provider.Tests.csproj`
- `build/provider-workers.json`
- `build/madorin-cli.publish.ps1`
- `build/madorin-cli.publish.linux.sh`
- `build/madorin-cli.publish.macos.sh`
- `README.md`
- `docs/开发文档/15-Provider适配器开发指南.md`
- `docs/执行计划(Provider进程隔离AOT按需加载).md`
