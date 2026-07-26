# CLI 交互显示与工作目录修复方案

> 文档状态：已完成
>
> 适用范围：`Madorin.AI.Runtime.Cli` 独立启动与交互式 REPL
>
> 参考项目：`Madorin.AI.Runtime.Client`、`Madorin.AI.Runtime.Tools.Builtin`
>
> 编制日期：2026-07-26

## 1. 背景与问题证据

本次修复聚焦 CLI 的首次使用体验、交互命令可发现性和独立启动时的工作目录契约。现有实现已经具备 REPL、模型选择和工作区安全边界，但用户可见行为不完整，导致“功能存在但不容易发现或确认”。

### 1.1 欢迎语缺少命令引导和视觉层次

`Commands/ReplWelcomeWriter.cs` 当前以纯文本输出标题、运行状态、模式、Agent、Provider、Model、工作区和 Session，最后仅显示：

```text
Enter a message to begin. /exit to quit.
```

缺少 `/help` 命令提示；标题、状态、字段标签、字段值和命令使用同一种显示样式，无法快速区分。

### 1.2 `--no-color` 已声明但未生效

`CliApplication.cs` 已注册递归选项 `--no-color`，但未将该选项传递到终端输出层。当前 `ICliTerminal` 只有普通 `Write`、`WriteLine`、`Clear` 和输入方法，没有语义样式能力。

### 1.3 带参数的选择命令行为不一致

当前 REPL 行为如下：

| 命令 | 无参数行为 | 当前问题 |
| --- | --- | --- |
| `/model` | 列出当前 Provider 的模型，支持编号或 ID 选择 | 已基本满足，但输出未使用语义颜色 |
| `/provider` | 仅显示当前 Provider | 未列出可选 Provider，不能编号选择 |
| `/agent` | 仅显示当前 Agent | 未列出可选 Agent，不能编号选择 |

Provider 切换时已有模型回退逻辑：优先保留当前模型，其次使用默认模型，最后使用目标 Provider 的第一个模型。Agent 切换时已有合法 Provider/Model 解析逻辑。本次保留这些业务规则，只补齐可发现的列表和一致的选择入口。

### 1.4 裸启动工作目录需要固化为公开契约

`StandaloneRuntimeContext.Create()` 当前以 `workspace ?? Directory.GetCurrentDirectory()` 解析工作区。因此从 Windows 资源管理器地址栏输入 `cmd`，再无参数启动 CLI 时，理论上会使用该命令行进程的当前目录。

现有测试主要覆盖直接调用和进程内逻辑，缺少“发布后的真实 CLI 进程在指定 `WorkingDirectory` 下无参数启动”的回归用例。为防止后续入口归一化、发布方式或进程启动逻辑改变该行为，需要在裸启动入口显式捕获启动目录，并用进程级测试固定契约。

### 1.5 Client 与内置工具的边界

- `Madorin.AI.Runtime.Client` 已使用参数数组显式传递 `--workspace`，不需要加入终端交互或选择逻辑。
- `Madorin.AI.Runtime.Tools.Builtin` 已以 `ToolPermissionContext.WorkspaceRoot` 作为文件工具安全根，不应读取进程当前目录来判断权限。
- 本次原则上不修改上述两个项目，通过 CLI 集成测试确认工作区仍正确传入 Runtime 和工具权限上下文。

## 2. 修复目标

1. 欢迎语提供清晰的颜色层次，并明确提示 `/help` 和 `/exit`。
2. `/provider`、`/model`、`/agent` 无参数时均列出可选数据、标记当前项，并允许输入编号或 ID。
3. REPL 命令、参数占位符、提示、当前选项、成功、警告和错误具备稳定的语义颜色。
4. `--no-color` 真正关闭颜色，并在非交互或机器输出场景自动降级为纯文本。
5. 无参数独立启动时，将 CLI 启动瞬间的当前目录作为默认工作目录；显式 `--workspace` 始终优先。
6. 不破坏 JSON/JSONL、重定向、输出文件、测试注入 Writer 和 Client 进程调用。

## 3. 非目标

- 不引入 `Spectre.Console` 或其他新终端依赖。
- 不重做 `System.CommandLine` 的全局帮助页面。
- 不改变 Provider、Model、Agent 的持久化配置格式。
- 不改变 Session 中 Selection 的版本和下一轮生效语义。
- 不调用 `Directory.SetCurrentDirectory()` 修改进程全局状态。
- 不把工作目录安全边界交给内置文件工具自行推断。
- 不在 Client SDK 中复制 CLI 的颜色、菜单或交互逻辑。

## 4. 终端语义样式设计

### 4.1 样式类型

扩展 `ICliTerminal`，为文本输出增加语义样式。建议定义 `CliTextStyle` 枚举：

| 语义样式 | ConsoleColor | 用途 |
| --- | --- | --- |
| `Default` | 保持当前颜色 | 普通正文 |
| `Title` | `DarkCyan` | 产品标题、列表标题 |
| `Command` | `DarkCyan` | `/help`、`/provider` 等命令 |
| `Parameter` | `White` | `[providerId]`、编号、可输入参数 |
| `Selected` | `White` | 当前 Provider、Model、Agent 等关键值 |
| `Prompt` | `DarkCyan` | REPL 输入提示和选择提示 |
| `Success` | `DarkGreen` | Ready 状态和成功结果 |
| `Warning` | `DarkYellow` | 可恢复问题和警告 |
| `Error` | `DarkRed` | 错误信息 |
| `Muted` | `DarkGray` | 字段标签、补充说明、`(current)` |

正常界面仅使用暗青色强调和灰白中性色：命令、标题、提示使用暗青色，参数和当前值使用白色，说明信息使用暗灰色。绿色、黄色和红色只用于成功、警告和错误状态，不参与普通内容层级，避免多个高饱和颜色同时抢占视觉重点。颜色只表达语义，不作为唯一信息来源；纯文本模式仍保留标题、标签、编号和 `(current)` 标记。

### 4.2 终端 API

在不破坏现有调用方的前提下，为 `ICliTerminal` 增加带样式的重载：

```csharp
void Write(string value, CliTextStyle style);
void WriteLine(string value, CliTextStyle style);
```

原有无样式方法继续保留。`CliTerminal` 仅在生产控制台启用颜色；写入前保存 `Console.ForegroundColor`，写入后在 `finally` 中恢复，避免异常导致后续输出串色。

### 4.3 强制纯文本条件

满足任一条件时禁止写颜色：

- 命令行包含 `--no-color` 或 `--no-color=true`。
- 环境变量 `NO_COLOR` 存在，无论其值是什么。
- 输出不是 `Console.Out`，例如 `StringWriter`、文件 Writer 或宿主注入 Writer。
- `Console.IsOutputRedirected` 为 `true`。
- 请求 `--json`、`--output-format json` 或 `--output-format jsonl`。
- 命令将主要结果写入输出文件。

颜色状态在创建 `CliTerminal` 时确定，不在业务命令中直接操作 `Console.ForegroundColor`。

## 5. 欢迎语与 REPL 显示契约

### 5.1 欢迎语文本

欢迎语保持紧凑，不添加装饰性 ASCII 图形。纯文本等价输出如下：

```text
Madorin AI Runtime <version>
Local runtime ready

Mode       <mode>
Agent      <agent>
Provider   <provider>
Model      <model>
Workspace  <absolute-path>
Session    New

Type /help to view commands, or /exit to quit.
```

显示规则：

- 第一行使用 `Title`。
- `ready` 使用 `Success`。
- 字段标签使用 `Muted`，字段值使用 `Default`；当前 Provider、Model、Agent 使用 `Selected`，实际显示为白色。
- `/help`、`/exit` 使用 `Command`。
- 欢迎语改为接收 `ICliTerminal`，不再绕过终端抽象直接写 `TextWriter`。

### 5.2 REPL 输入提示

`madorin [provider/model] >` 中：

- `madorin` 和 `>` 使用 `Prompt`。
- Provider/Model 值使用 `Selected`，实际显示为白色。
- 方括号和分隔符保持默认或 `Muted`。

### 5.3 `/help` 显示

无主题 `/help` 继续列出全部命令，但改为逐项语义渲染，至少区分命令与参数。主题帮助保留现有说明文本，并将命令部分标为 `Command`、参数占位符标为 `Parameter`、说明标为 `Muted`。

## 6. Provider、Model、Agent 选择契约

三个命令使用同一交互模式：

1. 输出列表标题。
2. 每项显示从 1 开始的稳定编号、可输入 ID/名称及必要说明。
3. 当前项 ID 使用 `Selected` 样式，追加的 `(current)` 标记使用 `Muted` 样式，避免整行使用状态色。
4. 输出 `Select <kind> by number or ID (Enter to cancel): ` 提示。
5. 空输入取消，不修改 Selection，也不增加 Selection 版本。
6. 合法编号转换为对应 ID；非编号按原 ID 规则解析。
7. 越界编号按普通 ID 处理，最终返回现有的“未找到/不可用”错误，不抛出异常。
8. 成功后继续使用 `UpdateReplSelectionAsync`，保持已有 Session Selection 版本递增和下一轮生效语义。

### 6.1 `/provider [providerId]`

无参数时列出 `config.Providers`，显示 Provider 名称及模型数量。当前 Provider 标记为 `(current)`。

选中目标 Provider 后，模型按下列优先级确定：

1. 目标 Provider 支持当前 Model 时保留当前 Model。
2. 否则目标 Provider 支持全局默认 Model 时使用默认 Model。
3. 否则使用目标 Provider 的第一个 Model。
4. 没有可用 Model 或当前 Agent 无法形成合法 Selection 时，保持原 Selection 并显示错误。

### 6.2 `/model [modelId]`

无参数时列出当前 Provider 的 `Models`。保留现有编号、ID 选择与可用性验证；改为共享的列表和提示渲染，减少三个命令之间的行为漂移。

### 6.3 `/agent [agentId]`

无参数时列出已加载的 Agent，至少显示 `Agent.Id`，存在名称时同时显示名称。当前 Agent 标记为 `(current)`。

选中 Agent 后继续调用现有 `TryResolveSelection`：Agent 自身声明的 Provider/Model 优先，无法构成合法 Selection 时保持原 Selection 并显示错误。

### 6.4 空列表

- Provider 或 Agent 列表为空时显示明确错误，不进入等待输入状态。
- Model 列表为空时显示当前 Provider 没有可用模型，不进入等待输入状态。
- 空列表属于配置问题，当前 REPL 保持运行，用户可以使用其他命令诊断或退出。

## 7. 独立启动工作目录契约

### 7.1 行为定义

| 启动方式 | 工作目录结果 |
| --- | --- |
| 无参数启动 | CLI 入口捕获的 `Directory.GetCurrentDirectory()` |
| `--mode standalone` | 同无参数启动 |
| `run` 且未传 `--workspace` | 命令开始时的当前目录 |
| 任意形式显式传入 `--workspace <path>` | 显式路径，优先级最高 |
| Client 启动 Runtime | Client 参数数组传入的显式工作区 |

裸启动入口应在参数归一化前捕获启动目录，并将无参数启动规范化为显式等价形式：

```text
run --workspace <startup-current-directory>
```

这样可以避免后续初始化流程、配置加载或第三方代码改变进程当前目录后影响业务工作区。`StandaloneRuntimeContext.Create()` 的默认逻辑继续保留，服务于直接 `run` 和现有调用方。

### 7.2 安全边界

- 工作区最终通过 `Path.GetFullPath` 规范化。
- 不修改进程当前目录，不影响相对路径由其他组件自行解析时的既有行为。
- 独立模式数据目录继续采用现有规则：位于配置目录的 `data/workspaces/<workspace-key>`，不改为可执行文件目录或进程当前目录。
- `ToolPermissionContext.WorkspaceRoot` 必须来源于已确定的 Runtime 工作区，不得重新读取当前目录。

### 7.3 Windows 验证场景

发布 CLI 后创建临时目录，在 `ProcessStartInfo.WorkingDirectory` 指向该目录的情况下无参数启动。测试进程通过 `MADORIN_CONFIG_DIR` 指向隔离的临时配置目录，避免读取或修改用户真实配置；默认未设置该环境变量时仍使用现有用户配置目录。向标准输入写入 `/exit`，断言：

- 进程正常退出。
- 欢迎语 `Workspace` 为临时目录的规范化绝对路径。
- Runtime 数据目录位于临时配置目录的 workspace-keyed 数据区，不位于可执行文件目录或仓库目录。

## 8. 代码修改范围

### 8.1 计划修改

| 文件 | 修改内容 |
| --- | --- |
| `src/Madorin.AI.Runtime.Cli/CliApplication.cs` | 消费 `--no-color`；识别机器输出；创建颜色策略；裸启动时捕获并显式传入启动工作区；支持进程级 `MADORIN_CONFIG_DIR` 配置隔离 |
| `src/Madorin.AI.Runtime.Cli/Config/CliTerminal.cs` | 增加语义样式、颜色启用策略和安全的颜色恢复 |
| `src/Madorin.AI.Runtime.Cli/Commands/ReplWelcomeWriter.cs` | 改为终端语义输出；增加 `/help` 提示 |
| `src/Madorin.AI.Runtime.Cli/Commands/ServiceCommands.cs` | REPL 提示着色；统一 Provider/Model/Agent 列表、编号选择和帮助渲染 |
| `tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneWelcomeTests.cs` | 更新欢迎语断言，覆盖 `/help` 提示和纯文本等价输出 |
| `tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneReplStage7Tests.cs` | 覆盖三类列表、当前标记、编号/ID、取消、非法输入和样式语义 |
| `tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneCliProcessStage7Tests.cs` | 增加真实发布进程在指定工作目录下裸启动的回归测试 |

如测试项目已有更合适的 Fake Terminal 或进程发布夹具，应在既有文件内扩展，避免重复基础设施。

### 8.2 原则上不修改

| 项目/文件 | 原因 |
| --- | --- |
| `Madorin.AI.Runtime.Client` | 已通过参数数组显式传递工作区，职责正确 |
| `Madorin.AI.Runtime.Tools.Builtin` | 已以权限上下文工作区为文件安全根，职责正确 |
| `Madorin.AI.Runtime.Cli.csproj` | 本次不新增第三方依赖 |

若测试暴露上述项目存在真实契约缺口，必须先更新本方案的证据和修改范围，再进行局部修复。

## 9. 测试矩阵

### 9.1 终端颜色

| 用例 | 预期 |
| --- | --- |
| 真实交互 Console，普通文本模式 | 样式映射到预期 `ConsoleColor` |
| `--no-color` | 不改变 Console 前景色 |
| `NO_COLOR` 存在 | 不改变 Console 前景色 |
| 输出重定向 | 纯文本，无 ANSI 控制序列 |
| 注入 `StringWriter` | 纯文本，快照测试稳定 |
| `--json` / JSONL | 机器输出不含颜色或额外欢迎文本 |
| 样式写入发生异常 | `finally` 恢复原 Console 前景色 |

### 9.2 欢迎语与帮助

| 用例 | 预期 |
| --- | --- |
| 无参数启动 | 显示版本、Ready、Selection、Workspace、Session |
| 欢迎语结尾 | 包含 `Type /help to view commands, or /exit to quit.` |
| `/help` | 列出全部命令，命令和参数使用不同语义样式 |
| `/help provider` | 显示 `/provider [providerId]` 及说明 |

### 9.3 参数选择

| 用例 | 预期 |
| --- | --- |
| `/provider` | 列出全部 Provider，标记当前项并提示输入 |
| Provider 编号 | 切换到对应 Provider，按回退规则选择模型 |
| Provider ID | 切换到对应 Provider |
| `/model` | 只列出当前 Provider 的模型并标记当前项 |
| Model 编号或 ID | 切换模型 |
| `/agent` | 列出全部 Agent，显示 ID/名称并标记当前项 |
| Agent 编号或 ID | 切换 Agent 并解析有效 Selection |
| 三类选择输入为空 | 取消且 Selection/版本不变 |
| 非法 ID 或不可用组合 | 显示错误，REPL 保持运行，Selection 不变 |

### 9.4 工作目录

| 用例 | 预期 |
| --- | --- |
| 进程内无参数启动 | 当前目录成为 Workspace |
| 发布版进程无参数启动并设置 `WorkingDirectory` | 指定目录成为 Workspace |
| 显式 `--workspace` 与进程当前目录不同 | 显式路径优先 |
| 路径包含空格和中文 | 欢迎语和 Runtime 工作区保持完整绝对路径 |
| 内置文件工具集成 | 权限根与 CLI 确定的 Workspace 相同 |

## 10. 分阶段实施步骤

### 阶段一：终端样式基础设施

1. 新增 `CliTextStyle`。
2. 扩展 `ICliTerminal` 和 `CliTerminal`。
3. 将 `--no-color` 选项保留为变量，并结合参数、环境、Writer 和重定向状态确定颜色开关。
4. 添加终端单元测试或通过样式感知 Fake Terminal 验证语义调用。
5. 构建 CLI 和 EndToEnd 测试项目，先消除接口扩展带来的编译错误。

### 阶段二：欢迎语、提示与帮助

1. `ReplWelcomeWriter` 改为使用 `ICliTerminal`。
2. 加入 `/help` 和 `/exit` 提示。
3. 对欢迎语字段、REPL 输入提示、Selection 更新结果应用语义样式。
4. 将 `/help` 从单行拼接文本改为结构化命令条目渲染。
5. 更新既有欢迎语和帮助测试。

### 阶段三：统一参数列表选择

1. 提取小型的列表输出和输入解析辅助方法，限定在 CLI Commands 边界内。
2. 迁移 `/model` 到共享行为，确保既有用例保持通过。
3. 为 `/provider` 增加列表、当前标记、编号选择。
4. 为 `/agent` 增加列表、名称显示、当前标记、编号选择。
5. 覆盖取消、越界、非法 ID、无可用组合和 Selection 版本不变场景。

### 阶段四：工作目录契约

1. CLI 公共入口在裸启动时捕获当前目录。
2. 参数归一化时为无参数启动加入显式 `--workspace`。
3. 保证显式用户参数优先，不重复添加工作区参数。
4. 添加发布版真实进程回归，覆盖包含空格和中文的工作目录。
5. 验证 Runtime 的 Workspace 与数据目录 workspace key 均源自同一启动目录。

### 阶段五：综合验证

按以下顺序执行：

1. 刷新 CodeMap workspace overlay，检查改动后的接口引用和调用关系。
2. 运行欢迎语、REPL 选择、CLI 进程相关的定向 EndToEnd 测试。
3. 运行完整 `dotnet test`。
4. 执行 Release 构建。
5. 执行 `win-x64` Native AOT 发布，确认未引入新的裁剪/AOT 警告。
6. 执行 `git diff --check`，检查空白错误。
7. 审查 `git diff`，确认没有覆盖用户已有修改，也没有意外修改 Client、Tools.Builtin 或 CLI 项目依赖。

## 11. 风险与控制

| 风险 | 控制措施 |
| --- | --- |
| 直接设置 Console 颜色影响并行输出 | 颜色操作集中在 `CliTerminal`，保存并恢复原颜色；仅交互 Console 启用 |
| 扩展 `ICliTerminal` 导致大量测试替身编译失败 | 使用默认接口实现或统一更新既有 Fake Terminal，优先保持调用兼容 |
| Machine output 被欢迎语或颜色污染 | 机器格式和非 Console Writer 强制纯文本；欢迎语仍只在裸启动 REPL 显示 |
| 列表顺序不稳定导致编号变化 | 使用配置加载后的原始顺序，不额外按区域性规则排序 |
| Provider 切换后模型无效 | 保留现有模型回退和 `TryResolveSelection` 验证 |
| 裸启动目录在初始化期间变化 | 入口最早捕获并显式传给 `run --workspace` |
| 工作区修复削弱文件工具权限 | 工具仍只依赖 `ToolPermissionContext.WorkspaceRoot`，通过集成测试交叉验证 |
| 修改与工作区现有未提交内容冲突 | 仅做目标文件局部补丁，不回退、不覆盖无关改动 |

## 12. 验收清单

- [x] 方案文档位于 `docs/Bug修复/01-CLI交互显示与工作目录修复方案.md`。
- [x] 欢迎语包含 `/help` 和 `/exit` 提示。
- [x] 欢迎语、命令、参数、提示、当前项、成功、警告和错误存在语义样式。
- [x] `--no-color`、`NO_COLOR`、重定向和机器输出均为纯文本。
- [x] `/provider` 无参数列出所有 Provider，支持编号和 ID。
- [x] `/model` 无参数列出当前 Provider 的所有 Model，支持编号和 ID。
- [x] `/agent` 无参数列出所有 Agent，支持编号和 ID。
- [x] 三类列表均标记当前项，空输入可取消且不改变 Selection。
- [x] 无参数独立启动使用启动时当前目录作为工作区。
- [x] 显式 `--workspace` 的优先级高于当前目录。
- [x] 发布版进程测试覆盖 Windows 指定 `WorkingDirectory` 场景。
- [x] Client 显式工作区传参行为保持不变。
- [x] Tools.Builtin 文件权限根仍来自 Runtime 工作区上下文。
- [x] 定向测试、完整测试、Release 构建和 Native AOT 发布通过。
- [x] `git diff --check` 通过，改动范围符合本文档。

## 13. 完成定义

### 13.1 实际修改

- CLI 入口消费 `--no-color`、机器输出状态和 `MADORIN_CONFIG_DIR`，裸启动时捕获并显式传递启动工作目录。
- 新增 `CliTextStyle`，由 `CliTerminal` 统一映射 `ConsoleColor` 并在写入后恢复原颜色。
- 欢迎语增加 `/help` 提示，并对标题、状态、字段和命令使用语义样式。
- 新增 `ReplHelpWriter` 和 `ReplSelectionMenu`，统一帮助条目与 Provider/Model/Agent 列表选择。
- 未修改 `Madorin.AI.Runtime.Client`、`Madorin.AI.Runtime.Tools.Builtin` 和 CLI 项目依赖。

### 13.2 验证结果

- 欢迎语、REPL 和 CLI 进程定向测试通过。
- Windows apphost 在包含中文和空格的 `WorkingDirectory` 下无参数启动测试通过。
- 完整解决方案测试：951 个通过，6 个依赖外部 Provider 配置的测试跳过，0 个失败。
- Release 构建：0 警告，0 错误。
- `win-x64` Native AOT 发布成功；生成的 `madorin-ai-cli.exe` 可执行并返回版本 `0.1.3`、协议版本 `1.1`。
- `git diff --check` 通过。

本文档按本次任务要求保留在 `docs/Bug修复`，不随其他已完成开发文档迁入 `docs/备档`。
