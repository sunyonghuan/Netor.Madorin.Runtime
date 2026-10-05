# Runtime AOT 体积膨胀排查与依赖瘦身方案

> 文档状态：已执行并完成 P7 对齐复检
>
> 编写日期：2026-07-27
>
> 修订原则：无用直接依赖即使不降低 exe 体积也应删除；裁剪收益必须使用同一代码、SDK、RID 和发布参数实测；不要批量 `ExcludeAssets="all"` 破坏三方包依赖闭包。
>
> 二次实测补充：当前 AOT 配置存在属性名和 MSBuild 导入时序问题；修正为真正生效的 `OptimizationPreference=Size` 后，exe 已实测降至 53.8760 MiB。
>
> 对齐执行文档：[P7 NativeAOT 体积压缩与激进裁剪执行步骤](../控制台/执行/08-P7-AOT体积压缩与激进裁剪执行步骤.md)。本文件保留分析和历史实验，P7 文件负责本轮基线、候选、进度与发布证据。
>
> P7 最终实测：B1 Full roots 53.8755 MiB exe（默认正式版）；B2.3 None 49.3203 MiB（激进候选）；B3 None + StackTraceSupport=false 46.4873 MiB（实验版）；MConsole Size AOT 8.7734 MiB。53.8760 MiB 的先前 B1 候选保留为 A/B 历史值。

本文冻结 AOT 体积瘦身的安全实施顺序、可移除项、风险边界和实测体积基线。本轮只改文档，不修改 C# 源码、项目文件或发布配置；后续按阶段实施并逐阶段验收。

---

## 1. 结论

当前 CLI Native AOT exe 为 **78,359,552 字节，即 74.7295 MiB**。原文使用 MB 表述，但 PowerShell `1MB` 实际按 1 MiB（1,048,576 字节）计算，后文统一使用 MiB。

评审确认：

1. `Microsoft.Extensions.Hosting` 在生产源码中没有 `HostBuilder`、`IHost`、`IHostedService`、`Host.Create*`、`CreateDefaultBuilder` 或 `AddHostedService` 使用；单独移除该 PackageReference 后，Release 全量重编译为 `0 warnings / 0 errors`。它应当删除，即使 Native AOT exe 实测收益为 **0 字节**。
2. Server 项目的 `Microsoft.Agents.AI.OpenAI` 直接引用可以删除，但 OpenAI provider 项目的同名引用不能删除。`OpenAIProviderAdapter` 使用该包提供的 `AsIChatClientWithStoredOutputDisabled()`，并依赖它带入的 `OpenAI` 与 `System.ClientModel` 类型；直接删除会产生 `CS0234` / `CS0246` 编译错误。
3. `publish/rd.xml` 与 `TrimmerRootAssembly` 对同一批 AI 程序集做了重叠的整包保护；两种保护的收益不能重复相加，但它们不是 70+ MiB 的首要原因。
4. `Directory.Build.props` 写的是无效属性 `IlcOptimizationPreference`，.NET 10.0.9 Native AOT targets 实际读取 `OptimizationPreference`。使用真正生效的 `OptimizationPreference=Size` 后，exe 从 **74.7295 MiB** 降至 **53.8760 MiB**，减少 **20.8535 MiB（27.91%）**。
5. 推荐的低风险目标是 **53-55 MiB exe**；加上 `e_sqlite3.dll` 后解压运行文件为 **55.6987 MiB**，按当前发布脚本同等压缩方式生成的 zip 为 **21.4284 MiB**。若完整 Native AOT 业务验收证明 5 个 AI 整包保护均可取消，激进 exe 下界为 **49.3203 MiB**。

需要修正的关键点有五个：

1. `publish/rd.xml` 把 5 个程序集标记为 `Dynamic="Required All"`。
2. `Directory.Build.props` 同时把同一批程序集加入 `TrimmerRootAssembly`，这会继续把它们作为裁剪根；所以只改 `rd.xml` 不够。
3. 直接依赖是否可删必须按实际符号来源和编译验证判断，不能只看 `using` 的命名空间文字。
4. `IlcOptimizationPreference` 必须改为 .NET 10 targets 实际读取的 `OptimizationPreference`；否则 `Speed` 和 `Size` 都不会进入 ILC 响应文件。
5. `Directory.Build.props` 中依赖 `$(OutputType) == 'Exe'` 的 PropertyGroup 在 CLI 项目主体设置 `OutputType` 之前求值，导致 `DebugType=none`、`IlcGenerateStackTraceData=false` 等文本配置没有生效。应把 CLI 专属发布属性移到 CLI 项目，或改为不依赖后置属性的项目名条件。

本方案改为五阶段：

| 阶段 | 操作 | 风险 | 目标 |
| --- | --- | --- | --- |
| P0-CFG | 修正 AOT 属性名和 CLI 发布属性的求值位置，启用真正的 `OptimizationPreference=Size` | 低-中 | exe 实测降至 53.8760 MiB；用性能基线确认 `--Os` 代价 |
| P0 | 删除未使用的 Hosting 和 Server 上重复的 Agents OpenAI 直接引用 | 低 | 清理无用直接依赖，不以 exe 收益为前提 |
| P1 | 解除 `Microsoft.Agents.AI.OpenAI` 的整包保护 | 低-中 | 释放 Agents OpenAI 未达代码 |
| P2 | 解除 `Microsoft.Agents.AI` / `.Abstractions` 的整包保护 | 中 | 让 trimmer 保留静态可达代码，并用 Skills AOT 验收兜底 |
| P3 | 可选：解除 `Microsoft.Extensions.AI` / `.Abstractions` 的整包保护 | 中-高 | 在完整业务验收通过后进一步压缩 |

不再把“批量排除 Humanizer、Protobuf、Tensors 等转引用包”作为默认方案。这些包虽然项目源码没有 `using`，但可能被三方库内部冷路径使用；批量 `ExcludeAssets="all"` 容易造成运行时 `FileNotFoundException`、`TypeLoadException` 或 `MissingMethodException`。

---

## 2. 当前证据

| 文件 | 证据 | 结论 |
| --- | --- | --- |
| `Directory.Build.props` | 文本配置为 `IlcOptimizationPreference=Speed`；.NET 10.0.9 targets 只读取 `OptimizationPreference`，当前 ILC 响应文件无 `--Ot` / `--Os` | 当前所谓 Speed 配置未生效；改为 `OptimizationPreference=Size` 后响应文件出现 `--Os` 和 `--dehydrate` |
| `Directory.Build.props` | Release PropertyGroup 依赖 `$(OutputType) == 'Exe'`；最终求值显示 `DebugType=portable`、`NativeDebugSymbols=true`、`IlcGenerateStackTraceData` 为空 | CLI 专属属性组受导入时序影响；Native PDB 仍会生成，`IlcGenerateStackTraceData=false` 也未生效 |
| `publish/rd.xml` | 5 个程序集 `Dynamic="Required All"` | 会阻止这些程序集内未用类型/成员被裁剪 |
| `Directory.Build.props` | 同 5 个程序集被加入 `TrimmerRootAssembly` | 只改 `rd.xml` 不能完全释放裁剪 |
| `Madorin.AI.Runtime.Server.csproj` | 直接引用 `Microsoft.Extensions.Hosting`，源码无 Hosting API 使用；单独删除后 Release 全量重编译通过 | 确认可以删除；AOT exe 实测收益为 0 字节，但依赖清理仍有价值 |
| `Madorin.AI.Runtime.Server.csproj` | 直接引用 `Microsoft.Agents.AI.OpenAI`，实际实现位于被引用的 OpenAI provider 项目 | Server 上的重复直接引用可以删除 |
| `Madorin.AI.Runtime.Providers.OpenAI.csproj` | `OpenAIProviderAdapter` 使用 `OpenAI`、`System.ClientModel` 和 `AsIChatClientWithStoredOutputDisabled()` | provider 上的 `Microsoft.Agents.AI.OpenAI` 必须保留；删除后编译失败 |
| `MafSkillsAgentFactory.cs` | 使用 `ChatClientAgentOptions`、`AgentSkillsProvider`、`AsAIAgent()` | `Microsoft.Agents.AI` 仍需保留，但保护范围应缩小 |
| 旧优化模式 Native AOT A/B | 基线 74.7295 MiB；安全清理和 OpenAI root 调整后 74.5908 MiB；解除全部 Agents root 后 73.2100 MiB；解除全部 5 个 AI root 后 68.6807 MiB | 仅作为 roots 相对收益的历史对照；新目标必须在 Size 下重新测量 |
| 正确的 Size A/B | 保留全部 roots 时 53.8760 MiB；解除全部 5 个 AI roots 时 49.3203 MiB；关闭栈追踪时 50.9160 MiB | 首要收益来自正确启用 `OptimizationPreference=Size`；roots 和栈追踪属于后续可选项 |
| Native AOT map | `System.Text.Json` 约 17.186 raw MiB、OpenAI 约 5.652 raw MiB、`Netor.Anthropic` 约 3.537 raw MiB；泛型类型与方法表、反射映射和嵌入元数据占用明显 | map 数值是链接前节点，不能直接当最终节省量；但足以确定大型 JSON/Provider SDK 才是长期重点 |
| CLI 发布闭包 | 单个 exe 静态合入 29 个项目、10 个 provider、SQLite、MAF/MEAI、OpenAI SDK、Netor.Anthropic 和 System.CommandLine | 该程序虽是控制台入口，但不是小型单功能控制台；70+ MiB 不能仅按 CLI 命令代码判断 |

---

## 3. 可去掉或可修改项评估表

| 类别 | 位置 | 项 | 建议动作 | 风险 | 实测或边界 |
| --- | --- | --- | --- | --- | --- |
| AOT 配置 | `Directory.Build.props` / CLI 项目 | `IlcOptimizationPreference` | 改为 `OptimizationPreference=Size`，并确保在 CLI 项目中实际求值 | 低-中：功能语义不变；可能牺牲部分热点吞吐 | exe 实测减少 20.8535 MiB（27.91%） |
| 构建配置 | `Directory.Build.props` | `$(OutputType) == 'Exe'` 条件 | 将 CLI 专属属性移入 CLI `.csproj`，或改用明确的项目名条件 | 低：修复配置意图；需防止影响其他可执行项目 | 可避免约 395 MiB Native PDB 和项目 PDB 的无效生成；发布脚本目前会在打包前删除它们，因此不降低最终 zip |
| 诊断功能 | CLI 项目 | `StackTraceSupport=false` | 默认不采用，仅极限体积发行版评估 | 高：异常堆栈和生产故障定位能力下降 | 在 Size + roots 基础上额外减少 2.9600 MiB |
| 直接包引用 | `src/Madorin.AI.Runtime.Server/Madorin.AI.Runtime.Server.csproj` | `Microsoft.Extensions.Hosting` | 确认删除 PackageReference | 低：无 Hosting API 使用；单独删除后 Release 全量重编译通过 | exe 差值 0 字节；依赖清理仍应执行 |
| 直接包引用 | `src/Madorin.AI.Runtime.Server/Madorin.AI.Runtime.Server.csproj` | `Microsoft.Agents.AI.OpenAI` | 删除重复 PackageReference | 低：OpenAI provider 项目仍保留真实依赖 | 预计 exe 差值 0；减少直接依赖声明 |
| 直接包引用 | `src/Providers/Madorin.AI.Runtime.Providers.OpenAI/Madorin.AI.Runtime.Providers.OpenAI.csproj` | `Microsoft.Agents.AI.OpenAI` | **保留** PackageReference | 删除会导致 `OpenAI`、`System.ClientModel` 和扩展方法编译失败 | 不属于可删除项 |
| 裁剪根 | `publish/rd.xml` + `Directory.Build.props` | `Microsoft.Agents.AI.OpenAI` | 同阶段删除 `Dynamic="Required All"` 和 `TrimmerRootAssembly` | 低-中：包仍保留，仅释放未达代码 | 旧优化模式累计实测节省 0.1387 MiB；Size 下需重新 A/B |
| 裁剪根 | `publish/rd.xml` + `Directory.Build.props` | `Microsoft.Agents.AI` / `.Abstractions` | 同阶段解除整包保护；先不添加猜测性类型 root | 中：必须使用 Native AOT Skills 调用验收 | 旧优化模式无额外 descriptor 时累计节省 1.5195 MiB；Size 下需重新 A/B |
| 裁剪根 | `publish/rd.xml` + `Directory.Build.props` | `Microsoft.Extensions.AI` / `.Abstractions` | P3 可选解除整包保护 | 中-高：影响所有 provider 的公共 AI 抽象路径 | Size 下解除全部 5 个 AI roots 后累计实测节省 4.5557 MiB |

---

## 4. 不建议直接去掉的转引用包

以下包可以作为“观察对象”，但不作为默认 `ExcludeAssets="all"` 目标。只有在 P0-P3 后仍被完整保留、且逐包实验通过 publish 与运行验证时，才考虑处理。

| 包 | 当前来源 | 为什么不能直接排除 | 建议 |
| --- | --- | --- | --- |
| `Humanizer.Core` | `Microsoft.Agents.AI` 转引用 | 可能被 Agents.AI 内部描述、错误消息或 skill 元数据路径使用 | 让 trimmer 自动裁；不手动排除 |
| `Google.Protobuf` | `Microsoft.Agents.AI` 转引用 | 可能被 Agents.AI 内部协议或 schema 路径使用 | 让 trimmer 自动裁；不手动排除 |
| `System.Numerics.Tensors` | MEAI / VectorData 转引用 | 可能被 AI 抽象或 tokenizer 冷路径使用 | 先观察 map，不手动排除 |
| `Microsoft.ML.Tokenizers` | OpenAI SDK 转引用 | SDK 可能在 token 统计或模型路径使用 | 不手动排除 |
| `System.Memory.Data` | OpenAI SDK / System.ClientModel 转引用 | SDK 内部可能使用 BinaryData 类能力 | 不手动排除 |
| `Microsoft.Extensions.*Debug/EventLog/EventSource` | Hosting / logging 转引用 | 去 Hosting 后可能自然消失；其他 logging 路径仍可能引入部分包 | 通过 P0 删除 Hosting 后观察，不强制排除 |
| `Microsoft.Extensions.FileProviders.*` | Hosting / config 转引用 | 去 Hosting 后部分会自然消失；Json 配置可能仍需要文件扩展 | 只删除 Hosting，不强排除 |

---

## 5. 分阶段实施方案

### P0-CFG：先修正真正生效的 AOT 发布配置

改动范围：

1. 将 `IlcOptimizationPreference` 改为 `OptimizationPreference`，默认发行版取值 `Size`。
2. 不再让 CLI 专属发布属性依赖 `Directory.Build.props` 中过早求值的 `$(OutputType) == 'Exe'`。优先把 `PublishAot`、`SelfContained`、`JsonSerializerIsReflectionEnabledByDefault`、`OptimizationPreference`、`DebugType` 和 `DebugSymbols` 放到 CLI 项目的 Release PropertyGroup；若必须集中管理，则使用明确的项目名条件。
3. 删除或更正无效的 `IlcGenerateStackTraceData=false`。需要关闭栈追踪时，.NET 10 对应的是 `StackTraceSupport=false`，但默认发行版应保留栈追踪。
4. `EventSourceSupport` 在 Native AOT targets 中默认已经是 `false`，`DebuggerSupport` 默认也会编译为不支持，`UseSizeOptimizedLinq` 默认已经是 `true`；不要重复增加没有收益的开关。
5. 不把 `UseSystemResourceKeys=true`、`HttpActivityPropagationSupport=false` 作为默认项。map 中资源数据本身很小，而两者会分别降低系统错误消息可读性和分布式追踪能力。

验收：

- ILC 响应文件必须同时出现 `--Os` 和 `--dehydrate`，不能只看项目文件文本。
- 保留全部 5 个 AI roots 时 exe 应接近实测的 53.8760 MiB。
- 发布后的 Native AOT exe 完成 `--help`、`version`、`doctor`、SQLite 初始化、Runtime 启动和 IPC 验收。
- 对启动时间、普通请求和流式请求做发布前后基线；`Size` 可能降低部分热点吞吐，功能正确不等于性能完全无变化。
- 发布目录可以生成符号供归档，但最终安装包继续排除 `.pdb`；若设置 `DebugType=none`，确认生产故障诊断策略已有替代方案。

### P0：删除确认无用或重复的直接依赖

改动范围：

1. 删除 Server 项目的 `Microsoft.Extensions.Hosting` PackageReference。该项目没有 Hosting API 使用，且单独删除后的 Release 全量重编译已经通过。
2. 删除 Server 项目的 `Microsoft.Agents.AI.OpenAI` PackageReference。Server 已通过项目引用依赖 OpenAI provider，不需要重复声明该包。
3. **保留** OpenAI provider 项目的 `Microsoft.Agents.AI.OpenAI` PackageReference。它提供当前代码实际调用的 OpenAI Responses 转 `IChatClient` 扩展。
4. 不预先添加 Hosting 带入的整套 Extensions 子包。Server 没有直接使用这些 API；其他项目继续声明各自真实需要的依赖。若后续编译暴露缺失，再按实际符号来源补充精确 PackageReference。

验收：

- `dotnet build src/Madorin.AI.Runtime.Cli/Madorin.AI.Runtime.Cli.csproj -c Release --no-incremental` 为 `0 warnings / 0 errors`。
- `dotnet publish -c Release -r win-x64 --self-contained true` 通过。
- CLI `--help`、`doctor`、`serve` 启动与 client 连接通过。
- 不要求 exe 必须下降；本阶段的目标是删除无用依赖并收紧项目依赖边界。

### P1：解除 `Microsoft.Agents.AI.OpenAI` 的整包保护

改动范围：

1. 保留 OpenAI provider 项目的 `Microsoft.Agents.AI.OpenAI` PackageReference。
2. 删除 `publish/rd.xml` 中 `Microsoft.Agents.AI.OpenAI` 的整包 `Dynamic="Required All"`。
3. 同时删除 `Directory.Build.props` 中对应的 `TrimmerRootAssembly`，避免另一套 root 继续保留整包代码。

验收：

- Native AOT publish 无 trimming/AOT 警告。
- OpenAI provider 的 Responses、流式响应和工具调用 smoke test 通过。
- 在 `OptimizationPreference=Size` 的 53.8760 MiB 基线上重新记录精确字节数；74.5908 MiB 仅保留为旧的默认优化模式历史对照，不能作为新目标。

### P2：解除 `Microsoft.Agents.AI` / `.Abstractions` 的整包保护

改动范围：

1. 保留 `Microsoft.Agents.AI` 包引用，因为 `MafSkillsAgentFactory` 仍使用它。
2. 删除 `rd.xml` 中 `Microsoft.Agents.AI`、`Microsoft.Agents.AI.Abstractions` 的整包保护。
3. 同时删除 `Directory.Build.props` 中这两个程序集的 `TrimmerRootAssembly`。
4. 第一轮不添加猜测性的类型级 root，让 trimmer 按静态可达关系保留实际使用代码。
5. 如果 Native AOT 业务测试出现明确的反射元数据缺失，再根据失败类型和成员添加最小 descriptor；禁止恢复无证据的整包保护。

验收：

- Skills catalog 初始化通过。
- 使用发布后的 Native AOT exe 执行一次带 Skills 的 agent invocation，并确认触发 `AgentSkillsProvider`。
- `load_skill`、script runner 回调、工具审批和结果返回通过。
- OpenAI、Anthropic、DeepSeek/Kimi 等经过 MAF 的 provider streaming 仍能返回响应更新。
- Expert、Meeting、Work 三种模式至少各完成一次带 Skills 的调用。
- 在 `OptimizationPreference=Size` 下重新 A/B；73.2100-74.5908 MiB 仅是旧优化模式的历史边界，不能与 Size 收益直接相加或套比例估算。

### P3：可选解除 `Microsoft.Extensions.AI` / `.Abstractions` 的整包保护

进入条件：P0-P2 全部验收通过，并已归档各阶段 AOT 产物、大小和 smoke test 结果。

改动范围：

1. 删除 `rd.xml` 中 `Microsoft.Extensions.AI`、`Microsoft.Extensions.AI.Abstractions` 的整包保护。
2. 同时删除 `Directory.Build.props` 中对应的 `TrimmerRootAssembly`。
3. 不删除两个包本身，不对其转引用使用 `ExcludeAssets="all"`。

验收：

- 完整 provider、Skills、三模式、工具调用、session resume/recovery 和 IPC 测试全部使用 Native AOT exe 通过。
- `OptimizationPreference=Size` 下 exe 实测目标约 49.3203 MiB；68.6807 MiB 是旧优化模式下的历史对照。
- 任一路径出现缺类型、缺成员或行为回归，立即回退 P3；不要用批量 root 掩盖具体问题。

---

## 6. 修改后体积实测与目标

以下数据来自同一工作区代码、.NET SDK 10.0.301、ILCompiler 10.0.9、`win-x64`、Release、self-contained 的 Native AOT A/B 发布。后续代码或 SDK 变化后必须重新建立基线。

| 变体 | 主要变化 | exe 大小 | 相对 74.7295 MiB 基线累计节省 | 风险结论 |
| --- | --- | --- | --- | --- |
| 当前默认发布 | 5 个 AI roots；`OptimizationPreference` 实际为空 | 74.7295 MiB | - | 已实测，78,359,552 字节 |
| P0-CFG 推荐基线 | 保留全部 roots；正确设置 `OptimizationPreference=Size` | 53.8760 MiB | 20.8535 MiB（27.91%） | 低-中；功能语义不变，需验证热点性能 |
| 极限诊断变体 | Size + roots + `StackTraceSupport=false` | 50.9160 MiB | 23.8135 MiB | 高；额外节省 2.9600 MiB，但默认不采用 |
| P3 激进裁剪 | Size + 解除全部 5 个 AI roots | 49.3203 MiB | 25.4092 MiB（34.00%） | 中-高；P0-P6、Skills、Provider 失败/取消链及三模式已验证；DeepSeek 真实非流式成功链已补验 |

P0-CFG 推荐变体及 P7 候选已验证 `--help`、`version --json`、服务进程初始化、SQLite schema 16/WAL/完整性检查、已认证 IPC、Skills 元数据路径、P0-P6 和 Expert/Meeting/Work 的 Provider 失败/取消链。2026-07-27 又使用 `deepseek/deepseek-v4-pro` 补验 B1 非流式与流式、B2.3 非流式真实成功响应，三次均返回 `OK` 且退出码为 0。尚未覆盖其他 Provider、模型实际选择 Skill 和生产 streaming 负载，因此 B1 保持默认正式版，B2.3 只作为激进候选。

推荐基线的解压运行文件还包括 1,911,296 字节的 `e_sqlite3.dll`，因此 exe + SQLite 合计 **55.6987 MiB**。按发布脚本的 Optimal zip 压缩方式实测为 **21.4284 MiB**。三种数字必须分开记录：

1. exe 体积：53.8760 MiB。
2. 解压后的必要运行文件：55.6987 MiB。
3. 传输 zip：21.4284 MiB。

结论不再是“必须接受 70+ MiB”。在不移除功能、不关闭栈追踪、继续保留全部 AI roots 的前提下，当前代码已经证明可降至约 **53.9 MiB exe**。若要稳定进入 50 MiB 以下，应优先完成 roots 动态路径验收；再向下则需要 provider 发行风味拆分或 SDK 精简，不能通过随意排除转引用包实现。

---

## 7. 根据当前代码识别的其他瘦身方向

当前 Server 静态引用 10 个 provider，CLI 最终合入 29 个项目。Aliyun、Azure OpenAI、Gemini、GLM、Ollama 通过 OpenAICompatible 项目继续引用 OpenAI provider；Kimi、DeepSeek 也使用 `Microsoft.Extensions.AI.OpenAI`。因此优化重点应放在 provider/SDK 边界，而不是 CLI 命令解析代码。

| 优先级 | 方向 | 预计收益 | 运行影响与建议 |
| --- | --- | --- | --- |
| 1 | 正确启用 `OptimizationPreference=Size` | 已实测 20.8535 MiB | 默认采用；功能语义不变，但发布前比较启动、普通响应和 streaming 性能 |
| 2 | 精细解除 5 个 AI assembly roots | Size 基线上已实测最多 4.5557 MiB | 需要所有 provider、Skills、工具调用和三模式 Native AOT 验收；不能只凭 `--help` 放行 |
| 3 | 按 provider 发行风味构建，例如 Full、OpenAI-compatible、Anthropic | 单 provider/单协议风味规划区间约 44-50 MiB exe，尚未实测 | 对所选风味运行无影响，但其他 provider 不再可用；Native AOT 不适合运行时动态加载普通托管插件，优先使用构建时条件 ProjectReference/DI 注册生成不同发行版 |
| 4 | 精简或替换大型 provider SDK | map 显示 OpenAI 约 5.652 raw MiB、`Netor.Anthropic` 约 3.537 raw MiB；最终可省量不能直接相加，专项可先按 3-8 MiB 目标评估 | 高改造成本；要自行承担协议演进、streaming、工具调用、错误模型、重试和 AOT JSON 上下文兼容 |
| 5 | 可选的无 SQLite 发行风味 | 外部 `e_sqlite3.dll` 固定 1.8228 MiB，另有约 0.562 raw MiB 的项目节点；规划收益约 2-3 MiB | 会失去或改变 session、恢复、数据库维护等能力，不适合作为默认版 |
| 6 | 收紧 JSON/schema 使用面 | `JsonSchema.Net` 约 0.279 raw MiB；`System.Text.Json` 是最大节点来源但也是所有协议和持久化的基础 | 先检查过宽的 source-generated context 和重复 DTO，不要替换 `System.Text.Json`；收益需逐 context A/B |
| 7 | 禁止无效生成生产 PDB | 当前一次 publish 生成约 378.8 MiB PDB，随后被脚本删除 | 不降低最终 zip，但显著降低发布临时磁盘和 I/O；应通过修正 PropertyGroup 求值位置实现 |

明确不建议的方向：

1. 不重写 `System.CommandLine`。map 原始节点约 0.107 MiB，维护成本远高于收益。
2. 不把 framework-dependent 发布称为瘦身。它只是把 .NET Runtime 转移为机器前置条件；若仍随包分发 Runtime，总占用不会按小型 DLL 计算。
3. 不把 zip 的 21.4284 MiB 当成运行体积。它只降低下载和制品存储。
4. 删除 Server 的 `Microsoft.Extensions.Hosting` 直接引用后，该包仍可能由 Agents/Extensions 依赖链转引入；这不影响“无用直接引用应删除”的结论，也解释了 AOT exe 收益为 0 字节。

综合预估：

- **默认全功能版**：53-55 MiB exe，推荐目标 53.8760 MiB。
- **全功能激进裁剪版**：约 49-51 MiB exe，前提是完整 Native AOT 动态路径验收。
- **单 provider/单协议发行版**：规划区间 44-50 MiB exe，需先实现构建时风味并实测，不能作为已验证承诺。
- **关闭栈追踪的极限版**：50.9160 MiB（仍保留 roots），不建议生产默认使用。

---

## 8. 最终验收矩阵

| 验收项 | 必须通过的原因 |
| --- | --- |
| `dotnet msbuild -getProperty` + 检查 `madorin.ilc.rsp` | 确认 `OptimizationPreference=Size` 真实求值，响应文件包含 `--Os` / `--dehydrate`，避免再次出现“项目文件写了但编译器没收到” |
| Release `--no-incremental` build，0 warnings / 0 errors | 验证 PackageReference 删除没有被增量缓存掩盖 |
| `dotnet list package --include-transitive` 前后对比 | 验证 Hosting 顶级引用已删除，并确认剩余 Hosting.Abstractions 等包的真实转引用来源 |
| `dotnet publish -c Release -r win-x64 --self-contained true` | 验证 Native AOT 和 trimmer 通过，且没有 IL trimming/AOT 警告 |
| 发布后的 Native AOT exe 执行 `--help` / `version` / `doctor` | 验证基础启动、配置读取和 provider 探测；不能用 `dotnet run` 代替 |
| `serve` 启动 + client 连接 | 验证 RuntimeServer 生命周期和 IPC |
| provider smoke test | 验证 OpenAI、Anthropic、OpenAI-compatible、DeepSeek/Kimi 等适配器未被裁坏 |
| 带 Skills 的真实调用或本地协议 mock | 验证 `AgentSkillsProvider`、`AsAIAgent()`、streaming、`load_skill` 和 script runner 回调 |
| Expert / Meeting / Work 模式 smoke | 验证不同编排路径使用 MAF 时未因裁剪产生行为差异 |
| session resume/recovery 测试 | 验证 SQLite、JSON source generation、持久化路径未受影响 |
| exe 字节数和 map 对比 | 验证每阶段真实收益；归档 commit、SDK、RID、发布参数和产物哈希 |
| 启动、普通请求、streaming 的性能基线 | `OptimizationPreference=Size` 可能用吞吐换体积；功能正确不能替代性能验收 |

放行标准：

1. P0-CFG 在 ILC 参数确认、Native AOT 基础功能和性能基线通过后可独立合入；推荐 exe 门槛为不高于 55 MiB。
2. P0 可在 Release build、Native AOT publish 和基础 CLI/IPC 验收通过后独立合入；不设置体积下降门槛。
3. P1-P3 必须使用发布后的 Native AOT exe 完成对应动态路径验收，只有托管测试通过不能证明裁剪安全。
4. 任一阶段发生 `FileNotFoundException`、`TypeLoadException`、`MissingMethodException`、反射成员缺失或 provider 行为差异，都不得进入下一阶段。
5. 每阶段记录精确字节数，不把 `rd.xml` 与 `TrimmerRootAssembly` 的重叠收益重复计算，也不把链接前 map raw MiB 当成最终收益。

---

## 9. 回退策略

每阶段必须单独提交或至少单独保存 diff。出现问题时按阶段回退：

1. P0-CFG 性能回归：先恢复默认 `OptimizationPreference`，保留已修正的属性求值位置；不要通过关闭栈追踪补偿体积。
2. P0 失败：只恢复 Server 的 `Microsoft.Extensions.Hosting` 和/或重复的 `Microsoft.Agents.AI.OpenAI` PackageReference；不要改动 provider 上必须保留的包。
3. P1 失败：恢复 `Microsoft.Agents.AI.OpenAI` 的 `rd.xml` 整包保护和 `TrimmerRootAssembly`。
4. P2 失败：恢复 `Microsoft.Agents.AI` / `.Abstractions` 的整包保护；若只有单一反射成员缺失，优先回退后另行评审最小 descriptor，不在故障版本上盲目扩大 root。
5. P3 失败：恢复 `Microsoft.Extensions.AI` / `.Abstractions` 的整包保护。
6. 转引用观察实验失败：撤销最后一个实验，不要批量 `ExcludeAssets="all"`，也不要把失败归因到无关的 Hosting 删除。
