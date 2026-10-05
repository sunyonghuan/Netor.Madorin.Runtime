# Native AOT 激进裁剪与压缩对比报告

> 测试日期：2026-07-27  
> 测试平台：Windows x64，.NET SDK 10.0.301  
> 测试对象：`Madorin.AI.Runtime.Cli` 0.1.5  
> 激进版发布目录：`.tmp/aot-aggressive-release/run-20260727-roots-stack-diagnostics-off/publish-full`

## 1. 结论

激进版发布成功，第二次同参数 `dotnet publish` 正常返回退出码 0；最终 exe 哈希与第一次产物、已打包产物一致。本机可验证的 Native AOT 功能没有发现因本轮裁剪产生的类型缺失、反射缺失、序列化失败或进程崩溃。

与已登记的保守版相比：

- 必要运行文件从 **55.6987 MiB** 降至 **48.2002 MiB**，减少 **7.4985 MiB（13.46%）**。
- `madorin.exe` 从 **53.8760 MiB** 降至 **46.3774 MiB**，减少 **7.4985 MiB（13.92%）**。
- ZIP 从 **21.4284 MiB** 降至 **18.7263 MiB**，减少 **2.7021 MiB（12.61%）**。
- 相比最初 74.7295 MiB 的 exe，当前激进 exe 累计减少 **28.3521 MiB（37.94%）**。

激进版可以作为内部实验包和极限体积候选包，但暂不建议直接替代默认生产包。原因不是本轮发现功能失败，而是它主动关闭了生产诊断能力，并解除了动态 AI Framework 的整程序集保护。2026-07-27 已在保留诊断能力的 B2.3 激进候选上补验 `deepseek/deepseek-v4-pro` 真实非流式成功链；其他 Provider 和真实 Skill 调用仍未覆盖。

## 2. 两档版本登记

| 指标 | 保守版 `aot-safe-size` | 激进版 `aot-aggressive` | 差值 |
|---|---:|---:|---:|
| exe 字节数 | 56,493,056 | 48,630,272 | -7,862,784 |
| exe MiB | 53.8760 | 46.3774 | -7.4985 |
| 必要运行文件 MiB | 55.6987 | 48.2002 | -7.4985 |
| ZIP 字节数 | 22,469,337 | 19,635,993 | -2,833,344 |
| ZIP MiB | 21.4284 | 18.7263 | -2.7021 |
| ZIP/解压体积 | 38.47% | 38.85% | +0.38 个百分点 |

登记包目录：

```text
releases/AOT-Experiments
```

ZIP 哈希：

```text
aot-safe-size
b9cfba6ccac736010ee8896f32064dfc4582e3997dd714c2fd2b641ea05e2c49

aot-aggressive
814e334ae8bb571636292a72f55a706cc9bd342acbe855be2d8b66cb9548b70f
```

激进版 ZIP 已解压到独立目录复测，解压后的 exe 与发布目录 exe 哈希一致，`version --json` 退出码为 0。

## 3. 激进裁剪配置

激进版没有修改项目文件，通过命令行全局属性发布到独立目录。核心参数如下：

```powershell
dotnet publish src\Madorin.AI.Runtime.Cli\Madorin.AI.Runtime.Cli.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  --no-restore `
  -p:OptimizationPreference=Size `
  -p:UseAgentFrameworkRuntimeDirectives=false `
  -p:StackTraceSupport=false `
  -p:IlcGenerateStackTraceData=false `
  -p:DebuggerSupport=false `
  -p:EventSourceSupport=false `
  -p:HttpActivityPropagationSupport=false `
  -p:UseSystemResourceKeys=true `
  -p:IlcFoldIdenticalMethodBodies=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -p:NativeDebugSymbols=false
```

ILC 响应文件已确认：

| ILC 项 | 结果 |
|---|---|
| `--Os` | 存在 |
| `--dehydrate` | 存在 |
| `--methodbodyfolding:all` | 存在 |
| AI `--root` | 0 个 |
| `--rdxml` | 0 个 |
| `--stacktracedata` | 不存在 |
| `System.Diagnostics.StackTrace.IsSupported` | `false` |
| `System.Diagnostics.Debugger.IsSupported` | `false` |
| `System.Diagnostics.Tracing.EventSource.IsSupported` | `false` |
| `System.Net.Http.EnableActivityPropagation` | `false` |
| `System.Resources.UseSystemResourceKeys` | `true` |

本轮没有使用 `ExcludeAssets="all"` 手工切断 Humanizer、Protobuf、Tensors 等三方传递依赖，也没有删除实际使用的 Provider 项目。减重主要来自释放 5 个 AI Framework 整程序集 root、关闭栈追踪/诊断特性及相同方法折叠。

## 4. 激进版发布物回归

### 4.1 发布和命令入口

| 验证项 | 结果 |
|---|---|
| 完整 `dotnet publish` | 退出码 0 |
| `version --json` | 退出码 0，版本 0.1.5，win-x64 |
| 命令/子命令帮助 | 51/51 退出码 0，Usage 匹配 |
| 非法参数 | 9/9 退出码和错误码与保守版一致 |

第一次全量链接在 exe 已生成后触发了测试执行器 3 分钟上限；第二次同参数增量发布在 8.7 秒内正常结束，exe 字节数和 SHA-256 未变化。方法折叠会增加冷发布链接时间，应纳入 CI 超时预算。

### 4.2 配置、Agent、记忆和 Session

以下 15 项实际命令全部退出码 0、JSON 可解析且 `success=true`：

- `version`
- `config validate` / `config show`
- `agent list`
- `agent create`，创建后查询包含新 Agent
- `agent delete`，删除后查询不再包含测试 Agent
- `memory init project` / `show project` / `clear project --yes`
- `skill refresh` / `list` / `validate`
- `session list`

Agent CRUD 使用已验证可写 ACL 的系统用户 Temp 配置目录，避免把已知的自定义目录 ACL 问题误判为裁剪回归。

### 4.3 非空 Skill

在隔离工作区创建最小 `aot-smoke` Skill 后，以下 4 项全部通过并能返回该 Skill：

- `skill refresh`
- `skill list`
- `skill show aot-smoke`
- `skill validate`

这证明非空 Skill 元数据加载和 JSON 输出路径在激进裁剪后可用。后续补跑的真实 Provider smoke 只验证普通问答，没有工具选择，因此 Skill 被模型实际选择和调用的完整链路仍未验证。

### 4.4 Runtime、IPC、SQLite 和存储

| 验证项 | 结果 |
|---|---|
| Runtime Server 启动和 schema 16 初始化 | 通过 |
| 命名管道握手认证 | 通过 |
| `ctl status` / `sessions` / `runs` | 退出码 0，`success=true` |
| `ctl cancel missing-run` | 退出码 1，正确返回 `CancelRejected` |
| 真正零参数交互入口 | 退出码 0，显示 Banner，标准错误为空 |
| `db check` | schema 16、WAL、healthy=true |
| `db migrate/repair/rebuild --dry-run` | 通过 |
| `db vacuum` / `db backup` | 通过，备份 368,640 字节 |
| `storage check --verify-hashes` | 通过 |
| `storage gc --dry-run` | 通过 |
| `session list --status all` | 通过 |

### 4.5 Provider 和三种模式

使用 `.invalid` 假 Provider，不接触真实服务：

| 场景 | 退出码 | 结果 |
|---|---:|---|
| 1 秒超时 | 130 | `RunCancelled` |
| 10 秒网络失败 | 1 | `ProviderRequestFailed` |
| expert 模式 | 1 | 到达 Provider 后结构化失败 |
| meeting 模式 | 1 | 结构化 `RunExecutionFailed` |
| work 模式 | 2 | 单 Agent 配置正确返回 `InsufficientWorkAgents` |

三个模式均完成类型加载和编排初始化，没有出现 `TypeLoadException`、`MissingMethodException`、反射元数据异常或进程崩溃。

测试证据位于：

```text
.tmp/aot-aggressive-release/run-20260727-roots-stack-diagnostics-off
```

## 5. 风险与未覆盖项

### 5.1 真实 Provider 已补验，覆盖范围有限

2026-07-27 使用 `deepseek/deepseek-v4-pro` 在 B2.3 完成真实非流式请求，返回 `OK` 且退出码为 0；同一用户级配置在 B1 的非流式和流式请求也均成功。该结果证明当前 DeepSeek 普通成功响应链在两档 NativeAOT 产物上可用，但不能外推到 OpenAI、Anthropic、Azure OpenAI、Ollama、其他模型、工具循环或生产流式负载。正式放行仍应按实际部署 Provider 逐项补齐矩阵。

### 5.2 生产诊断能力明显下降

`StackTraceSupport=false` 会直接损失异常堆栈；`DebuggerSupport=false`、`EventSourceSupport=false` 和 HTTP Activity 传播关闭也会削弱调试、事件诊断和分布式追踪。功能 smoke 通过不能抵消线上故障不可诊断的成本。

`UseSystemResourceKeys=true` 还会把部分系统异常文本替换为资源键，对结构化错误码无影响，但人工错误信息的可读性可能下降。

### 5.3 动态 AI/Skill 路径仍有残余风险

本轮已验证非空 Skill 的发现、解析、显示和校验，但没有真实执行 MAF Skill 调用。解除 5 个 AI Framework 整程序集保护后，任何仅由运行时反射发现、且未被静态分析看到的冷路径仍可能缺失。

### 5.4 其他既有边界

- 当前工作树存在未提交修改，正式归档应在确定的 Git 提交上重建。
- 自定义 `MADORIN_CONFIG_DIR` 无法修改 ACL 时的未处理异常仍然存在，和 AOT 裁剪无关。
- 其他真实 Provider、真实 Skill 工具循环以及 `.csx`、`.sh` 解释器的测试仍未执行。
- 发布目录可能残留前一次构建的 PDB；交付必须采用本次白名单 ZIP，不应直接压缩整个 publish 目录。

## 6. 建议

1. 保留 `aot-safe-size` 作为默认生产候选和明确回退点。
2. 将 `aot-aggressive` 作为极限体积实验包；DeepSeek 普通成功链已补验，仍需完成生产 Provider 矩阵和真实 Skill 调用后再决定是否放行。
3. 若希望兼顾体积和可诊断性，下一档建议只解除 5 个 AI roots，保留栈追踪、Debugger、EventSource 和 HTTP Activity。现有 A/B 数据预计必要运行文件约为 51.14 MiB，比当前激进版多约 2.94 MiB，但生产风险更低。
4. CI 中为带 `IlcFoldIdenticalMethodBodies=true` 的冷发布预留超过 3 分钟的链接时间，并继续按 exe SHA-256 和 ZIP SHA-256 登记。

## 7. P7 最终对齐（当前结论）

前文 `roots-stack-diagnostics-off` 是更早的多开关极限实验，保留用于历史对照，不再代表当前激进候选。P7 将变量拆开后得到以下最终分层：

| 版本 | 配置 | exe MiB | 运行集 MiB | ZIP MiB | 定位 |
| --- | --- | ---: | ---: | ---: | --- |
| B1 | Size + Full roots | 53.8755 | 55.6982 | 21.4283 | 默认生产版 |
| B2.3 | Size + None roots | 49.3203 | 51.1431 | 19.7385 | 激进候选，保留 StackTraceSupport |
| B3 | B2.3 + StackTraceSupport=false | 46.4873 | 48.3101 | 18.7514 | experimental，不作默认 |

B2.3 相对 B1 的 exe 再减少 4.5557 MiB（8.46%），且 P0-P6、Skills 元数据、Provider 失败/取消链、Expert/Meeting/双 Agent Work、解压后 P0/P5/P6 均通过。B2.3 ZIP SHA-256 为 `7156b94818e55750bb478ea173b521df5065defe954f35f80faacf7c45be8e62`。

B3 只额外关闭 `StackTraceSupport`，未叠加关闭 EventSource、HTTP Activity 或资源文本等开关；它相对 B2.3 再减少 2.8330 MiB，但明确损失异常堆栈。B3 ZIP SHA-256 为 `333b5bb161eee27620a3e522cf94c6afb221d50df509508f5f6ba6fcbc8579d0`，只允许以 `experimental` 名称分发。

当前机器未安装 7z/7zz/UPX，因此本轮只有标准 ZIP Optimal 的可复核数字；正式发行不采用 UPX。`deepseek/deepseek-v4-pro` 普通成功响应已经补验，但真实模型选择 Skill、其他 Provider 和生产负载仍未覆盖；加上 B2.3 解除了整包 roots，故它仍不替代 B1 默认生产版。
