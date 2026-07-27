# P7 · NativeAOT 体积压缩与激进裁剪执行步骤

> 阶段状态：已完成并复检
>
> 当前进度：100%
>
> 基线日期：2026-07-27
>
> 阶段目标：保留当前可正式运行版本作为不可覆盖基线，在独立输出目录中依次完成 Size 优化、传输压缩和程序集 roots 激进裁剪，通过同一验收矩阵后再决定是否发布。
>
> 来源策划：[AOT 体积膨胀排查与依赖瘦身方案](../../策划/03-AOT体积膨胀排查与依赖瘦身方案.md)
>
> 最终选择：默认正式版 = B1 Size + Full roots；激进候选版 = B2.3 Size + None；实验下载版 = B3 Size + None + StackTraceSupport=false。

P7 是 P0-P6 完成后的独立优化阶段。体积下降不能覆盖功能正确性、诊断能力和安全边界；任何候选版都必须保留自己的参数、产物、哈希、报告和回退点，禁止覆盖 B0 基线。

## 1. 当前版本 B0 基线

构建环境：

```text
仓库：E:\Netor.me\Madorin\Netor.Madorin.Ai.Runtime
SDK：10.0.301
目标框架：net10.0
配置：Release
RID：win-x64
部署：self-contained NativeAOT
工作区状态：存在未提交修改，因此必须同时记录源码 diff，不把 HEAD 单独视为完整版本标识
```

当前 Release 可执行项目公共属性：

```xml
<PublishAot>true</PublishAot>
<SelfContained>true</SelfContained>
<JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
  <OptimizationPreference>Size</OptimizationPreference>
<IlcGenerateStackTraceData>false</IlcGenerateStackTraceData>
<DebugType>none</DebugType>
<DebugSymbols>false</DebugSymbols>
```

CLI 当前还同时使用 `publish/rd.xml` 和 `TrimmerRootAssembly` 保护以下程序集：

```text
Microsoft.Agents.AI
Microsoft.Agents.AI.Abstractions
Microsoft.Agents.AI.OpenAI
Microsoft.Extensions.AI
Microsoft.Extensions.AI.Abstractions
```

本次已经实际发布并完成 P0/P5/P6 NativeAOT 验收的产物：

| 产物 | 字节数 | MiB | SHA-256 |
| --- | ---: | ---: | --- |
| `madorin.exe` | 78,359,552 | 74.7295 | `e93c94d8b9e30e6c542186995454f40dc8c03e7f7cff32d9d88f633ff5179490` |
| `e_sqlite3.dll` | 1,911,296 | 1.8228 | `aabf85d7a8b416fb15203cb754fcfc9858c8f1dd3bbc7eab82335f6c362ba0d6` |
| `Madorin.AI.Runtime.MConsole.exe` | 11,949,056 | 11.3955 | `cc2dad154514e25d6bd81e86c2558e7d4720ea7060f9189a7804359b4ef35756` |

CLI 必要运行文件 `madorin.exe + e_sqlite3.dll` 合计：

```text
80,270,848 字节
76.5523 MiB
```

已通过的 B0 证据：

```text
Debug 全解决方案构建：0 警告，0 错误
Debug 全解决方案测试：1137 通过，9 跳过，0 失败
Release 全解决方案构建：0 警告，0 错误
P0-P6 托管 smoke：全部 passed=True
CLI NativeAOT P0：passed=True
MConsole NativeAOT P5：passed=True
MConsole NativeAOT P6：passed=True
```

B0 保留规则：

1. 不覆盖 `.artifacts/madorin-cli-aot` 和 `.artifacts/mconsole-aot`。
2. 新候选版使用独立目录和独立清单。
3. 在开始修改前生成 `baseline-manifest.json`，记录 Git HEAD、dirty diff 哈希、SDK、RID、MSBuild 属性、文件哈希和验证结果。
4. 传输压缩包只包含运行必需文件；PDB、测试数据和中间目录不得计入正式包体积。
5. B0 只有在候选版通过全部门禁并明确批准后才能被新正式版替代。

## 2. 阶段任务总表

| 任务 | 当前进度 | 状态 | 产出 |
| --- | ---: | --- | --- |
| P7-01 固化 B0 版本清单 | 100% | 完成 | `.artifacts/p7-manifests/b0-speed-full.manifest.json` |
| P7-02 建立可重复体积测量脚本 | 100% | 完成 | `build/measure-aot-artifact.ps1` |
| P7-03 CLI 全功能 NativeAOT 回归门槛 | 100% | 完成 | B1/B2.3 动态路径与解压复验 |
| P7-04 MConsole NativeAOT 回归门槛 | 100% | 完成 | Size AOT 发布版 P0/P5/P6 |
| P7-05 性能与诊断对比门槛 | 100% | 完成 | 体积/诊断/风险分层结论 |
| P7-06 版本选择与发布规则 | 100% | 完成 | B1 正式、B2.3 激进、B3 实验 |
| P7-07 B1 Size 安全候选 | 100% | 完成 | 53.8755 MiB exe + Full roots |
| P7-08 传输压缩 A/B | 100% | 完成 | ZIP Optimal 完成；7z/UPX 环境缺失 |
| P7-09 B2 roots 激进裁剪 | 100% | 完成 | B2.1/B2.2/B2.3 独立 A/B |
| P7-10 B3 极限实验候选 | 100% | 完成 | 46.4873 MiB experimental |

实际推进顺序：先完成 P7-01/P7-02，再以 P7-03 至 P7-06 作为统一门槛执行 P7-07；P7-07 通过后执行 P7-08，最后按风险逐步执行 P7-09/P7-10。版本选择始终回到 P7-06，不按任务编号机械跳过门槛。

## 3. P7-01 固化 B0 版本清单

当前进度：100%

执行步骤：

1. 读取 `git rev-parse HEAD`。
2. 保存 `git status --short` 和本任务相关 `git diff --binary` 的 SHA-256。
3. 保存 `dotnet --info`、ILCompiler 版本和 `win-x64` RID。
4. 使用 `dotnet msbuild -getProperty` 记录最终求值的 AOT、优化、调试、栈追踪和 globalization 属性。
5. 保存 CLI 与 MConsole 发布命令，不使用会自动递增版本号的脚本建立基线。
6. 枚举正式运行文件，记录逐文件字节数和 SHA-256。
7. 记录未压缩 exe、解压必要运行集和传输压缩包三种体积，禁止混用。
8. 把 P0-P6、全量测试和 NativeAOT smoke 结果写入清单。
9. 清单写入独立只读基线目录，不与候选版共用输出路径。

完成标准：

- 能仅依靠清单判断产物来自哪组源码、SDK 和参数。
- B0 文件哈希与本文件第 1 节一致。
- 后续候选版不会覆盖 B0。

执行记录：

```text
状态：完成
进度：100%
记录：HEAD=c87cad950d623bdfd8352a7de7149ce9c80c60b0；工作区 dirty；manifest=.artifacts/p7-manifests/b0-speed-full.manifest.json；exe=78,359,552 字节 / 74.7295 MiB / e93c94d8b9e30e6c542186995454f40dc8c03e7f7cff32d9d88f633ff5179490；运行集=80,270,848 字节 / 76.5522 MiB；ZIP=24,236,983 字节 / 23.1142 MiB / 0bf77938becf6d4b07cf64566a527d85451eb36d0fc7c3c9a0fc6fa836671144。
```

## 4. P7-02 建立可重复体积测量脚本

当前进度：100%

测量口径：

```text
ExecutableBytes：主 exe 字节数
RuntimeSetBytes：主 exe + 必需 native/runtime 文件
TransportBytes：正式压缩包字节数
BuildTransientBytes：PDB、obj/native 和链接中间文件，只用于构建成本分析
```

执行步骤：

1. 从全新候选输出目录发布，禁止从旧目录增量覆盖。
2. 按发布清单筛选运行文件，不把数百 MiB NativeAOT PDB 纳入传输包。
3. 对每个文件计算 SHA-256。
4. 生成 JSON 和 Markdown 两种报告。
5. 同时记录前一基线、绝对节省字节、MiB 和百分比。
6. 记录 publish 总耗时、峰值磁盘占用和输出文件数。
7. 对同一候选重复发布两次，确认尺寸差异和哈希是否可解释。
8. 报告中注明压缩工具、版本、算法、级别和命令行。

完成标准：

- 同一产物不会因显示精度不同产生互相矛盾的体积。
- 任何体积结论都能追溯到具体文件。
- 测量脚本不修改版本号和源码。

执行记录：

```text
状态：完成
进度：100%
记录：新增 build/measure-aot-artifact.ps1；输出目录 .artifacts/p7-manifests；已生成 b0/b1/b2-1/b2-2/b2-3/b3/mconsole 共 7 组 manifest+ZIP；PowerShell 5.1 UTF-8 no-BOM 兼容已验证。
```

## 5. P7-03 CLI 全功能 NativeAOT 回归门槛

当前进度：100%

每个 B1/B2 候选必须使用发布目录中的 `madorin.exe` 执行，不能用 `dotnet run` 代替：

1. `--help`、`version --json`、`doctor`、配置初始化和错误退出码。
2. P0-P6 全部 smoke，其中 P1-P3 必须覆盖真实 SDK/serve 生命周期。
3. 所有 Provider 的协议 mock 或真实受控 smoke，覆盖普通响应、streaming、工具调用和错误映射。
4. Skills catalog、`load_skill`、script runner、工具授权、审批和幂等结果查询。
5. Expert、Meeting、Work 三种模式。
6. session resume、Runtime 重启、GSN 重放、callback pending、凭据续期。
7. SQLite schema、WAL、backup/check/vacuum 和 `e_sqlite3.dll` 同级加载。
8. 多实例写锁、路径别名、只读并发、max-runs 和定向取消。
9. 中文工作区、中文参数、长路径、空格路径和 Unicode JSON。
10. secret 不进入 argv、环境报告、日志、历史、用例和崩溃输出。

完成标准：

- 候选版结果不低于 B0 的 `1137 通过、9 跳过、0 失败`。
- 所有 P0-P6 smoke 为 `passed=True`。
- 所有动态路径均由 NativeAOT exe 触发。

执行记录：

```text
状态：完成
进度：100%
记录：B1/B2.3 均通过 MConsole P0-P6 smoke；Debug/Release 全量测试均为 1137 通过、9 跳过、0 失败；B2.3 隔离假 Provider 验证 skill refresh/list/validate、Expert=ProviderRequestFailed/RunCancelled、Meeting=RunCancelled/RunExecutionFailed、双 Agent Work=RunExecutionFailed，无 FileNotFoundException/TypeLoadException/MissingMethodException。2026-07-27 使用用户级 `deepseek`（协议 `DeepSeek`、模型 `deepseek-v4-pro`）补跑真实成功链：B1 非流式、B1 流式和 B2.3 非流式均返回 `OK`，退出码均为 0；配置验证 `valid=true`。执行记录不保存 API Key。
```

## 6. P7-04 MConsole NativeAOT 回归门槛

当前进度：100%

执行步骤：

1. 从候选 MConsole NativeAOT exe 显示 `--help`。
2. 显式绑定同一候选 CLI，避免定位器误选 B0 或 `bin/.../native` 中间产物。
3. 运行 P0-P6 smoke。
4. 进入菜单 9-13，验证历史导出、用例回放、夹具清理、实时监控和 P6 并发入口。
5. 验证 JSON Lines 仍是一条记录一行，source-generated JSON 不缺类型。
6. 验证 owned serve、SDK channel 和夹具在异常路径中全部收敛。
7. 对 MConsole exe、必要配置和压缩包独立计量。

完成标准：

- MConsole `67/67` 或后续新增测试全部通过。
- 发布版 P5/P6 均 `passed=True`。
- 定位器只接受正式 Debug/Release 或 publish 输出，不接受 `obj/native/ref/refint`。

执行记录：

```text
状态：完成
进度：100%
记录：最终 Size NativeAOT manifest=.artifacts/p7-manifests/release-0.1.5-mconsole-size-aot.manifest.json；exe=9,199,616 字节 / 8.7734 MiB / f61856caacc3ad2a35a053042e152a78f30c17010b159479997bbd1fe983c30c；相对 B0 11.3955 MiB 下降 23.01%；ILC 含 --Os/--dehydrate 且无 AI roots；最终 ZIP SHA-256=156ec08e31ac8f1d807285c0b3b80f6a3df0fb8a7037fbe22a0bc02d95e52075；发布版与解压复验均 P0/P5/P6 passed=True；MConsole 测试 67/67。
```

## 7. P7-05 性能与诊断对比门槛

当前进度：100%

对 B0、B1 和准备放行的 B2 运行相同测量：

```text
冷启动与热启动时间
version/doctor/db check 延迟
serve ready 时间
SDK 首次连接与重连时间
普通请求首 token 时间与总时长
streaming 吞吐
工作集与峰值内存
NativeAOT publish 时间和中间磁盘占用
异常堆栈可读性与崩溃诊断材料
```

规则：

1. 每项至少预热后重复测量，保存原始数据，不只记录平均值。
2. 机器负载、SDK、配置、输入和 provider mock 必须一致。
3. B1 若体积达标但关键延迟明显回归，保留 B0/Speed 作为性能版。
4. B2 若收益不足 3% 或诊断成本过高，不因“已经做了”而发布。
5. B3 不参与默认版性能承诺。

完成标准：

- 体积收益与性能代价能在同一报告中比较。
- 放行选择有明确阈值和数据，不使用主观“感觉更小”。

执行记录：

```text
状态：完成
进度：100%
记录：B0 Speed 保留为性能/回退基线；B1 Size+Full 作为默认生产版，保留完整 roots 与异常诊断；B2.3 再减 4.5557 MiB 且动态路径通过，可作激进候选；B3 再减 2.8330 MiB 但关闭 StackTraceSupport，仅作 experimental；已完成 B1 的真实 Provider 流式功能 smoke，但未执行并发、长响应或持续压测，因此不能把单次成功结果当作 production streaming 吞吐基线，性能承诺仍以体积/诊断权衡为准。
```

## 8. P7-06 版本选择与发布规则

当前进度：100%

候选定位：

| 版本 | 默认定位 | 允许发布条件 |
| --- | --- | --- |
| B0 Speed 基线 | 回退版 | 已验证，始终保留 |
| B1 Size | 推荐生产候选 | 全门禁通过且性能可接受 |
| B2 roots 激进裁剪 | 可选生产候选 | 动态路径完整通过且收益显著 |
| B3 极限实验 | 实验下载 | 明确标注诊断能力损失，不设为默认 |

发布步骤：

1. 为选中候选生成最终 manifest、SBOM、SHA-256 和压缩包。
2. 对 exe 和 DLL 完成签名后再生成最终压缩包与哈希。
3. 从最终压缩包解压并重跑最小发布验收，不直接复用发布目录结果。
4. 发布说明列出 B0/B1/B2 的精确字节、百分比、性能差异和功能差异。
5. 保留 B0 下载和一键回退说明。
6. 更新本文件、控制台执行总览、AOT 策划文档和仓库发布文档。

最终放行门槛：

- 0 build、trim、AOT 警告。
- 全量测试、P0-P6、provider、Skills、三模式、恢复和并发全部通过。
- 最终解压产物哈希与 manifest 一致。
- 标准版不使用 UPX，不关闭异常、SQLite、IPC 安全和 secret 脱敏。
- 文档明确区分 exe、运行集和传输包体积。

执行记录：

```text
状态：完成
进度：100%
记录：默认正式版 B1；激进候选 B2.3；实验下载 B3；最终 ZIP 已解压复验并完成 version/P0/P5/P6；Directory.Build.targets 在 AOT+RID 发布上下文默认 OptimizationPreference=Size，Directory.Build.props 默认 AgentFrameworkRootProfile=Full；MConsole 项目显式 PublishAot=true；7z/UPX 环境缺失，标准传输格式使用 ZIP Optimal。
```

## 9. 回退顺序

出现回归时只回退最后一个变量：

1. B3 失败：恢复 `StackTraceSupport` 或移除可执行压缩壳，保留 B2/B1。
2. B2.3 失败：恢复 `Microsoft.Extensions.AI` 两个 roots。
3. B2.2 失败：恢复 `Microsoft.Agents.AI` 与 `.Abstractions` roots。
4. B2.1 失败：恢复 `Microsoft.Agents.AI.OpenAI` root。
5. B1 性能失败：恢复 Speed 优化，保留测量和发布脚本改进。
6. 压缩包兼容性失败：恢复标准 ZIP；不回退已经验证的二进制裁剪。
7. 无法快速定位时直接发布 B0，不在故障候选上扩大 descriptor 或抑制警告。

## 10. 文档对齐检查

每次候选完成后检查：

- 本文件任务表、逐节进度和执行记录一致。
- `00-执行总览与进度.md` 的 P7 状态与本文件一致。
- AOT 策划文档中的历史实验和本轮实测分栏记录。
- 发布文档使用同一字节数、MiB 换算和哈希。
- 所有命令都指向候选发布目录，不误用 `bin/.../native` 中间产物。

## 11. P7-07 B1 Size 安全候选

当前进度：100%

B1 原则：只改变 NativeAOT 优化目标，保留五个 AI assembly roots、SQLite、全部 provider、Skills、工具、三种模式和异常栈功能。

执行步骤：

1. 不直接把当前 `IlcOptimizationPreference=Speed` 改成未经验证的最终配置。
2. 在独立候选属性文件或显式 MSBuild 参数中设置公开属性 `OptimizationPreference=Size`。
3. 用 `dotnet msbuild -getProperty:OptimizationPreference` 验证最终值为 `Size`。
4. 检查 `madorin.ilc.rsp`，确认 ILC 收到 `--Os` 和预期的 dehydration 参数；仅看项目文件不足以证明生效。
5. CLI 与 MConsole 分别发布到 `aot-b1-size-cli`、`aot-b1-size-mconsole`。
6. 对比 B0 文件列表，确认没有意外丢失 `e_sqlite3.dll` 或配置文件。
7. 运行基础 CLI、P0-P6、全量 NativeAOT 回归和性能基线。
8. 记录 exe、运行集、ZIP 和 7z 四组数字。

本轮实际结果：

```text
最终默认 CLI exe：56,492,544 字节，53.8755 MiB，SHA-256=a449e3b5f0de48ccc05e00fe8d2e1057e94ade26c7f74b2cd330f7859525a2b1。
最终默认 CLI exe + e_sqlite3.dll：58,403,840 字节，55.6982 MiB。
最终默认 ZIP Optimal：22,469,200 字节，21.4283 MiB，SHA-256=8f3d1c8d79b03225047c5a3618061b57b2b7f2899df57d8cf4f133721ca52f48。
先前 B1 候选比最终冷发布多 512 字节，候选 manifest/ZIP 继续保留为审计记录，不作为最终发布哈希。
```

B1 体积门槛：

- CLI exe 不高于 55 MiB。
- CLI 必要运行集不高于 57 MiB。
- 相对 B0 CLI exe 至少降低 20%。
- MConsole exe 相对 B0 至少降低 10%；没有重新实测前不填写绝对承诺。

完成标准：

- 所有功能、诊断和安全边界保持不变。
- NativeAOT/trimmer 为 0 警告。
- 性能回归在批准阈值内。

执行记录：

```text
状态：完成并推荐生产发布
进度：100%
记录：Size + Full roots 发布无 trim/AOT 警告；ILC 含 --Os/--dehydrate、rd.xml 和五个 AI roots；全量测试 1137/9/0；P0-P6 passed=True；最终 manifest=.artifacts/p7-manifests/release-0.1.5-cli-size-full.manifest.json。
```

## 12. P7-08 传输压缩 A/B

当前进度：100%

压缩只优化下载与制品存储，不改变解压运行体积。报告必须把压缩收益与裁剪收益分开。

执行步骤：

1. 生成标准 ZIP，固定文件顺序、相对路径和时间戳策略，使用 Optimal 压缩。
2. 生成 7z LZMA2 Ultra 候选，记录字典大小、solid block 和线程参数。
3. 分别测量压缩耗时、解压耗时和峰值内存。
4. 解压到全新目录，逐文件核对 SHA-256。
5. 从解压目录执行 `version --json`、P0、P5 和 P6。
6. 不把 UPX 或其他可执行文件壳用于默认发布：它会影响签名、杀毒信誉、启动行为和崩溃诊断。
7. 如需 UPX，只能作为 B3 实验，必须在压缩后重新签名并完成安全扫描。
8. 最终选择一个兼容性优先的 ZIP 和一个可选高压缩归档，不用高压缩格式替代运行验收。

完成标准：

- 解压文件与压缩前哈希完全一致。
- 标准 ZIP 能被 Windows 常见工具直接解压。
- 高压缩包的额外收益足以覆盖解压成本，否则不发布。

执行记录：

```text
状态：完成（标准 ZIP）；7z/UPX 因环境缺失未执行
进度：100%
记录：所有正式包只纳入白名单运行文件并使用 Compress-Archive Optimal；B0=23.1142 MiB、B1=21.4284 MiB、B2.3=19.7385 MiB、B3=18.7514 MiB、MConsole=3.7432 MiB；全部解压后逐文件 SHA-256 一致。当前机器未安装 7z/7zz/UPX，不伪造 LZMA2 或可执行壳收益，正式版不采用 UPX。
```

## 13. P7-09 B2 roots 激进裁剪

当前进度：100%

B2 只在 B1 全部门禁通过后执行。每组 roots 单独发布、单独测试、单独记录，禁止一次删除全部保护后只跑 `--help`。

裁剪顺序：

1. B2.1：解除 `Microsoft.Agents.AI.OpenAI` 的 `rd.xml` 和 `TrimmerRootAssembly` 整包保护。
2. B2.2：解除 `Microsoft.Agents.AI` 与 `.Abstractions` 的整包保护。
3. B2.3：解除 `Microsoft.Extensions.AI` 与 `.Abstractions` 的整包保护。

每一小步执行：

1. 只修改当前组的 `publish/rd.xml` 和 `Directory.Build.props`，保持两处同步。
2. 运行无增量 Release build 和 NativeAOT publish。
3. 收集所有 IL2026、IL2057、IL207x、IL3000、IL3050；禁止用 pragma 或 `UnconditionalSuppressMessage` 隐藏。
4. 如有警告，优先修复 source-generated JSON、反射注解和类型流；第三方不可修复问题必须记录。
5. 运行对应动态路径测试后才进入下一组。
6. 记录本组独立收益，不把 `rd.xml` 与 `TrimmerRootAssembly` 的重叠收益重复相加。
7. 发现 `FileNotFoundException`、`TypeLoadException`、`MissingMethodException` 或反射成员缺失时立即回退当前组。

本轮实际结果：

```text
B2.1 WithoutAgentsOpenAI：53.7798 MiB exe，55.6025 MiB 运行集，21.3838 MiB ZIP。
B2.2 ExtensionsOnly：52.6455 MiB exe，54.4683 MiB 运行集，20.8853 MiB ZIP。
B2.3 None：51,716,096 字节 / 49.3203 MiB exe，51.1431 MiB 运行集，19.7385 MiB ZIP。
B2.3 exe SHA-256=bc781ecbe14ebadcfb9bab1f6fd128b6b8040d61752cb40e08ed9e1e0705d250；ZIP SHA-256=7156b94818e55750bb478ea173b521df5065defe954f35f80faacf7c45be8e62。
```

B2 体积门槛：

- CLI exe 不高于 51 MiB。
- 相对 B1 至少再降低 3%；不足 3% 时优先保留诊断和兼容性。
- MConsole 不应用 CLI 专属 roots；只继承已经证明安全的公共 AOT 属性。

完成标准：

- 五个 roots 的每一步都有独立 A/B 和动态路径证据。
- 全量功能与 B0 行为一致。
- 没有通过批量 root 或警告抑制掩盖具体问题。

执行记录：

```text
状态：完成，作为激进候选发布
进度：100%
记录：三档均独立发布且无 trim/AOT 警告；B2.3 ILC 含 --Os/--dehydrate，无五个 AI --root/rdxml；相对 B1 再减 4.5557 MiB（8.46%），相对 B0 减少 34.00%；P0-P6、Skills、Provider 失败/取消链和 Expert/Meeting/双 Agent Work 均通过；ZIP 解压后哈希与 version/P0/P5/P6 复验通过。B2.3 已使用 `deepseek/deepseek-v4-pro` 完成真实非流式成功 smoke，退出码 0、响应 `OK`；这只补齐一个 Provider/模型组合，不抵消解除整包 roots 的动态兼容风险，因此 B2.3 仍不替代默认 B1。
```

## 14. P7-10 B3 极限实验候选

当前进度：100%

B3 不作为默认生产版，只用于回答“还能缩小多少”。

可选实验：

1. 在 B1 上设置 `StackTraceSupport=false`，与当前仅关闭 ILC stack trace data 的配置区分开。
2. 保持 `InvariantGlobalization=true`，但补跑中文路径、中文参数和 JSON Unicode 用例。
3. 禁止关闭异常、事件、SQLite、IPC 身份校验、secret 脱敏或恢复能力换体积。
4. 可评估 UPX，但必须独立于 B2 roots 结果，避免无法归因。
5. 每个极限开关都单独发布，不叠加后再猜测收益来源。

历史参考：Size + 保留 roots + `StackTraceSupport=false` 曾测得 50.9160 MiB CLI exe。

完成标准：

- 报告明确列出失去的诊断能力。
- 产物名称包含 `experimental`，不能覆盖标准发布版。
- 没有明确批准时不进入正式发布目录。

执行记录：

```text
状态：完成，仅 experimental
进度：100%
记录：B2.3 上单独设置 StackTraceSupport=false；exe=48,745,472 字节 / 46.4873 MiB，运行集=48.3101 MiB，ZIP=18.7514 MiB；exe SHA-256=a6a1606df80cbcc56e7e01a58f731e184efd852fdbcea48d2a92258b94d6684b，ZIP SHA-256=333b5bb161eee27620a3e522cf94c6afb221d50df509508f5f6ba6fcbc8579d0；相对 B2.3 再减 2.8330 MiB。ILC 明确含 System.Diagnostics.StackTrace.IsSupported=false；ZIP 解压后 P0 passed=True。因异常堆栈诊断损失，不进入默认发布。
```
