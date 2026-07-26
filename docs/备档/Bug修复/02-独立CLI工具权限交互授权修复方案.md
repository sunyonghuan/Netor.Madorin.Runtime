# 独立 CLI 工具权限交互授权与审计闭环修复方案

> 文档状态：已实施并归档（2026-07-26）
>
> 适用范围：`Madorin.AI.Runtime.Cli` 独立启动、`LocalRuntime` 本地执行、Expert / Meeting / Work 三种模式
>
> 不影响范围：由宿主通过 Reverse RPC 执行权限审核与用户审批的 `serve` / Client SDK 路径
>
> 编制日期：2026-07-26

> 实施结果：本方案已完成。实际落地位置以 `Madorin.AI.Runtime.Cli/Tools`、`Services.Tools`、`RuntimeExecutionCore`、`LocalRuntime` 和 SQLite Schema v15 为准；测试与发布证据见同目录的[执行记录](./02-独立CLI工具权限交互授权执行记录.md)。

## 1. 结论与问题定性

修复前结论：**独立启动路径没有实现工具权限与审批的交互处理器**。修复前测试中没有出现授权提示、工具直接返回拒绝，并不是终端漏显示了已有提示，而是权限请求根本没有被连接到 CLI 终端。

修复前行为仍然遵循 fail closed 原则，因此不存在“未授权却执行”的安全倒退；问题在于独立 CLI 缺少一个合法的授权权威，导致所有需要动态 Grant 或明确审批的工具都只能失败。当前实现结果见文档顶部实施结果和第 17 节验收清单。

### 1.1 代码证据

| 位置 | 当前实现 | 直接影响 |
| --- | --- | --- |
| `src/Madorin.AI.Runtime.Cli/Commands/ServiceCommands.cs:424` | 创建 `LocalRuntimeOptions` 时只注入 Memory 服务 | CLI 没有把权限或审批处理器交给 Runtime |
| `src/Madorin.AI.Runtime.Server/LocalRuntimeOptions.cs:7` | 选项只包含工作区、数据目录、Provider、Memory 和限制 | 公共本地启动入口没有 Consent 注入点 |
| `src/Madorin.AI.Runtime.Server/LocalRuntime.cs:834`、`:925` | 新 Session 和已有 Session 都向执行核心传入 `controlPeer: null` | 本地路径不会创建 Reverse RPC 权限通道 |
| `src/Madorin.AI.Runtime.Server/RuntimeExecutionCore.cs:88` | 仅在 `controlPeer` 非空时创建 `ReverseRpcToolExecutor` | 权限处理器来源被错误地绑定在远程执行器是否存在上 |
| `src/Madorin.AI.Runtime.Server/RuntimeExecutionCore.cs:122` | `ToolConsentCoordinator` 的两个 handler 都来自 `reverseExecutor` | 独立模式下 permission / approval handler 同时为 `null` |
| `src/Madorin.AI.Runtime.Services/Tools/ToolConsentCoordinator.cs:74`、`:176` | handler 为空时直接返回 `ToolApprovalRequired` / `ToolPermissionRequired` | 不会询问用户，模型只能收到拒绝结果 |
| `src/Madorin.AI.Runtime.Services/Tools/ToolGateway.cs:464` | 授权判断前已创建 `Pending ToolIntent` | 拒绝路径如果不终结 Intent，会留下不完整状态 |
| `src/Madorin.AI.Runtime.Services/Tools/ToolGateway.cs:867` | 高风险审计记录在授权且 `Prepare` 成功后才创建 | 无处理器或用户拒绝没有完整的执行前审计闭环 |

### 1.2 当前调用链

```text
CLI ServiceCommands
  -> LocalRuntime.StartAsync
  -> LocalRuntime.RunAsync
  -> RuntimeExecutionCore.RunAsync(controlPeer: null)
  -> reverseExecutor = null
  -> ToolConsentCoordinator(permissionHandler: null, approvalHandler: null)
  -> ToolGateway 返回 NeedsPermission / NeedsApproval
  -> ToolConsentCoordinator 直接返回 Required 错误
  -> 模型收到工具失败结果，终端没有授权提示
```

### 1.3 当前默认权限表现

Restricted 策略下，根 Agent 通常可以直接使用工作区读取、列出、搜索、内容读取以及不覆盖已有目标的新建操作。下列操作需要额外 Grant 或明确审批，当前独立 CLI 无法完成：

- 覆盖、移动、删除和其他 destructive 文件操作。
- `builtin.memory.append`。
- `builtin.process.run`。
- `builtin.powershell.run`。
- `builtin.http.request`。
- Work 模式子 Agent 未获得父级可委派 Grant 时的受控工具调用。

因此，“读取成功、写入或执行被直接拒绝”与现有代码完全一致。

## 2. 两类 Consent 的职责边界

本方案统一使用 Consent 表示用户或宿主对工具调用的安全决定，但保留现有两类协议含义。

| 类型 | 触发时机 | 现有请求/响应 | 返回 Granted 时必须包含 |
| --- | --- | --- | --- |
| 权限 Grant | 当前 Run / Agent / Call 缺少执行该工具所需的有效权限 | `ToolPermissionRequest` / `ToolPermissionResponse` | 收窄后的 `ToolGrant` |
| 明确审批 | 工具描述符要求审批，或调用风险必须由用户确认 | `ApprovalRequest` / `ApprovalResponse` | 绑定当前审批请求的 `ToolGrant` |

两者不是同一个开关：

1. Grant 回答“该 Agent 在什么范围内可以调用什么工具”。
2. Approval 回答“这一次高风险动作是否由用户明确同意”。
3. 一个调用可能先缺权限，保存 Grant 后再次校验时仍需要明确审批。
4. CLI 必须支持有界的两阶段 Consent，不能只处理第一次 `NeedsPermission` 就假定工具一定执行。

宿主模式已有两个 Reverse RPC 回调，权限权威属于宿主。独立模式没有宿主，权限权威必须由本地 CLI 用户承担，但 Runtime 安全策略仍然具有最终否决权，CLI 只能发放更窄的 Grant，不能绕过 `ToolAuthorizationService`。

## 3. 修复目标

1. 独立 CLI 在真实交互终端中遇到权限或审批请求时，显示可理解、可追溯且不泄密的授权提示。
2. 默认选择只授权当前调用；只有能安全表达最小作用域的工具才允许授权到当前 Run。
3. `LocalRuntime` 提供正式的 Consent 注入点，不再把权限处理能力绑定到 `controlPeer`。
4. 宿主 Reverse RPC 与本地 CLI 两种权限权威互斥，避免同一次 Run 出现多个授权来源。
5. 权限请求、用户决定、Grant、执行前审计和执行结果形成可查询闭环。
6. 用户拒绝、无处理器、非法响应、无效 Grant、取消和异常都终结对应 ToolIntent，不遗留 `Pending`。
7. Expert、Meeting、Work 三种模式使用同一套 Consent 状态机；Work 模式保留 Agent、父 Agent、根 Grant 和委派链。
8. JSON / JSONL、重定向、输出文件和其他非交互场景继续 fail closed，不向机器输出混入提示文字。
9. 保持现有 `ToolPermissionRequest/Response`、`ApprovalRequest/Response` 和 `ToolGrant` 公开协议兼容。

## 4. 非目标

- 不允许通过 `--yes`、配置文件或环境变量静默批准模型发起的工具调用。
- 不实现永久全局授权，也不跨 Run 复用本地交互产生的 Grant。
- 不放开工作区外路径、任意可执行程序、任意网络目标或无限期权限。
- 不把 PowerShell 的工作目录误认为操作系统沙箱；本方案只控制 Runtime 授权，不提升进程隔离等级。
- 不在提示中默认输出完整参数 JSON、脚本、文件内容、Prompt、HTTP Header、Token 或密钥。
- 不改变宿主 Reverse RPC 消息格式，不让 CLI 参与宿主模式的审批决策。
- 不在 `ServiceCommands` 内直接堆积参数解析、作用域计算和 Grant 构造逻辑。

## 5. 目标行为契约

### 5.1 启动与输出模式矩阵

| 场景 | 是否允许提示 | 行为 |
| --- | --- | --- |
| 裸启动进入 REPL，stdin/stdout 都连接真实终端，文本输出 | 是 | 显示 Consent 提示并等待用户选择 |
| `run --input ...`，输入输出仍为真实终端且为文本输出 | 是 | 单次 Run 内可以询问，决定后继续或拒绝 |
| stdin 重定向或管道输入 | 否 | 不读取额外输入，安全拒绝并终结 Intent |
| stdout 重定向、注入文件 Writer 或指定输出文件 | 否 | 不混入提示，安全拒绝 |
| `--json`、JSONL 或其他机器输出 | 否 | 返回既有结构化工具失败/Run 事件，不写人类提示 |
| 测试注入 Fake Terminal | 显式决定 | 只有 Fake 声明可交互时才允许读取 Consent 输入 |
| `--yes` | 否 | 仅维持其现有 CLI 命令确认语义，不批准模型工具 |

“是否可交互”必须在创建本地 Consent Handler 时一次确定，不能等到权限请求发生后再直接读取 `Console.In`。

### 5.2 权限阶段决策选项

权限 Grant 阶段使用纯数字选项，避免字母 `O` 与数字 `0`、大小写及本地化文本产生歧义：

| 输入 | 选项 | 含义 | 默认可用性 |
| --- | --- | --- | --- |
| `1` | `Deny` | 拒绝当前调用，不创建 Grant | 始终可用，且为默认值 |
| `2` | `Allow once` | Grant 绑定当前 `RunId + AgentId + ToolId + CallId` | 始终可用，是首选授权范围 |
| `3` | `Allow for run` | Grant 绑定当前 Run、Agent、Tool 和可安全表达的固定作用域 | 仅由作用域策略显式允许时显示 |

输入为空时选择 `Allow once` 并不安全，容易因误按回车执行高风险操作。因此空输入等同 `1`，默认必须是 `Deny`；界面可以把 `Allow once` 标记为推荐的授权范围，但用户必须明确输入 `2`。

### 5.3 明确审批阶段决策选项

权限阶段完成后，如果工具仍然要求明确审批，使用二选一的 `Y/N` 输入：

| 输入 | 决定 |
| --- | --- |
| `Y` / `y` | 批准当前调用 |
| `N` / `n` | 拒绝当前调用 |
| 空输入 | 等同 `N`，拒绝当前调用 |

审批输入不区分大小写。审批只针对当前 Call，不再提供 Run 级批准；如果调用没有先经过权限阶段，输入 `Y/y` 时也只能生成绑定当前 CallId 的单次 Grant。

### 5.4 不可交互时的错误语义

非交互场景不应伪装成用户主动拒绝。返回原因至少区分：

- 当前运行不具备交互式 Consent 通道。
- 用户明确拒绝。
- Consent 输入被取消。
- Runtime 收到无效或不匹配的响应。
- 返回的 Grant 无法通过最小权限校验。

第一阶段可以继续复用现有 `ToolPermissionRequired` / `ToolApprovalRequired` 稳定错误码，通过安全的 `Reason` 和持久化 decision 区分原因，避免无必要地扩大公开协议变更。

## 6. 架构修改方案

### 6.1 引入统一的 Runtime Consent Handler

在 `Madorin.AI.Runtime.Services.Tools` 增加统一处理器抽象，避免 `RuntimeExecutionCore` 继续分别持有两个可空委托：

```csharp
public interface IToolConsentHandler
{
    Task<ToolPermissionResponse> RequestPermissionAsync(
        ToolPermissionConsentContext context,
        CancellationToken ct);

    Task<ApprovalResponse> RequestApprovalAsync(
        ToolApprovalConsentContext context,
        CancellationToken ct);
}
```

两个 Context 是 Runtime 内部使用的富上下文，包含：

```text
现有协议 Request
ToolInvocation（含 Run / Session / Agent / ParentAgent / Call 链）
ToolDescriptor（风险、能力、RequiresApproval）
规范化且已收窄的 ToolRequestedScope
WorkspaceRoot
```

现有 Reverse RPC 仍只发送原有公开 DTO。增加一个适配器把 `IToolConsentHandler` 调用转发给 `ReverseRpcToolExecutor.RequestPermissionAsync` 和 `RequestApprovalAsync`，忽略仅供进程内使用的富上下文字段。这样不会改变协议或宿主实现。

`ToolConsentCoordinator` 当前公开的委托构造方式如果已被测试或外部代码使用，应保留兼容构造函数，并在内部包装成 `IToolConsentHandler`；新代码统一走接口。

### 6.2 为什么不能只给 CLI 现有 DTO

现有 `ToolPermissionRequest.RequestedScope` 在协调器中固定为 `"call"`，`ApprovalRequest.TargetSummary` 只是 `local:<toolId>`。这两个值不足以回答：

- 文件调用真正访问的规范化源路径、目标路径和父目录。
- `process.run` 请求的 executable。
- `http.request` 请求的 scheme、host 和 port。
- 是否只请求 overwrite、move 或 delete 中的某一个能力。
- Work 子 Agent 当前应绑定的父 Grant 和委派链。

如果 CLI 只根据 `ToolId` 构造 Grant，只能选择“权限不足导致仍然失败”或“发放过宽权限”两种错误结果。因此本地处理器必须获得 Runtime 已解析的最小作用域；该作用域不能靠 CLI 从显示字符串或错误消息中反向解析。

### 6.3 增加内置工具作用域解析器

在 `Madorin.AI.Runtime.Tools.Builtin` 增加 `BuiltinToolConsentScopeResolver`，与内置工具参数 Schema 和路径策略放在同一所有权边界。它只做无副作用解析和规范化，不读取目标内容、不打开网络连接、不启动进程。

`ToolRequestedScope` 放在 `Madorin.AI.Runtime.Tools.Abstractions`，Builtin 只负责实现解析器，Services 和 CLI 只依赖抽象，避免 Services 反向依赖 Builtin。建议结构：

```csharp
public sealed record ToolRequestedScope(
    string TargetSummary,
    string[] AllowedReadRoots,
    string[] AllowedWriteRoots,
    bool AllowOverwrite,
    bool AllowMove,
    bool AllowDelete,
    string[] AllowedExecutables,
    bool AllowPowerShell,
    NetworkPolicy NetworkPolicy,
    bool CanGrantForRun);
```

解析器必须复用内置工具已有的参数 DTO、路径规范化和网络校验帮助方法；如果现有方法封装在 executor 私有成员中，应提取小型共享组件，不能复制一套路径或 URL 安全规则。

解析失败、工具未知、作用域无法可靠表达时，返回结构化失败并禁止授权。不得回退为工作区根、任意 executable、`DenyAll = false` 或通配 host。

### 6.4 `LocalRuntimeOptions` 注入点

为 `LocalRuntimeOptions` 增加：

```csharp
public IToolConsentHandler? ToolConsentHandler { get; init; }
```

`LocalRuntime` 保存该处理器，并在新 Session、已有 Session以及后续新增的恢复执行入口中一致传给 `RuntimeExecutionCore.RunAsync`。不能只修复 `RunNewSessionAsync`，否则恢复会话仍会直接拒绝。

`RuntimeExecutionCore.RunAsync` 增加可空的本地 handler 参数，按下列优先级解析唯一权限权威：

```text
controlPeer != null + local handler == null
  -> ReverseRpcToolConsentHandler

controlPeer == null + local handler != null
  -> Local CLI ToolConsentHandler

controlPeer == null + local handler == null
  -> 无 Consent 能力，fail closed

controlPeer != null + local handler != null
  -> 配置错误，Run 开始前抛出明确异常
```

不得使用“本地 handler 优先、失败后回退 Reverse RPC”或相反的隐式回退，否则同一调用的权限权威和审计来源不确定。

### 6.5 CLI 组件拆分

在 CLI 中新增独立安全组件，`ServiceCommands` 只负责组合：

| 组件 | 职责 |
| --- | --- |
| `StandaloneToolConsentHandler` | 串行化提示、显示安全摘要、读取决定、返回现有协议响应 |
| `StandaloneToolGrantFactory` | 从规范化 `ToolRequestedScope` 构造最小 `ToolGrant` |
| `StandaloneToolConsentPolicy` | 判断当前输出是否可交互，以及 `Allow for run` 是否可用 |

建议放入 `src/Madorin.AI.Runtime.Cli/Security/`。实施前按仓库规则检查该目录及其父目录 README；若没有目录则先检查目标父目录，再创建文件。

`ServiceCommands` 在 `LocalRuntime.StartAsync` 前完成以下组合：

1. 根据终端、输出格式、重定向和输出文件计算交互策略。
2. 只有策略允许时创建 `StandaloneToolConsentHandler`。
3. 将 handler 赋给 `LocalRuntimeOptions.ToolConsentHandler`。
4. 非交互时不创建假批准 handler，让 Runtime 明确走无 Consent 的 fail-closed 分支。

### 6.6 终端交互能力

为当前正在扩展的 `ICliTerminal` 增加只读能力：

```csharp
bool IsInteractive => false;
```

接口默认返回 `false`，保证第三方或测试实现升级后不会意外获得授权能力。生产 `CliTerminal` 只有同时满足下列条件才返回 `true`：

- 输入是 `Console.In` 且输出是 `Console.Out`。
- `Console.IsInputRedirected == false`。
- `Console.IsOutputRedirected == false`。

`IsInteractive` 只表示终端能力；最终策略还必须叠加文本输出、无输出文件等 CLI 条件。现有 `CliTextStyle` 继续用于提示样式，不新增终端 UI 依赖。

## 7. 交互提示设计

### 7.1 显示字段

提示必须包含足够的决策信息，但只显示经过安全整理的值：

```text
Tool permission required

Agent      worker-1
Parent     manager-1
Tool       builtin.process.run
Risk       Process
Target     dotnet (workspace: E:\Projects\Demo)
Reason     The tool requires a Grant.
Arguments  sha256:0123abcd...
Scope      Current call only

1. Deny
2. Allow once
3. Allow for run
Decision [1]:
```

如果重新校验后还需要明确审批，显示二选一提示：

```text
Explicit approval required

Tool       builtin.process.run
Risk       Process
Target     dotnet
Arguments  sha256:0123abcd...

Approve this call? [y/N]:
```

显示规则：

- Tool、Agent、父 Agent、风险、原因、目标摘要和参数哈希必须显示。
- `ArgumentsHash` 可以缩短显示，但持久化时保留完整值。
- 文件路径允许显示规范化目标；用户主目录之外的路径仍须经过工作区/Grant 校验。
- HTTP 只显示 scheme、host、port 和必要路径摘要，不显示 userinfo、query、fragment、Header 或 body。
- Process 显示 executable 和参数数量，不默认显示完整参数或环境变量值。
- PowerShell 显示脚本哈希、字符数和工作目录，不显示完整脚本。
- Memory append 显示目标层级和内容哈希/长度，不显示完整记忆内容。
- 输出采用当前 `CliTextStyle.Warning`、`Parameter`、`Muted`、`Error`，`--no-color` 和非彩色规则保持生效。

### 7.2 输入规则

- 权限阶段只接受 `1`、`2`、`3`，不再接受 `D/O/R` 等字母快捷键。
- 权限阶段空输入等同 `1`，即 `Deny`。
- 明确审批阶段只接受 `Y/y` 和 `N/n`，不区分大小写。
- 明确审批阶段空输入等同 `N`，即拒绝。
- EOF 等同非交互拒绝，不循环读取。
- 非法输入最多重试 3 次，之后拒绝，避免自动化环境永久挂起。
- 输入 `3` 但该作用域不允许 Run 授权时，显示一次说明并要求重新选择；不得静默降级为更宽或更窄的其他 Grant。
- Ctrl+C 取消当前授权等待并传播到当前 Run，不保存 Granted 决定。

### 7.3 并发与终端所有权

`StandaloneToolConsentHandler` 内部使用一个 `SemaphoreSlim` 串行所有提示。Meeting 或 Work 模式即使并发产生多个工具请求，也只能一次显示一个完整提示并读取一个决定。

REPL 主输入与 Consent 输入不能并发读取。同一 Run 枚举期间，Consent Handler 临时拥有终端输入；Run 返回或失败后，REPL 才重新显示普通提示。提示等待必须使用 Run 的取消令牌，不能创建无法被 Ctrl+C 取消的独立读取任务。

## 8. Grant 最小化规则

### 8.1 所有本地 Grant 的共同约束

每个本地交互 Grant 都必须满足：

- `RunId` 与当前调用完全一致。
- `WorkspaceRoot` 使用 Runtime 已固定并规范化的工作区。
- `AgentId` 为实际调用 Agent，不只记录外层 Manager。
- `AllowedToolIds` 只包含当前工具。
- `MaximumRisk` 不高于当前工具描述符风险。
- `ExpiresAt` 不晚于当前 Run 的生命周期；若无法得到 Run 截止时间，使用短时有效期并在 Run 结束后不再复用。
- 默认 `AllowDelegation = false`、`DelegatedAgentIds = []`。
- `NetworkPolicy` 默认 `DenyAll = true`。
- 未请求的 read/write/overwrite/move/delete/process/PowerShell/network 能力全部关闭。
- Grant 在保存前继续经过 `ToolAuthorizationService.SaveGrantAsync` 的子集和委派校验。

### 8.2 `Allow once`

`Allow once` 额外设置：

```text
AllowedCallIds = [当前 CallId]
ApprovalRequestId = 当前 permission/approval request id
```

即使作用域结构由于 `ToolGrant` 粒度限制必须包含一个目录或一个 executable，`AllowedCallIds` 也会阻止其他调用复用该 Grant。

### 8.3 `Allow for run`

只有 `ToolRequestedScope.CanGrantForRun == true` 时可用，并设置：

```text
AllowedCallIds = null 或空
AllowedToolIds = [当前 ToolId]
AgentId = 当前 AgentId
作用域 = 当前请求已规范化的固定目标范围
```

下列调用默认不提供 `Allow for run`：

- `builtin.memory.append`。
- 删除、移动、覆盖和补丁等 destructive 文件操作。
- `builtin.powershell.run`。
- 作用域包含凭据、动态目标、无法稳定规范化路径或未知工具的调用。

### 8.4 各工具作用域

| 工具类型 | Grant 内容 | `Allow for run` |
| --- | --- | --- |
| 工作区文件写入/建目录 | 只加入必要的规范化父目录；overwrite 标志仅按本次参数开启 | 仅无覆盖、固定目录且策略允许 |
| copy/move/delete/patch | 同时解析源、目标和父目录；只开启实际操作标志 | 否 |
| `builtin.memory.append` | 只允许当前 ToolId + CallId，不扩大文件根 | 否 |
| `builtin.process.run` | `AllowedExecutables` 只包含规范化后的请求 executable；环境变量继续用白名单 | 默认仅 once；后续可对明确 allowlist 开放 run |
| `builtin.powershell.run` | `AllowPowerShell = true`，同时绑定当前 CallId | 否 |
| `builtin.http.request` | `AllowedSchemes/Hosts/Ports` 只包含请求 URL；保留响应字节和重定向上限 | 默认 once；固定公开目标且策略明确时才允许 run |
| 未知/宿主/MCP 工具 | 本地作用域解析器不发 Grant | 否；宿主模式仍走 Reverse RPC |

文件路径、URL 和 executable 的原始值只用于显示或执行，权限判断使用规范化副本；不能把为了权限校验产生的规范化值覆盖回模型参数。

## 9. Work 子 Agent 与委派

Work 子 Agent 默认不继承 Manager 的本地交互 Grant。当前调用由子 Agent 发起时：

1. `AgentId` 必须是子 Agent，`ParentAgentId` 必须保留 Manager/父 Agent。
2. `Allow once` 直接为该子 Agent 和 Call 创建叶子 Grant，不必扩大父 Grant。
3. 如果必须基于已有父 Grant 委派，`ParentGrantId`、`RootGrantId` 和 `DelegationChain` 必须来自已持久化父 Grant，不能由 CLI 根据 Agent 名称伪造。
4. 委派作用域是父 Grant、当前请求作用域、Runtime Restricted 策略和当前工具目录的交集。
5. 本地 CLI 第一阶段不提供“允许此 Manager 的所有子 Agent”选项；`DelegatedAgentIds` 默认只包含明确目标，且 `AllowDelegation` 默认关闭。
6. 用户拒绝后，Work Step 必须从 waiting 状态恢复为可终结状态并记录失败，不能永久停留在 `WaitingForApproval`。

## 10. Consent 状态机

### 10.1 有界解析循环

将 `ToolConsentCoordinator.ResolveAndExecuteAsync` 从“处理一次 Consent 后直接返回”调整为有界状态机：

```text
ToolGateway.Execute
  -> Success / Failed / Cancelled
  -> NeedsPermission
       -> 持久化 pending permission
       -> 请求唯一 Consent Handler
       -> Denied: 终结 Intent(Failed)
       -> Granted: 校验并保存最小 Grant
       -> 再次 ToolGateway.Execute
  -> NeedsApproval
       -> 持久化 pending approval
       -> 请求唯一 Consent Handler
       -> Denied: 终结 Intent(Failed)
       -> Granted: 绑定 ApprovalRequestId、保存 Grant
       -> 再次 ToolGateway.Execute
```

循环规则：

- 最多处理一个 permission 和一个 approval 阶段。
- 使用 deterministic consent request id 去重；同一种 request id 再次出现视为 Grant 无效或状态机无进展，立即失败并终结 Intent。
- 每次重新执行仍必须完整经过 `ToolAuthorizationService.AuthorizeAsync`，不能因为用户选择 Granted 就直接调用 executor。
- Handler 返回的 `CorrelationId`、`CallId` 或 `ApprovalRequestId` 与请求不一致时 fail closed。
- Granted 但未提供 Grant、Grant 无法保存或保存后仍不覆盖所需作用域时 fail closed。

### 10.2 终态与取消

| 情况 | ToolIntent 终态 | Grant | Consent decision |
| --- | --- | --- | --- |
| 用户拒绝 | `Failed` | 无 | `denied` |
| 无本地/宿主 handler | `Failed` | 无 | `unavailable` |
| 非交互 CLI | `Failed` | 无 | `unavailable` |
| 输入 EOF/非法输入耗尽 | `Failed` | 无 | `denied` |
| Ctrl+C / Run 取消 | `Cancelled` | 无 | `cancelled` |
| 响应关联不匹配 | `Failed` | 无 | `invalid-response` |
| Grant 无效 | `Failed` | 不保存或已拒绝 | `invalid-grant` |
| 授权后 Prepare 失败 | `Failed` | 已保存、仍受 Run/过期约束 | `granted` |
| 工具执行成功 | `Succeeded` | 已保存 | `granted` |
| 工具执行失败 | `Failed` | 已保存 | `granted` |

取消路径使用 `CancellationToken.None` 或受控的短期持久化令牌完成 Intent 和 decision 后再重新抛出取消，避免原 Run 令牌已取消导致清理写入也被跳过。

## 11. 审计闭环

### 11.1 当前缺口

当前 ToolIntent 在授权前创建为 `Pending`，但 `ToolConsentCoordinator` 的无 handler、拒绝和非法响应分支只返回内存中的失败结果。`ToolAuditRecord` 又只针对 destructive 风险，并且在 executor `PrepareAsync` 成功后写入 `prepared` 状态。因此：

- 被直接拒绝的调用可能长期保留 `Pending ToolIntent`。
- permission 请求不像 approval 请求那样持久化 pending/decision。
- 无法可靠区分用户拒绝、没有处理器和无效响应。
- Work Step 可能受到悬空 Intent 或 pending approval 状态影响。

### 11.2 复用现有持久化模型

不新增另一套审计数据库。复用现有四类记录，但补齐语义：

| 记录 | 负责内容 |
| --- | --- |
| `tool_intents` | 每次调用的幂等身份、Pending/Sent/终态、错误码和完成时间 |
| `tool_approvals` | 作为兼容保留的 Consent decision ledger，同时记录 permission 和 approval 请求/决定 |
| `tool_grants` | Granted 后的最小权限、Agent、父/根 Grant、委派链、过期和撤销 |
| `tool_audit` | 现有高风险调用在 Prepare/执行后的目标摘要、Grant、结果哈希和诊断信息 |

为 `ToolApprovalState` 和 `tool_approvals` 增加兼容字段：

```text
request_kind: permission | approval
```

旧数据和旧构造调用默认 `approval`。permission 分支采用与 approval 分支相同的两次写入：提示前保存 `pending`，决定后更新为 `granted/denied/unavailable/cancelled/invalid-*`。request id 已有 `permission-...` / `approval-...` 前缀，但不能只靠字符串前缀承担结构化查询。

permission 产生的 Grant 也要把 consent request id 写入现有 `ApprovalRequestId` 兼容字段，使 `tool_intents`、`tool_grants` 和 `tool_audit` 可以沿同一键关联。该字段名称暂不修改，避免扩大协议和数据库迁移范围；文档应明确它在持久化层代表通用 Consent Request ID。

### 11.3 ToolIntent 终结 API

在 `ToolGateway` 提取一个同程序集可用的终结入口，例如：

```csharp
internal Task<ToolGatewayResult> CompletePendingConsentAsync(
    ToolInvocation invocation,
    ToolIntentStatus status,
    string errorCode,
    string reason,
    CancellationToken ct);
```

该入口复用现有 `CompleteBeforeExecutionAsync` 的事务、结果哈希、outbox 事件和幂等竞争处理，不允许 `ToolConsentCoordinator` 直接操作 SQLite 仓储实现。

要求：

- 只允许从 `Pending` 转到 `Failed` 或 `Cancelled`。
- 如果并发方已经把 Intent 变为终态，返回已有结果，不覆盖成功结果。
- 失败时写入安全错误原因和 `CompletedAt`。
- 不把完整参数或用户输入写入错误消息。
- no-handler、Denied、invalid-response、invalid-grant 和 handler 异常都必须调用该入口。

### 11.4 完整审计链

成功路径：

```text
ToolIntent(Pending)
  -> Consent(pending)
  -> Consent(granted, grantId)
  -> ToolGrant(active)
  -> ToolAudit(prepared，高风险工具)
  -> ToolIntent(Sent)
  -> ToolAudit(succeeded/failed)
  -> ToolIntent(Succeeded/Failed)
```

拒绝路径：

```text
ToolIntent(Pending)
  -> Consent(pending)
  -> Consent(denied/unavailable/cancelled)
  -> ToolIntent(Failed/Cancelled)
```

拒绝路径不伪造 `prepared` ToolAudit，因为工具并未完成 Prepare，也没有有效 Grant；可审计性由 Consent ledger 与终态 ToolIntent 提供。

## 12. 预计代码修改范围

### 12.1 生产代码

| 文件 | 计划修改 |
| --- | --- |
| `src/Madorin.AI.Runtime.Tools.Abstractions/ToolRequestedScope.cs` | 新增与具体 executor 解耦的规范化最小作用域模型 |
| `src/Madorin.AI.Runtime.Services/Tools/IToolConsentHandler.cs` | 新增统一 handler 与 permission/approval 富上下文 |
| `src/Madorin.AI.Runtime.Services/Tools/ToolConsentCoordinator.cs` | 使用统一 handler；支持有界 permission + approval；持久化两类 decision；所有失败分支终结 Intent |
| `src/Madorin.AI.Runtime.Services/Tools/ToolGateway.cs` | 提取幂等的 Pending Consent 终结入口，复用现有事务和 outbox 行为 |
| `src/Madorin.AI.Runtime.Tools.Builtin/BuiltinToolConsentScopeResolver.cs` | 无副作用解析内置工具参数并产生最小 `ToolRequestedScope` |
| `src/Madorin.AI.Runtime.Server/LocalRuntimeOptions.cs` | 增加 `ToolConsentHandler` 注入点 |
| `src/Madorin.AI.Runtime.Server/LocalRuntime.cs` | 保存并向所有执行入口传递本地 handler |
| `src/Madorin.AI.Runtime.Server/RuntimeExecutionCore.cs` | 解析唯一 handler；适配 Reverse RPC；禁止双权限权威 |
| `src/Madorin.AI.Runtime.Cli/Config/CliTerminal.cs` | 增加默认 fail-closed 的 `IsInteractive`，复用现有样式输出 |
| `src/Madorin.AI.Runtime.Cli/Security/StandaloneToolConsentHandler.cs` | 新增终端提示、输入、并发串行和响应构造 |
| `src/Madorin.AI.Runtime.Cli/Security/StandaloneToolGrantFactory.cs` | 按 once/run 与规范化作用域创建最小 Grant |
| `src/Madorin.AI.Runtime.Cli/Security/StandaloneToolConsentPolicy.cs` | 集中判断机器输出、重定向和可交互条件 |
| `src/Madorin.AI.Runtime.Cli/Commands/ServiceCommands.cs` | 只做策略和 handler 组合，注入 `LocalRuntimeOptions` |
| `src/Madorin.AI.Runtime.Persistence.Abstractions/ToolApprovalState.cs` | 增加兼容的 `RequestKind`，默认 `approval` |
| `src/Madorin.AI.Runtime.Persistence.Sqlite/SqliteSchema.cs` | 为 `tool_approvals` 增加 `request_kind` 迁移与 Schema 校验 |
| `src/Madorin.AI.Runtime.Persistence.Sqlite/SqliteToolIntentRepository.cs` | 读写 `request_kind`，保持旧库升级兼容 |

实际实施时，新增/移动/修改文件前必须再次检查各受影响目录 README，并对工作树已有修改做局部补丁。尤其 `ServiceCommands.cs`、`CliTerminal.cs` 当前已有未提交的 CLI 交互改动，不能覆盖或回退。

### 12.2 原则上不修改

| 文件/项目 | 原因 |
| --- | --- |
| `ToolPermissionRequest/Response`、`ApprovalRequest/Response` | 现有 Reverse RPC 公开协议足够，富上下文只在 Runtime 进程内传递 |
| `ToolGrant`、`NetworkPolicy` | 已具有 Tool、Call、Agent、父/根 Grant、委派链和网络收窄字段 |
| `Madorin.AI.Runtime.Client` | 宿主模式继续使用现有回调和 Reverse RPC |
| Provider Adapter | Provider 只发起工具调用，不应决定本地用户授权 |
| Memory 文件格式 | `builtin.memory.append` 只修复 Consent，不改变 Memory 注入与持久化语义 |

如果作用域解析无法复用内置 executor 的现有参数 DTO，需要局部调整对应内置工具文件的可见性或提取共享 parser；不得为省事在 CLI 复制 JSON 字段名。

## 13. 分阶段实施步骤

### 阶段一：Runtime Consent 抽象

1. 新增 `IToolConsentHandler`、两个富上下文和 Reverse RPC 适配器。
2. 保留 `ToolConsentCoordinator` 现有委托构造兼容入口。
3. 修改 `RuntimeExecutionCore`，实现本地/Reverse RPC 互斥解析。
4. 为双 handler、无 handler、Reverse RPC handler 添加定向测试。
5. 构建 Services、Server 和现有 Reverse RPC 测试，确认宿主路径无回归。

### 阶段二：最小作用域与 Grant 工厂

1. 从各内置 executor 复用或提取无副作用参数解析。
2. 实现文件、Memory、Process、PowerShell、HTTP 的 `ToolRequestedScope`。
3. 对路径逃逸、URL userinfo/重定向、可执行文件规范化和未知工具 fail closed 做单元测试。
4. 实现 once/run Grant 工厂及 `ToolAuthorizationService` 二次校验测试。
5. 确认 Work 子 Agent 的 AgentId 和委派链不被根 Agent 替换。

### 阶段三：CLI 交互

1. 在 `ICliTerminal` 增加默认 false 的 `IsInteractive`。
2. 实现 `StandaloneToolConsentPolicy`，覆盖文本/机器输出、重定向和输出文件。
3. 实现串行的 Consent Handler、脱敏提示和三类决定。
4. 在 `ServiceCommands` 创建 handler 并注入 `LocalRuntimeOptions`。
5. 覆盖 EOF、非法输入、Ctrl+C、中文路径、空格、引号和并发请求。

### 阶段四：审计闭环

1. 扩展 `ToolApprovalState` 和 SQLite `request_kind` 迁移。
2. permission 分支补齐 pending/decision 持久化。
3. 从 `ToolGateway` 提取 Pending Intent 终结入口。
4. 修改 Coordinator 的所有提前返回和异常分支。
5. 验证拒绝、无 handler、取消和非法 Grant 后不存在悬空 Intent。

### 阶段五：三模式与发布验证

1. Expert 覆盖 Memory、文件、Process、PowerShell、HTTP。
2. Meeting 覆盖参与 Agent 的授权、拒绝和并发提示串行。
3. Work 覆盖 Manager、子 Agent、父/根 Grant、委派边界和 Step 状态恢复。
4. 运行完整测试、Release 构建和 Native AOT 发布。
5. 刷新 CodeMap overlay，审查 handler 引用和所有 `RuntimeExecutionCore.RunAsync` 调用点。
6. 执行 `git diff --check` 并检查没有覆盖当前工作树的 CLI 修改。

## 14. 测试矩阵

### 14.1 Consent Handler 与交互

| 用例 | 输入 | 预期 |
| --- | --- | --- |
| 默认拒绝 | 权限阶段空行 | 等同 `1`，`Denied`，不创建 Grant，Intent 为 Failed |
| 明确拒绝 | 权限阶段输入 `1` | decision reason 标明用户拒绝 |
| 单次允许 | 权限阶段输入 `2` | Grant 含当前 ToolId + CallId + AgentId |
| Run 允许 | 权限阶段输入 `3`，安全作用域 | Grant 不含其他 Tool/Agent，作用域固定到解析结果 |
| 禁止 Run 允许 | destructive/PowerShell/Memory append 输入 `3` | 不创建 Grant，要求重新选择或最终拒绝 |
| 明确审批允许 | 审批阶段输入 `Y` 或 `y` | 批准当前 Call，回复不区分大小写 |
| 明确审批拒绝 | 审批阶段输入 `N`、`n` 或空行 | 拒绝当前 Call，回复不区分大小写 |
| 非法输入 | 连续 3 次未知文本 | 拒绝，不无限等待 |
| EOF | `ReadLineAsync` 返回 null | 拒绝，不再次读取 |
| Ctrl+C | 读取期间取消 | Consent 为 cancelled，Intent 为 Cancelled，Run 收到取消 |
| 并发请求 | 两个 Agent 同时请求 | 两个提示不交叉，每个响应绑定正确 CallId |
| 响应错配 | CorrelationId/CallId 不一致 | fail closed，Intent 为 Failed |
| handler 异常 | I/O 或非预期异常 | 安全失败并终结 Intent，不崩溃遗留 Pending |

### 14.2 交互边界

| 场景 | 预期 |
| --- | --- |
| stdin 重定向 | 不显示提示、不消费额外行、Required 错误 |
| stdout 重定向 | 不显示提示，不污染重定向结果 |
| `--json` | 输出保持合法 JSON，Consent 失败为结构化事件/错误 |
| JSONL | 每行仍可独立解析，无人类提示行 |
| 输出文件 | 文件中只有请求的业务输出，不含 Consent 菜单 |
| `--yes` | 工具仍需要真实 Consent，不自动 Granted |
| `--no-color` | 提示为纯文本，无颜色状态修改 |
| Fake Terminal 默认实现 | `IsInteractive == false`，fail closed |
| Fake Terminal 显式交互 | 可按预置输入验证完整提示和决定 |

### 14.3 Expert 模式

| 工具 | 场景 | 预期 |
| --- | --- | --- |
| `builtin.memory.append` | once 允许 | 写入一次，Grant 绑定 CallId，审计链完整 |
| `builtin.memory.append` | 拒绝 | Memory 文件不变，无 Pending Intent |
| `builtin.fs.write` | 新文件 | Restricted 已允许时不额外提示；行为保持不变 |
| `builtin.fs.write` | 覆盖已有文件 | 显示目标路径，只开启 overwrite + 当前 CallId |
| `builtin.fs.move/delete/patch` | once 允许/拒绝 | 只影响请求目标；拒绝无副作用 |
| `builtin.process.run` | `dotnet --version` | executable 精确收窄，参数不泄露到审计 |
| `builtin.powershell.run` | once 允许 | `AllowPowerShell` 只对当前 CallId 生效 |
| `builtin.http.request` | HTTPS URL | policy 只含请求 scheme/host/port，默认限制响应和重定向 |

### 14.4 Meeting 模式

| 用例 | 预期 |
| --- | --- |
| 单参与 Agent 请求工具 | 提示显示实际参与 Agent，不显示为主持人 |
| 两个参与 Agent 相继请求 | 两个决定和 Grant 不串 CallId/AgentId |
| 一个拒绝、一个允许 | 各自调用结果独立，Meeting 不因审计状态串线 |
| Run 取消时存在等待提示 | 当前提示取消，排队提示不继续读取，Run 正常进入取消终态 |

### 14.5 Work 模式

| 用例 | 预期 |
| --- | --- |
| Manager 请求工具 | Grant.AgentId 为 Manager |
| 子 Agent 请求工具 | Grant.AgentId 为子 Agent，ParentAgentId 可追溯 |
| 子 Agent 无父级委派 | 通过本次用户 Consent 创建叶子 Grant，不能继承其他 Agent Grant |
| 父 Grant 不允许 delegation | 子 Agent 不能通过 run 授权扩大父范围 |
| 父 Grant 允许受限 delegation | 子 Grant 是父 Grant 与请求作用域的交集，RootGrantId/链完整 |
| 用户拒绝 | Work Step 离开 WaitingForApproval，工具结果失败，Intent 终结 |
| 两阶段 permission + approval | 最多各提示一次，最终执行只发生一次 |

### 14.6 作用域与脱敏

| 输入 | 预期 |
| --- | --- |
| 中文、空格和引号路径 | 显示与执行不损坏，权限比较使用规范化路径 |
| `..`、UNC、设备路径、Reparse Point | 沿用文件策略拒绝，不产生扩大 Grant |
| URL 含 userinfo、query、fragment | 提示和审计不泄露敏感部分，网络策略只用解析后的目标 |
| HTTP 重定向到未授权 host | executor 拒绝，不因初始授权自动扩大 host |
| Process 参数含 Token | 提示只显示参数数量/哈希，审计不记录原值 |
| PowerShell 脚本含密钥字样 | 提示只显示哈希和长度 |
| 未知 ToolId 或未知参数 Schema | fail closed，不构造通配 Grant |

### 14.7 持久化与恢复

对 SQLite 至少断言：

- permission 与 approval 都有 `pending -> 最终 decision` 记录及正确 `request_kind`。
- Granted 记录可关联 ToolGrant；Denied/Unavailable/Cancelled 不存在活动 Grant。
- ToolIntent 在所有返回路径中为 Succeeded、Failed 或 Cancelled，不残留 Pending。
- 高风险执行存在 prepared 和最终 ToolAudit，GrantId、RootGrantId、ApprovalRequestId 和 Agent 链正确。
- 拒绝路径没有伪造 prepared ToolAudit。
- 同一 CallId 重放不会重复提示、重复 Grant 或重复副作用。
- 旧数据库没有 `request_kind` 时可自动升级，旧 approval 记录读取为 `approval`。
- Runtime 重启后不会把先前 Denied 调用恢复为可执行调用。

### 14.8 回归与发布

建议按下列层次验证：

1. `Madorin.AI.Runtime.Provider.Tests`：`ToolGatewayTests` 与新增 Consent Coordinator 定向测试。
2. `Madorin.AI.Runtime.Persistence.Tests`：`SqliteToolStateStoreTests`、Schema 升级和 Intent 终结测试。
3. `Madorin.AI.Runtime.Modes.Tests`：`MeetingToolLoopTests`、`WorkToolLoopTests`。
4. `Madorin.AI.Runtime.EndToEnd.Tests`：`StandaloneLocalRuntimeTests`、`StandaloneReplStage7Tests`、`StandaloneRunInputOutputStage7Tests`、`StandaloneRunModesStage7Tests`。
5. 现有 `ReverseRpcToolLoopTests`、`ReverseRpcHotReconnectTests`，确认宿主 Consent 路径行为不变。
6. 完整 Debug `dotnet test`。
7. Release 构建。
8. `win-x64` Native AOT 发布和真实终端冒烟测试。
9. `git diff --check`。

## 15. 风险与控制

| 风险 | 控制措施 |
| --- | --- |
| CLI 根据 ToolId 发放过宽权限 | Runtime 侧解析规范化最小作用域；CLI 只从结构化 scope 建 Grant；未知工具拒绝 |
| 本地 handler 与 Reverse RPC 同时生效 | `RuntimeExecutionCore` 在 Run 开始前强制互斥 |
| Permission 后仍需 Approval 但未再次提示 | Coordinator 使用有界两阶段状态机，并检测重复 request id |
| 用户拒绝后 Intent 悬空 | 所有提前返回统一调用 ToolGateway 终结入口 |
| Ctrl+C 取消后持久化被同一令牌跳过 | 使用受控清理令牌写入 Cancelled 后再传播取消 |
| Meeting/Work 并发读取同一 stdin | Handler 内全局提示锁，响应按 request id 绑定 |
| `Allow for run` 被过度复用 | 默认隐藏；只对 `CanGrantForRun` 工具显示；绑定 Run/Agent/Tool/固定 scope/短期过期 |
| Work 子 Agent 权限扩大 | 父 Grant 子集校验、实际 AgentId、Parent/Root/DelegationChain 全链持久化 |
| 提示或审计泄露密钥 | 只显示/存储哈希、长度和安全 target summary；URL、脚本、Header、环境变量专门脱敏 |
| `--yes` 被误用为自动授权 | ConsentPolicy 明确忽略该选项并增加回归测试 |
| 机器输出被提示污染 | Handler 只在纯文本真实终端创建；Fake 默认不可交互 |
| 修改公共协议破坏宿主 | 富上下文仅进程内使用，现有 DTO 和 Reverse RPC 序列化保持不变 |
| SQLite 迁移破坏旧数据 | 新字段带默认值、幂等 AddColumn、旧记录默认 approval、升级测试覆盖 |
| 与当前未提交 CLI 改动冲突 | 实施时局部补丁；先刷新 CodeMap overlay；不回退 `CliTextStyle`、帮助、选择菜单等现有改动 |

## 16. 文档一致性修订

`docs/执行步骤/04B-全局与项目记忆.md` 当前关于 `builtin.memory.append` “已支持用户明确审批”的描述只对具备宿主 Consent 回调的路径成立，与独立 CLI 实际行为不一致。

专项完成后的同步文案为：

```text
builtin.memory.append 已接入统一审批协议。
宿主模式通过 Reverse RPC 权限/审批；独立交互 CLI 通过本地终端 Consent Handler；独立非交互 CLI 无明确 Grant 时 fail closed。
```

专项完成后，三路径事实为：

- 宿主模式：Reverse RPC 权限/审批。
- 独立交互 CLI：本地终端 Consent Handler。
- 独立非交互 CLI：无明确 Grant 时 fail closed。

`docs/执行步骤/04B-全局与项目记忆.md`、`docs/备档/05-安全与数据运维.md` 和 `docs/备档/06-自动化与故障排查.md` 已按上述三路径同步；机器输出不会弹出授权提示，`--yes` 不批准模型工具。

## 17. 验收清单

- [√] 独立 REPL 调用需授权工具时出现一次完整、脱敏的终端提示。
- [√] `Allow once` 创建绑定当前 Run、Agent、Tool、Call 和最小作用域的 Grant。
- [√] 只有明确安全的作用域显示 `Allow for run`，且 Grant 不跨 Run。
- [√] `Deny`、空输入、EOF、无 handler、非交互和 invalid response 都不执行工具。
- [√] Ctrl+C 取消提示和当前 Run，不遗留 Pending Intent 或 Waiting Work Step。
- [√] Permission 后仍需要 Approval 时可以完成第二阶段，且工具最多执行一次。
- [√] Expert 的 Memory append、覆盖文件、Process、PowerShell、HTTP 均覆盖允许与拒绝。
- [√] Meeting 并发 Agent 的提示、决定和 CallId 不串线。
- [√] Work 子 Agent 的 AgentId、ParentAgentId、ParentGrantId、RootGrantId 和委派链正确。
- [√] JSON、JSONL、stdin/stdout 重定向和输出文件不出现人类授权提示。
- [√] `--yes` 不会自动批准模型工具。
- [√] 提示、日志和 SQLite 不包含完整脚本、Memory 内容、HTTP 密钥或环境变量密钥值。
- [√] permission/approval decision、Grant、ToolIntent 和高风险 ToolAudit 可以按 CallId 关联。
- [√] 所有拒绝和异常分支都把 ToolIntent 终结为 Failed/Cancelled。
- [√] 旧 SQLite 数据目录可以幂等升级，新旧 approval 记录都可读取。
- [√] 宿主 Reverse RPC 权限、审批、重连和恢复测试保持通过。
- [√] Debug 全量测试、Release 构建、Native AOT 发布通过。
- [√] CodeMap overlay 已刷新，所有 `RuntimeExecutionCore.RunAsync` 入口均传递正确 handler。
- [√] `git diff --check` 通过，现有未提交 CLI 改动未被覆盖。

## 18. 完成定义

只有同时满足以下条件，才能认为本 Bug 修复完成：

1. 独立交互 CLI 不再对需要 Consent 的工具直接无提示拒绝。
2. 独立非交互 CLI 仍然安全拒绝，并保持机器输出可解析。
3. 每个 Granted 决定都能证明其最小作用域、调用身份和有效期。
4. 每个 Denied/Unavailable/Cancelled 决定都能查询到终态 Intent，不存在悬空授权状态。
5. Expert、Meeting、Work 共用同一 Runtime Consent 状态机，而不是在三个模式分别复制提示逻辑。
6. 宿主 Reverse RPC 仍是宿主模式唯一权限权威，公开协议保持兼容。
