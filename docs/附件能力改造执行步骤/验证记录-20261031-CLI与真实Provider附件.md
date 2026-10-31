# 2026-10-31 CLI 与真实 Provider 附件验证

> 状态：存在失败项，不通过附件端到端门禁。本记录是执行中的验证证据，不改变 01-07 的实施顺序，不把局部 Adapter 成功标为 Worker/Host 完成。
>
> 最新判定以第 6 节为准：按用户修订，只验附件传输与可见性，不考核识别准确率。第 1-5 节是前一轮历史记录，其模型选择和关键词门禁不再作为当前通过条件。

## 1. 环境与证据边界

- 使用用户现有 `~/.madorin/config.json`，共 8 个连接、5 种协议：OpenAI、Anthropic、DeepSeek、Kimi、Aliyun。
- 测试前逐个重新构建这 5 种协议的 Worker 和 CLI，零警告、零错误。每次 CLI 仅在当前进程指定 `MADORIN_PROVIDER_DIR` 为相应 Worker 的独立输出目录，避免 CLI 输出目录旧 DLL 混装；不改变生产协议或降级路线。
- CLI 使用隔离工作区 `obj/attachment-cli-live-20261031-154049/workspace`。原始事件、脱敏终态及临时数据库留在被 Git 忽略的该目录；不提交 Key、全局配置、完整请求正文或附件 Base64。
- PNG 来自仓库既有 `obj/provider-icon-src/openai.png`，在本机查看确认是黑色交织、中心六边形的图案；文件名在测试中改为 `sample.png`，提示词不透露图案答案。大小 11,297 字节，SHA-256 为 `8a33312f6bb17c1edddf8f9d7fbac09ce1d1d697d6601f37227afa879e94c52e`。透明背景在不同端点可能呈白色，不把背景色差异单独判为错误。
- 当前测试实际还原的 SDK：`Microsoft.Extensions.AI.OpenAI/10.6.0`、`OpenAI/2.10.0`、`Netor.Anthropic.Minimal/0.1.5`、`Netor.Anthropic.Minimal.Extensions/0.1.5`。它不同于步骤 01 早期 MEAI 10.5.1 的测试环境，记录不相互替代。
- 全程不以 Vision/Files/Audio 手填值判断模型是否支持图片。进程内测试将这些值保持 false；只为隔离附件变量声明 ToolCalling/Reasoning，不将这个测试配置当作生产默认配置修复。

## 2. CLI 生产路线结果

每条连接先运行仅回复 OK 的文本请求。成功后再要求只调用一次 `mcli_fs_attach(path=sample.png)` 并描述图案；使用 `--allow-tools mcli_fs_attach`、`--max-tokens 512`、JSONL 输出及 75 秒超时。这里的 allow-tools 是明确测试授权，不代替步骤 03 工作区默认/外部 Grant 验收。

| 连接 / 当前 API | 测试模型 | 文本 | 工具附件续调 | 结论 |
| --- | --- | --- | --- | --- |
| deepseek / api.deepseek.com Chat | deepseek-v4-pro | OK | 本地 ProviderRequestFailed / InvalidOperationException | 文件工具成功，后续映射失败；不是厂商图片不支持证据 |
| magpie-openai / 本机网关 Responses | group/auto-gpt-5-6 | OK | 同上 | Worker 缺 Resolver 的生产路径未闭合 |
| magpie-anthropic / 本机网关 Messages | group/auto-claude-fable-5-1 | OK | 同上 | Worker 缺 Resolver 的生产路径未闭合 |
| Aliyun / DashScope compatible Chat | glm-5.2 | OK | 本地 CapabilityNotSupported: reasoning | 历史 reasoning 回放先被检查拦截，不能声称图片已发送 |
| Kimi / api.moonshot.cn Chat | kimi-latest、moonshot-v1-32k、moonshot-v1-128k | 均 404 / model not found or permission denied | 未对失败文本路线继续发图 | 配置中的旧模型 ID 不可用；不判定附件能力 |
| Kimi / 同一连接 | kimi-k3.1-pro | OK | 本地 ProviderRequestFailed / InvalidOperationException | 新模型可用，仍暴露 Worker Resolver 缺口 |
| axis-code(gpt) / axis-code.com Responses | gpt-5.6 | 403 | 未继续 | 路线/权限错误，未定位，不等价于 Key 已失效 |
| axis-code(Anthropic) / api.axis-code.com Messages | claude-haiku-4-5 | 403 | 未继续 | 同上，不把网关结果外推官方 Anthropic |
| 火山方舟 / 当前 OpenAI Responses 路线 | doubao-seed-1-6-flash-250615 | 404 | 未继续 | 当前路径或模型错误待定位，不自动切 Chat 或替换 Endpoint |

### 已证实与未证实

- 5 次真实工具执行成功，history 均保存 `text + blob_ref`，JSON 元数据为 146 字节，原文件 bytes 不在工具文本。落盘 Blob 大小/哈希与 fixture 一致，staging 剩余文件为 0。
- Worker BuildAdapter 工厂目前只接收连接配置，没有从 Runtime 获得 invocation-scoped Resolver；这是步骤 04 已规划但未实现的缺口。CLI 终态只给出 InvalidOperationException 名称，诊断过于笼统也需记录，但本轮不通过回退 JSON 或直接给 Worker 任意磁盘权限绕过。
- Aliyun 在同一次附件工具成功后先被 reasoning 历史校验拦住；未修改无关能力校验来制造全绿。
- 所有 CLI 工具附件续调均未通过模型识别门禁；不能因为 Blob 文件已生成就报告“模型读取附件成功”。

## 3. Kimi 配置修正

- 使用现有连接凭据调用 `/models`，HTTP 200 返回 `kimi-k2.7-code`、`kimi-k3.1-pro`、`kimi-k3.1-pro-highspeed`、`kimi-k2.6`、`kimi-k2.7-code-highspeed`、`kimi-k3`。
- 按用户允许修改全局配置的明确授权，先在用户配置目录备份，再只追加 `kimi-k3.1-pro`。保留原三个模型 ID、所有其他连接和默认选择，不改凭据、Endpoint 或上下文窗口，不将本机配置提交 Git。
- 新 ID 的 CLI 文本调用成功；不能把旧 ID 的 404 误归因于 Runtime 窗口检查或 Blob 传输。

## 4. 进程内 Adapter 与真实端点隔离测试

新增 `LiveAttachmentProviderTests`，显式开关后才会联网计费，默认跳过。它不走 Worker，使用只允许 fixture 引用的本地 Resolver，分别构造顶层图片和工具结果图片，以隔离 Worker 缺口。工具结果采用固定 CallId 的续调历史，不等价于真实模型先发起工具调用的完整业务闭环。

OpenAI、Anthropic、Aliyun 通过测试注入的 HTTP handler 检查最终原生 image 字段，Base64 解码后逐字节等于 fixture，只输出 path、part 数量和请求字节数，不保存 Key/Header/body。DeepSeek/Kimi 当前 Adapter 不提供该 HTTP 注入点，因此这两条真实测试只证明 Resolver 被读取和端点回包，不能把 handler 的 0 次捕获解释为未发送图片，也不能冒充已有真实网络字节证据。

| 实际连接 / 模型 | 顶层图片 | 工具结果图片 | 请求字段/字节证据 | 限制与结论 |
| --- | --- | --- | --- | --- |
| magpie-openai / group/auto-gpt-5-6 | 描述黑色交织图形，符合 fixture | 同样符合 fixture | `/v1/responses`，各 1 个原生 image part；body 15,504 / 17,366 字节，解码字节完全一致 | 本机网关该模型的进程内 PNG 输入已验证；不是官方 OpenAI endpoint、Worker 或输出附件证明 |
| magpie-anthropic / group/auto-claude-fable-5-1 | 描述黑色交织、中心六边形结构；透明区域称白色 | 多次错误描述：笑脸、眼睛、彩色几何图形 | `/v1/messages`，各 1 个原生 image part；body 16,594 / 17,190 字节，解码字节一致 | 顶层结构识别通过；工具结果识别失败，即使 HTTP 成功也不得标通过。问题位于 SDK 后的网关/模型路径还是具体 tool-result 表达仍待定位，不外推官方 Anthropic |
| deepseek / deepseek-v4-pro | 回答图片不可见 | 回答不可见或 unsupported image format | Resolver 各读取一次；真实 HTTP 未注入捕获，另有 Mock HTTP 字节测试 | 请求完成但识别失败；不是 Runtime 本地能力拦截，不预设厂商支持黑名单 |
| Aliyun / glm-5.2 | 回答无图片可访问 | 同样无图片可访问 | `/compatible-mode/v1/chat/completions`，各 1 个原生 image part；body 15,467 / 16,229 字节，解码字节一致 | 在不含 reasoning 历史的隔离请求中也未识别，不能因字段正确/HTTP 成功认定该模型可用 |
| Kimi / kimi-k3.1-pro | 512 输出预算曾无文本；增至 2048 后有结构描述 | 可描述交织六边形结构，但后一次回包将黑色线条说成绿色、黑底 | Resolver 各一次；真实 HTTP 尚无注入捕获 | 结构关键词检查通过但人工颜色复核不稳定；不关闭完整图片门禁，不将工具结果一次关键词通过当作最终证明 |

### 执行结果的准确解释

- 初版只检查完成事件/非空文本，结果 8 通过、2 失败（旧 Kimi ID 404）；人工读回包后发现“看不到图片”也被算通过，因此这组结果**不能作为附件通过率**。
- 增加人工查看 fixture 后给出的结构/颜色关键词断言，以及可注入路线最终请求字节检查。第二轮 3 通过、7 失败；更新 Kimi 模型后的完整 10 项为 4 通过、6 失败，TRX 为 `attachment-live-final.trx`。
- Kimi 提高输出预算后单独重跑 2 通过、8 未选中跳过，TRX 为 `attachment-live-kimi.trx`。这只是粗粒度结构关键词门禁通过；人工复核发现一条绿色线条描述不符，所以 Kimi 仍不算完整验收。
- 收尾 Release 本地回归 85 通过、10 跳过，TRX 为 `attachment-post-live-release.trx`。10 条真实联网测试在未启用开关时全部跳过，不能把这个 Release 全绿数字当作真实端点全绿；编译零警告、零错误。
- 初版及后续 TRX 均保留，测试的成功断言不改写事实；失败涉及服务端拒绝、不可见、错图/幻觉和输出截断，各自记录，不归并成一个“Provider 支持/不支持”布尔值。
- 本轮仅测试 PNG 输入及文本回答。PDF、音频、视频、厂商返回二进制、Host/Client 附件下载均未验收。未发起图片生成任务，也不为验收添加生成业务。

## 5. 复跑方式与后续门禁

在 PowerShell 进程中设置环境变量，执行后恢复原值；测试使用本机全局配置，不把凭据写入命令行。

```powershell
$env:MADORIN_TEST_LIVE_ATTACHMENTS = '1'
$env:MADORIN_TEST_ATTACHMENT_IMAGE = '<已人工查看的 PNG 绝对路径>'
$env:MADORIN_TEST_ATTACHMENT_EXPECTED_TERMS = 'black;interlock|interw|knot|loop|hexagon'
# 可选：只调用一家，其他行明确跳过。
$env:MADORIN_TEST_LIVE_PROVIDER = 'Kimi'
dotnet test tests/Madorin.AI.Runtime.Provider.Tests/Madorin.AI.Runtime.Provider.Tests.csproj --no-restore --filter 'FullyQualifiedName~LiveAttachmentProviderTests' --logger 'trx;LogFileName=attachment-live-recheck.trx' -v q
```

- 测试绑定现有连接名与记录中的模型；换模型必须记录新模型和端点，不能把这一轮结论复制过去。关键词只排除明显假成功，复跑仍要人工核对回包，尤其是颜色和错图描述。
- 首先按步骤 02/03 关闭生命周期和授权门禁，再按步骤 04 实现十个 Worker invocation-scoped Blob 桥。不得让 Worker 直接读取任意磁盘路径来临时凑通 CLI。
- 步骤 05 需复核 Anthropic tool-result 在本机网关的映射/处理、DeepSeek 和 Aliyun 当前模型的实际媒体行为，以及 Aliyun reasoning 历史回放的独立阻断。
- OpenAICompatible、AzureOpenAI、Gemini、GLM、Ollama 没有对应全局测试连接，真实调用未执行。Aliyun 上的 glm 模型不是 GLM Adapter 测试，方舟配置为 OpenAI 也不是 OpenAICompatible Adapter 的证明。
- CodeMap 基线仍被任务外未提交源码阻塞；未把别人的代码强行提交或声称语义索引已刷新。

## 6. 充值后复测与验收口径修订

### 6.1 用户确认的口径

- 用户说明 axis-code 原 403 来自欠费，充值完成后重新测试；不再沿用前一轮未定位的权限猜测。
- 本专项验收图片原始内容能通过正确的附件字段传给端点，记录模型是否可见；颜色、物体、图案描述准确率属于模型识别质量，不是附件传输门禁。删除测试中的关键词准确率断言，不因错色/错图就判本地映射失败。
- 传输自动化门禁：Resolver 读取受控 fixture、最终 HTTP 原生 image 字段解码后字节完全一致、Provider 没有失败事件且有完成事件。回包可见性和 finish reason 单列；空文本/length 不能自动证明可见，必要时补足输出预算重跑。HTTP 200 或模型口头确认本身仍不能替代字节核验。
- 根据用户指定方向选择实际测试模型：Aliyun Qwen 3.8 系列、DeepSeek 最新配置 Flash、Anthropic Opus 4.6 以上。不把本次测试选型变成生产能力黑名单，也不据此宣称其他 Anthropic 型号一律不支持视觉。

### 6.2 模型与配置

- axis-code GPT/Anthropic `/models` 均 HTTP 200。使用 `gpt-5.6-sol` 与接口返回的 `claude-opus-4-6`；后者已先备份全局配置再追加，保留旧模型、凭据、端点与默认选择不变。
- magpie Anthropic 选择已有 `group/auto-claude-opus-4-8`；DeepSeek 选择已有 `deepseek-v4-flash`；Kimi 沿用 `kimi-k3.1-pro`。
- Aliyun 首试字面 ID `qwen3.8` 在文本和图片请求均返回 404。随后查询当前账号 `/models`，HTTP 200 返回 `qwen3.8-max`、`qwen3.8-max-0902`、`qwen3.8-flash` 等，但无字面 `qwen3.8`；改用全局配置中已有 `qwen3.8-max` 后文本成功、图片成功，不另造模型别名。
- 测试保留媒体 flags=false；仍不要求用户先勾选视觉能力才能发图片。全局配置没有补虚构窗口或修改媒体能力来绕过附件检查。

### 6.3 真实进程内传输矩阵

| 连接 / 模型 | 顶层图片 | 工具结果图片 | 原生字段及最终字节核验 | 回包可见性 |
| --- | --- | --- | --- | --- |
| magpie-openai / group/auto-gpt-5-6 | 通过 | 通过 | `/v1/responses`，各 1 image part，15,515 / 17,377 bytes | 两项均明确能看到图片 |
| magpie-anthropic / group/auto-claude-opus-4-8 | 通过 | 重跑通过 | `/v1/messages`，各 1 image part，16,604 / 17,200 bytes | 两项均确认可见；首轮工具请求 90 秒取消，第二轮完成，不隐去超时记录 |
| axis-code(gpt) / gpt-5.6-sol | 通过 | 通过 | `/v1/responses`，各 1 image part，15,508 / 17,370 bytes | 两项均确认可见，充值后的 403 已消失 |
| axis-code(Anthropic) / claude-opus-4-6 | 通过 | 通过 | `/v1/messages`，各 1 image part，16,593 / 17,189 bytes | 两项均确认可见，充值后的 403 已消失 |
| DeepSeek / deepseek-v4-flash | 通过 | 通过 | `/chat/completions`，各 1 image part，15,488 / 17,348 bytes | 两项均确认可见；与前一轮 Pro 结果分开 |
| Kimi / kimi-k3.1-pro | 通过 | 通过 | `/v1/chat/completions`，各 1 image part，16,660 / 17,367 bytes | 两项均确认可见；描述颜色差异不判传输失败 |
| Aliyun / qwen3.8-max | 通过 | 通过 | `/compatible-mode/v1/chat/completions`，各 1 image part，15,483 / 16,245 bytes | 两项均确认可见；初次 512 输出预算工具结果为 length/空文本，2048 预算复跑后 stop/有文本 |

所有附件仍为第 1 节同一 PNG，11,297 bytes，SHA-256 完全一致。请求 bytes 包含各自协议元数据和 Base64 编码，只是测量，不作跨协议大小推断。

DeepSeek/Kimi 本次新增 `LiveAttachmentHttpRelay`，只用于测试：SDK 指向 loopback 随机端口，relay 固定转发到该连接原有 origin，保持 path/query 和正文，内存核验后不保存 Header/body，不跟随重定向。生产 Adapter 未新增任意文件/网络读取能力；其原有 reasoning/thinking handler 仍执行。相比前一轮，此次两家也有真实请求附件字节核验，不再只有 Resolver 读取证据。

### 6.4 结果数量与 CLI 边界

- 首轮 14 项：11 通过、3 失败，TRX `attachment-live-vision-retest.trx`。失败为 Aliyun 字面 `qwen3.8` 两项 404、magpie Opus 4.8 工具一项超时。
- Aliyun `qwen3.8-max` 两项通过，TRX `attachment-live-qwen38max.trx`；补输出预算确认可见的两项再次通过，TRX `attachment-live-qwen38max-visible.trx`。magpie Opus 4.8 两项重跑通过，TRX `attachment-live-opus48-retry.trx`。每次单连接复跑另外 12 项明确跳过。上述矩阵是多次运行合并证据，不是一次 14/14 全绿。
- CLI 充值复跑：axis-code GPT、axis-code Opus 4.6、DeepSeek Flash 文本均成功；三个 attach 工具均完成，但续调仍失败于 Worker 无 Blob Resolver。Aliyun `qwen3.8-max` 文本成功，attach 后仍被本地 reasoning 能力检查拦截。事件目录为 `obj/attachment-cli-recharge-20261031-161501`，未修改生产实现掩盖问题。
- 因此：7 连接的进程内 PNG 顶层/工具结果传输已取得通过证据；**CLI 生产完整附件链路仍未通过**。不将进程内 Resolver 注入测试当成步骤 04 已完成，也不外推 PDF、视频、音频或 Provider 输出附件。
- 收尾 Release 本地回归：85 通过、14 条联网测试在开关关闭时跳过，构建零警告/错误；TRX `attachment-recharge-regression.trx`。本地回归数字与上述真实调用结果分开，不计作 14 条真实重跑通过。

### 6.5 当前复跑命令

不再使用 `MADORIN_TEST_ATTACHMENT_EXPECTED_TERMS`；旧第 5 节示例仅保留为历史。

```powershell
$env:MADORIN_TEST_LIVE_ATTACHMENTS = '1'
$env:MADORIN_TEST_ATTACHMENT_IMAGE = '<PNG 绝对路径>'
# 可选单连接；不设置则运行当前 7 个连接的 14 项。
$env:MADORIN_TEST_LIVE_PROVIDER = 'axis-code(Anthropic)'
dotnet test tests/Madorin.AI.Runtime.Provider.Tests/Madorin.AI.Runtime.Provider.Tests.csproj --no-restore --filter 'FullyQualifiedName~LiveAttachmentProviderTests' --logger 'trx;LogFileName=attachment-live-retest.trx' -v q
```

命令完成后恢复各环境变量原值；凭据从全局配置读取，不写进命令或文档。测试选择的模型清单见第 6.3 节，不自动切换模型或协议。

## 7. Worker 输入桥实现后的真实 CLI 复测

用户确认继续修复 Worker Resolver 后，IPC 升为 2，十家 Worker 都装配 invocation-scoped Resolver。实现在[步骤 04](./04-ProviderWorker双向Blob管道.md)，只新增受控分块输入，不赋予 Worker 任意磁盘读取权。

使用同一 fixture、隔离工作区、`mcli_fs_attach` 和 2048 输出预算：

| 真实 CLI 路线 | 终态 | 图片回包 | 证据文件 |
| --- | --- | --- | --- |
| deepseek / deepseek-v4-flash | run.completed | 能看到，并描述交织环/六边形 | `deepseek.jsonl` |
| axis-code(gpt) / gpt-5.6-sol | run.completed | 能看到黑色标志 | `axis-code_gpt_.jsonl` |
| axis-code(Anthropic) / claude-opus-4-6 | run.completed | 可以看到，描述几何交织结构 | `axis-code_Anthropic_.jsonl` |
| Kimi / kimi-k3.1-pro | run.completed | 能看到，描述几何花形 | `Kimi.jsonl` |
| Aliyun / qwen3.8-max | 重跑 run.completed | 能看到黑色线条六角结 | `Aliyun-history-fix.jsonl` |

本机证据目录 `obj/worker-blob-cli-20261031-164403`。五次 attach 权威历史均成功，JSON 元数据为 146 bytes，Blob 为 11,297 bytes，SHA-256 与前述 fixture 相同，staging 无残留；模型不只返回元数据，均确认图片可见。描述准确率不作为本轮传输判定。

Aliyun 第一次运行到 90 秒取消；排查发现 HistoryMessageCount 没有跨 IPC 传递，本批补入 Invoke 控制字段并让 Worker 使用现有历史兼容投影。对已生成的历史 reasoning 可投影，对本轮 reasoning 仍执行原校验，新增测试验证两种情况，未全局关闭 reasoning 能力门禁。

这组结果取代“Worker 永远缺 Resolver”的现状描述，前面第 1-6 节仍保留历史时间点结论。十家 Worker 的模拟装配测试与五条真实账号路线分开记；输出附件、其他模态、正式 AOT 与发布包兼容尚未验收。
