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

## 8. PDF、音频、视频输入增量与真实端点

用户确认视频与图片应复用 Blob 分块，但仍需对每家接口验证原生 part；代码新增 route 级媒体 codec，保留 Blob 通道不变。阿里云官方[图像与视频理解](https://help.aliyun.com/zh/model-studio/vision)要求直接传视频时使用 `video_url.url`，Base64 data URI 编码后的字符串小于 10 MB，故仅阿里云 Chat route 为 MP4 使用该扩展，写入 SDK 原生 JSON patch，视频不进入普通文本或 Worker Invoke JSON。相应大小限制在读取 Blob 前拒绝，未替用户维护模型能力黑名单。

- 进程内协议模拟：`ProviderMediaAttachmentTests` 20 通过，包含 PDF/MP3/WAV/MP4 字节还原、顶层与工具续调、缺 codec 明确拒绝、阿里云视频大小拒绝。十家 Worker 子进程媒体测试 19 通过：PDF 十家、Chat WAV 八家、阿里云 MP4；300,001 字节均经分块读取在模拟 HTTP 的附件字段还原。最终 Release 联合回归 140 通过、0 失败，TRX `attachment-media-final.trx`；编译零警告、零错误。模拟端点不是厂商已支持所有这些模态的证据。
- 真实 CLI，隔离目录 `obj/attachment-media-cli-20261031`：`axis-code(gpt)`/`gpt-5.6-sol` 调用 `mcli_fs_attach(sample.pdf)`，443 字节、SHA-256 `C4E981AD759043D19469EA9A15133D62586EF208973CBBF7EBAA2C2FEBAA216D`，最终 `run.completed` 并读出页面 `Attachment PDF Test`；证据 `openai-pdf.jsonl` 和工作区历史。
- 阿里云真实 `qwen3.8-max` 的 `mcli_fs_attach(sample.mp4)` 3 秒 MP4、9,335 字节、SHA-256 `A6AC23A3A0E8539EF76076CE471FDDDF53703E91CFEE55272DEE2A80AB2CF052`，`run.completed`，模型回复包含同一静态画面的白底黑色交织图案，区别于元数据；证据 `aliyun-video-visual.jsonl`、权威历史。描述正确度不是独立验收门禁，但可用来交叉检验模型看到帧内容。
- 失败也保留：第一次 1 秒视频、7,667 字节返回 HTTP 400（`aliyun-video.jsonl`），补至 3 秒后成功。阿里云[官方视频限制](https://help.aliyun.com/zh/model-studio/vision/)写明 Qwen3.8 系列视频时长至少 2 秒，因此首轮失败与此限制一致；由于 SDK 只呈现泛化 HTTP 400，未捕获服务端具体错误体，不声称已唯一确定成因。阿里云 `qwen3.8-max` 的 2 秒 WAV（64,078 字节）工具续调返回 HTTP 400（`aliyun-audio.jsonl`）；当前 `input_audio` 模拟请求字段不能视为该模型/端点支持音频。原始请求/密钥未写入文档或仓库。

尚未完成其余真实厂商 PDF/音频/视频矩阵与 Provider 输出写回；步骤 04 输出、02 生命周期和 03 外部授权仍开放。

## 9. Provider 输出写回与启动目录落盘

本节为后续输出实现的增量，更新第 8 节当时的“输出未实现”结论；这里的 HTTP 回包为模拟服务，不计作真实厂商生成成功。

### 9.1 实现与交付路径

- SDK 二进制内容 → invocation StoreOutputBlob → Worker IPC 3 begin/chunk/complete/ack → Runtime 有界 Pipe → BlobStore 校验长度/SHA → AttachmentCompleted 引用事件。
- Orchestrator 在 CLI 进程中将确认附件写入 `AppContext.BaseDirectory/.attach`；目录不存在则创建，GUID + MIME 扩展名，FileMode.CreateNew。校验保存结果后生成图片 Markdown 或普通附件链接，经既有 text.delta 按原顺序推送宿主。
- 权威 assistant 历史保留 BlobRef 和 Markdown。落盘失败返回 `AttachmentDeliveryFailed`，已确认 Blob 保留在部分回复中，不发送不存在的文件链接。未改工作区/外部路径授权规则。
- 音频分片不能直接每片落一个文件，现已在 SDK 边界有界合并至完成标记再发布；延迟文字和附件计入 invocation 配额，避免漏计/重复计数。无完成标记的音频以失败结束。

### 9.2 本地验证结果

| 测试 | 结果 | 证据 |
| --- | --- | --- |
| Provider/Worker/媒体/工具联合 Release 回归 | 168 通过，0 失败 | `output-blob-final-release.trx` |
| Protocol 全量 | 49 通过，0 失败 | `output-blob-protocol.trx` |
| 既有 BlobTransfer E2E | 5 通过，0 失败 | `output-blob-transfer.trx` |

本批输出专项包含在 168 项中：八条独立 Chat Worker 路线各接收 300,001 字节分片音频，字节和哈希一致、前文字/附件/后文字顺序一致；OpenAI Responses 完成图片写回，保存两次生成不同文件名，单调用配额不足时失败且不发布附件。补测音频加延迟文字的配额边界、跨附件累计限额、输出帧上限，以及 generation/hash/offset/quota/cancel 反例，失败不留 staging。

贯通测试使用真实 OpenAI Worker 子进程及模拟 Responses HTTP，经过 LocalRuntime 编排取得宿主文字事件，解析 Markdown 的 file URI 后确认目标位于测试主程序启动目录 `.attach`、名称为 GUID.png、文件字节完全一致，并核验历史 BlobRef/Markdown。另有直接 Provider 路线和不存在 Blob 路线。测试仅删除自己产生的文件。此处没有启动独立 CLI 可执行进程，也没有人工验证外部宿主 Markdown 渲染器。

第一次较早联合回归暴露同 InvocationId 立即续调时 Worker 尚未释放资源的竞态，已修复为先清理再发布终态；另一次遇 Worker hello 启动超时，同 binary 重跑 162 项通过。最终增补测试和修正后上述 168 项通过，不隐去先前失败。

### 9.3 未关闭事项

- 输出按十家逐条记录在步骤 06：九条 Worker 路线有模拟二进制证据，Anthropic 当前 Minimal SDK 没有二进制输出映射成功证据；不能把共享 sink 装配当成厂商文件输出已支持。
- Anthropic Files API / 服务端代码执行结果引用、其他 route 的 HostedFileContent/UriContent 安全下载 codec 未实现。当前遇到 SDK 已暴露的 file/URI 内容明确失败；SDK 自身丢弃未知 block 的缺口仍需专属 codec 修复。
- 未调用真实厂商生成接口，不开启新的生成工具，不自动下载普通文本 URL。各厂商真实媒体输出、崩溃/half-close/慢消费者故障矩阵、会话保留回收、重放、正式 AOT/manifest 仍需逐项完成。
- CLI 与全部 Worker 的协议版本现为 IPC 3，必须同批构建部署，不能混用 IPC 2 二进制。

## 10. Anthropic 服务端文件结果与 Files API 下载增量

本节更新第 9.3 节当时的 Anthropic codec 缺口，保留其他厂商引用下载和真实输出验收。用户确认新包发布后，已将 `Netor.Anthropic.Minimal` / `Extensions` 还原至 0.1.6。检查实际 DLL 发现 Minimal 响应模型/mapper 仍不包含文件结果，客户端也没有 Files 服务；Runtime 利用 SDK 保留的原始 SSE 增补解析，而不是假定升级已解决。

### 10.1 实现路径

1. Messages 原始 SSE 的服务端文件结果块，在 `content_block_stop` 后取 `file_id`；支持 container_upload、代码执行、bash 代码执行及文本编辑结果中的正式文件输出字段。`server_tool_use` 不作为本地工具调用重新执行。
2. 用当前连接身份依次请求文件元数据和内容，固定到当前 Provider base URL 的 `v1/files/{id}` 与 `v1/files/{id}/content`；携带 `anthropic-version: 2023-06-01`、`anthropic-beta: files-api-2025-04-14`。依据 [Files API 官方文档](https://platform.claude.com/docs/en/build-with-claude/files)，只下载 `downloadable=true` 的文件。
3. 检查 ID、元数据上限 64 KiB、文件长度/MIME、调用累计配额和两分钟单文件超时；禁用 HTTP 重定向，取消向下传递。完整文件经既有 StoreOutputBlob → IPC 3 → Blob 校验 → `.attach/GUID.ext` → Markdown 文字事件交付，不传输文件字节 JSON。
4. 新增完整文件内容标记，完整音频文件不参加流式音频片段合并。文本/CSV/JSON/ZIP/Office 按固定 MIME 映射扩展名，服务端文件名不能决定磁盘路径。
5. 贯通测试首次发现 `ConversationStore` 将 text/plain 输出 Blob 展开为正文，修正 Provider 输出 scope 为 `provider-output:` 并在 Stream 查询保留引用；正文 offload 仍沿用原有展开行为。

### 10.2 验证证据

| 测试 | 结果 | 证据 |
| --- | --- | --- |
| Anthropic 文件结果专项（包含在联合回归内） | 15 通过，0 失败 | `anthropic-output-regression.trx` |
| Provider/Worker/媒体/工具联合 Release 回归 | 185 通过，0 失败，4 跳过 | `anthropic-output-regression.trx`、`obj/anthropic-output-final-regression.binlog` |
| Protocol 全量 | 49 通过，0 失败 | `anthropic-output-protocol.trx` |
| ConversationStore 持久化回归 | 23 通过，0 失败 | `anthropic-output-history.trx` |
| Anthropic Worker win-x64 Native AOT 发布 | 成功，零警告、零错误 | `obj/anthropic-output-final-aot-20261031.binlog` |
| 使用已发布 Native AOT Anthropic Worker 的输出/交付专项 | 19 通过，0 失败 | `anthropic-native-output.trx` |

专项成功用例覆盖进程内与独立 Worker、container_upload、code_execution/bash_code_execution 结果、多文件原始顺序，以及两个独立 audio/wav 文件不被合并。每个 300,001 字节回包在 Blob 中逐字节还原，并核验请求认证和 Files beta header。音频 MIME 用例验证传输与文件边界，不用模拟 ASCII 内容证明音频可播放。

十项失败用例覆盖不可下载、超额、长度不符、MIME 不符、重定向、HTTP 403、非法 ID、未闭合内容块、注入 IChatClient 无下载身份和取消。失败不发布附件/成功终态且不留 staging；重定向目的模拟服务器未收到请求或密钥。

LocalRuntime 贯通使用真实 Anthropic Worker 子进程加模拟 HTTP，确认 `.attach/GUID.txt`、原字节、前文字/文件/后文字顺序、宿主 text.delta Markdown 和历史 BlobRef。联合回归的 4 项跳过为真实联网测试开关未启用，不能计为厂商验收。

Native AOT 首次发布在原生生成阶段以退出码 1 结束且没有诊断输出；补抓 binlog 后发布成功，音频完整文件修正后的最终发布也成功。发布验证仅覆盖当前 Windows win-x64 Anthropic Worker，不代表全 RID 或完整 CLI 发布验收。

原生专项在测试输出目录临时替换 Anthropic Worker exe，执行 15 项文件专项和 4 项交付测试，结束后恢复托管 exe。涉及 Anthropic 子进程的成功用例和 LocalRuntime 贯通实际使用该原生二进制；其余进程内/失败用例保留原测试方式，不将全部 19 项都记为原生子进程调用。

### 10.3 保留边界

- 当前没有自动启用 Anthropic 代码执行/Skills 等服务端生成工具；收到正式文件结果后能下载交付，不代表已经增加生成业务。真实厂商或转发端点是否提供并透传 Files API 仍待验收。
- Files 下载采用独立禁重定向的 SocketsHttpHandler，使用系统代理；自定义聊天 IHttpClientFactory 的代理/handler 不自动作用于此通道。依赖这类配置的部署需要单独核验。
- SDK 边界仍在调用限额内完整缓冲文件；不是全链路常量内存。其他协议 HostedFileContent/UriContent 的未实现下载路径保持明确失败，普通文本 URL 不下载。

## 11. 已配置 Anthropic 连接的真实文件输出验收（2026-10-31 20:00）

用户要求使用已配置模型测试。本批只读取全局配置，不更换地址/凭据、不修改配置；分别使用 `axis-code(Anthropic)` 的 `claude-opus-4-6` 和 `magpie-anthropic` 的 `group/auto-claude-opus-4-8`。六次 Messages 请求均为真实配置端点，未使用 Fake Provider；这些地址是转发服务，并非 Anthropic 官方直连。

### 11.1 方法与官方依据

- 直接请求当前连接的 `/v1/messages`，显式提供正式 `code_execution` 服务端工具，输出预算 2048 tokens，单次超时三分钟。任务为创建 UTF-8 小文件，其内容为随机固定校验串，无换行；要求服务端执行并返回正式文件引用。
- 对照 [Anthropic 官方代码执行文档](https://platform.claude.com/docs/en/agents-and-tools/tool-use/code-execution-tool)，使用 `code_execution_20250825`，并补测 `code_execution_20260120`。官方说明这些工具版本支持所选 Claude 4.6，代码执行本身无需 beta header；请求同时带 Files API 的 `files-api-2025-04-14` header。
- 仅从正式文件输出字段提取 `file_id`，取得后才请求元数据/内容并核对原始字节。未把模型文字、HTTP 200、普通 Markdown 路径视为生成成功；未将服务端工具调用交给本地执行。
- 只读探测两个连接的 `/v1/files?limit=1`；不记录文件列表正文。证据仅保存模型、状态、内容块类型、脱敏工具结果及校验信息，不保存请求头、凭据。

### 11.2 结果

| 连接 / 模型 | 代码执行版本 | 真实响应 | 文件引用 | Files 列表路由 | 结论 |
| --- | --- | --- | --- | --- | --- |
| axis-code(Anthropic) / claude-opus-4-6 | 20250825，两次 | HTTP 200；首轮 bash 工具返回 `return_code=1`，stderr 明确 `Server-side code execution is not available on this channel.`；调整为纯 Python 后只返回与任务无关的 Hello, World! | 0 | HTTP 404，`404 page not found` | 未通过 |
| axis-code(Anthropic) / claude-opus-4-6 | 20260120 | HTTP 200，`stop_reason=tool_use`，普通 `tool_use` 内容块，没有已执行的文件结果 | 0 | HTTP 404 | 未通过 |
| magpie-anthropic / group/auto-claude-opus-4-8 | 20250825，两次 | HTTP 200；首轮 bash 工具返回 NameError，调整为纯 Python 后只返回与任务无关的 Hello, World! | 0 | HTTP 404，服务错误列出的可用路由不包含 Files | 未通过 |
| magpie-anthropic / group/auto-claude-opus-4-8 | 20260120 | HTTP 200，`stop_reason=tool_use`，普通 `tool_use` 内容块，没有已执行的文件结果 | 0 | HTTP 404 | 未通过 |

共六次生成尝试，零个正式文件引用，因此没有可合法执行的真实文件下载，也没有进入 Worker/CLI `.attach` 的真实文件交付。本次结果不能关闭步骤 06 的真实验收项；本地模拟和原生 Worker 的通过证据仍仅代表 Runtime 实现验证。

证据位于 `obj/anthropic-live-*.json`：axis-code 时间后缀 `20261031-200025`、`200146`、`200201`；magpie 时间后缀 `20261031-200038`、`200140`、`200159`。本机复测脚本为 `obj/Test-AnthropicGeneratedFile.ps1`，从全局配置取身份，文件引用不存在时不会编造 ID 尝试下载。

### 11.3 后续条件

当前证据将阻塞定位到所测连接的服务端生成/Files API 路线，不能据此断言模型本身没有文件能力，也不外推到全部配置模型或官方直连。继续完成真实验收需要现有转发服务提供正式代码执行和 Files 路由，或在全局配置中增加确实支持这两者的 Anthropic 连接；拿到真实文件引用后，再运行下载、Worker、CLI 落盘与宿主 Markdown 的完整验收。
