# Madorin AI Runtime SampleHost

SampleHost 是只通过公开 Client SDK 和 Contracts 集成 Madorin AI Runtime 的参考宿主，不引用 Provider、MAF、MEAI、SQLite 或 Server 实现项目。当前提供 `multi-instance` 和 `reference-host` 两个独立入口。

## `multi-instance` 入口

`multi-instance` 保留阶段 2 的进程隔离示例：单个宿主启动、认证并管理两个绑定不同工作区的 Runtime 子进程，支持按实例重连和独立关闭。

每个 Runtime 使用独立实例 ID、Pipe 前缀和握手 secret。两个 secret 由调用方通过受控 stdin 逐行传入，宿主再通过子进程 stdin 转交；secret 不进入命令行、环境变量或 stdout。

## `reference-host` 入口

`reference-host` 是阶段 7 的完整业务集成示例。调用方提供三个工作区、三个 Runtime 实例 ID 和三个 Pipe 端点，三个 handshake secret 继续通过 stdin 逐行传入。宿主随后：

- 创建三个独立 `RuntimeClient`，在三个 Runtime 上并发运行 Expert 会话；不再依赖已移除的 Meeting/Work 模式。
- 声明目录、资源、脚本和热刷新 Skills 能力；工作区存在 `.madorin/host-skills/` 时，将其作为有序补充来源传入。Runtime 仍先加载绑定工作区和 Runtime OS 用户全局来源。
- 通过 Client SDK 查询并刷新每个 Runtime 的 Skills Catalog，输出实例、刷新前后版本、有效数量和实际来源 ID。
- 按 `runtimeInstanceId`、`runId`、GSN 和 Run 序号校验、分流事件，并在处理成功后显式确认 GSN。
- 发布工具目录，处理权限、审批、宿主/MCP 工具回调，并按 callId 查询持久结果。
- 演示 Run 取消、Selection 更新、Session resume/rehydrate、Runtime 重启和继续同一 Session。
- 只关闭自身三个 Client 句柄，不枚举或终止其他 Runtime 实例。

端到端测试负责启动三个使用确定性 Provider 的真实 Runtime，并在恢复阶段重启 Expert Runtime。宿主不会读取个人 `~/.madorin/config.json`，也不会把测试 secret 写入参数、环境变量、stdout 或 stderr。

## 构建与测试

在 `Src/Madorin.Ai.Runtime` 目录执行：

```powershell
dotnet build .\Madorin.AI.Runtime.slnx -c Debug
dotnet test .\tests\Madorin.AI.Runtime.EndToEnd.Tests\Madorin.AI.Runtime.EndToEnd.Tests.csproj `
  -c Debug `
  --filter "ClassName=Madorin.AI.Runtime.EndToEnd.Tests.ReferenceHostProcessTests"
```

阶段 7A 正在执行。当前协议版本为 `1.1`，SQLite Schema 版本为 `16`。每个 Client 拥有独立的 `State`/`Completion` 生命周期；单实例失败保留最后快照并标记为 `WAITING`，其他实例继续更新。恢复时先恢复事件游标再查询 Run，不伪造业务终态。
