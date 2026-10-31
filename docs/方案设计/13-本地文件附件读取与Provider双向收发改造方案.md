# 本地文件附件读取与 Provider 双向收发改造方案

> 状态：调研完成，代码改造待执行。
>
> 日期：2026-10-31。源码基线：`9fa7a29670f050d9fba8554fa6b3b375c13216c7`。
>
> Provider 源码快照调研增补：2026-10-31。CodeMap 因工作区变更不可用，按源码回退规则复核十个 Adapter/Worker、MEAI 转换和 Provider 测试。以下是代码路径证据；除文中既有模拟测试外，未真实调用厂商 endpoint，不能据此认证厂商模型支持。
>
> 本次只交付修改方案和执行清单，不表示附件功能已实现。调研仅使用主仓库 `src/`、`tests/`；不把未跟踪的 `图片视频/` 副本作为实现依据。

## 1. 本次冻结的产品决策

1. 附件是否发送，不依赖用户填写的模型能力。`Vision`、`Audio`、`Files` 为 false 或未填写，均不得造成附件工具隐藏、当前输入拒绝、历史附件过滤或工具结果降级。
2. 模型是否支持某种附件，由真实服务端请求结果决定。正常传输后保留厂商返回的错误，不通过本地模型名名单或手填能力表抢先判定。
3. 工作区内文件读取沿用默认权限，无须新增用户审批。这里的“无需授权”指无需交互式申请，内部仍经过默认 PermissionContext、路径校验与审计。
4. 工作区外路径复用宿主既有权限请求与 Grant：宿主明确批准后，可在该 Grant 指定的外部读根、写根中执行相应读写。默认范围仍限工作区，不因有授权通道而自动放宽。
5. 外部读取只申请读权限；创建/写入只申请写权限；覆盖、移动、删除继续要求各自的现有权限维度。不得因同意一次文件读取而额外获写权限，反之亦然。Provider 自身已配置的模型请求通道不是新一次 `mcli_http` 调用。
6. 十个 Provider 分别验收发送和接收。共用适配器、SDK 或 HTTP Handler 不等于某个厂商的附件协议已通过验证。
7. Runtime 内部、会话历史和控制事件传 Blob 引用，不传整段 Base64。厂商协议要求的 Base64 仅在 Provider 网络边界生成；小块 IPC 二进制数据也可按帧编码，不塞进整条 invoke。
8. 不自动改模型、抽帧、OCR、转码或把附件替换成文字。确需这类转换时另建显式工具/选项，不作为附件读取的隐式降级。

本方案修正此前“按所选模型的有效能力保留附件”和“读取后再申请发送授权”的建议。

## 2. 调研方法、证据等级与限制

- 尝试建立 CodeMap 基线时返回 `BASELINE_SOURCE_MISMATCH`；尝试使用已有基线创建 `session` 工作区又返回 `STORAGE_FORMAT_INVALID`。未提交、暂存或清理用户文件来绕过问题。
- 本次按不可用回退规则，用有目录过滤的 Bash `rg` 定位，再分段读取实际源码。下列行号是当前基线的定位信息，不是以后实施时可直接使用的编辑范围。
- 检查了十个 Adapter、十个 Worker 入口、共享请求转换、工具网关、权限、Blob、消息持久化和现有附件测试。
- 复跑现有三项附件映射测试通过，但使用 `--no-build`，仅证明已有 Debug 测试产物通过；没有构建本次源码，也没有调用真实厂商。
- “有代码分支”“已有自动化”“真实服务验收”必须分别记录。本文不把未运行的测试、未确认的厂商格式或未核对的 SDK 行为写成已支持。

## 3. 现有链路与实际缺口

### 3.1 文件工具与结果

| 落点 | 当前事实 | 修改意义 |
| --- | --- | --- |
| `src/Madorin.AI.Runtime.Tools.Builtin/BuiltinToolRegistry.cs:70` | `mcli_fs_read` 必填 encoding，输出行号文本 | 保留文本语义，增加独立附件读取工具 |
| `src/Madorin.AI.Runtime.Tools.Builtin/FileSystem/BuiltinFsToolExecutor.cs:10` | 文本文件读取上限 1 MiB | 附件不能复用这个文本上限 |
| `src/Madorin.AI.Runtime.Tools.Builtin/FileSystem/BuiltinFsToolExecutor.Operations.cs:660` | 读取后调用文本解码 | 二进制需要独立读取分支 |
| `src/Madorin.AI.Runtime.Tools.Abstractions/ToolResult.cs:5` | 已有 OutputJson、单个 ResultBlob、ResultHash | 第一版单文件工具可复用，避免先引入批量结果模型 |
| `src/Madorin.AI.Runtime.Tools.Abstractions/IToolResultBlobStore.cs:7` | StoreAsync 接受整个 ReadOnlyMemory | 新增流式入口，避免视频整块常驻内存 |
| `src/Madorin.AI.Runtime.Server/BlobStagingStore.cs:48` | 存储已有 Begin/AppendChunk/Complete、哈希与配额 | 在现有实现上增加流式写入，不另建附件仓库 |
| `src/Madorin.AI.Runtime.Services/Tools/ToolGateway.cs:1219` | 校验 Blob 引用；OutputJson 存在时 ResultHash 对应 JSON | 元数据与附件分别校验，不能把两个哈希混用 |
| `src/Madorin.AI.Runtime.Modes.Expert/ExpertModeOrchestrator.cs:1268` | ResultBlob 转成嵌套 BlobRefContentBlock | Runtime 已有表达能力，Provider 尚未完整消费 |

### 3.2 权限并不是“已有外部读取授权就能用”

- `RuntimeToolPolicy.CreateRestricted` 在 `src/Madorin.AI.Runtime.Tools.Abstractions/RuntimeToolPolicy.cs:28` 默认允许工作区读根，显式 Grant 的上限为 null。
- `ToolAuthorizationService.IsDefaultAllowedTool` 在 `src/Madorin.AI.Runtime.Services/Tools/ToolAuthorizationService.cs:579` 已允许现有只读工具，新工具必须加入同一规则。
- `BuiltinFsPathPolicy.ResolveRequestedPath` 在 `src/Madorin.AI.Runtime.Tools.Builtin/FileSystem/BuiltinFsPathPolicy.cs:113` 把规范化和工作区边界检查合在一起，直接拒绝工作区外路径。
- `BuiltinToolConsentScopeResolver.FileReadScope` 与写入 scope 在 `src/Madorin.AI.Runtime.Tools.Builtin/BuiltinToolConsentScopeResolver.cs:250` 一带调用同一严格解析，外部目标在构造授权 scope 之前就失败。
- `ToolGrantCalculator.NormalizeRoots` 在 `src/Madorin.AI.Runtime.Services/Tools/ToolGrantCalculator.cs:181` 拒绝工作区外读根和写根；宿主不能用现有 `AllowedReadRoots` / `AllowedWriteRoots` 合法表达已同意的外部范围。
- `ToolConsentCoordinator.ExpandRunFilesystemRoots` 在 `src/Madorin.AI.Runtime.Services/Tools/ToolConsentCoordinator.cs:809`，以及 `StandaloneToolGrantFactory` 在 `src/Madorin.AI.Runtime.Cli/Tools/StandaloneToolGrantFactory.cs:73`，会把 Run 级文件根替换为工作区根。
- `ToolGateway` 在 `src/Madorin.AI.Runtime.Services/Tools/ToolGateway.cs:191` 已把准备阶段的 ToolPermissionRequiredException 转为 NeedsPermission；普通 UnauthorizedAccessException 则转为工具失败。

因此，外部路径的宿主申请入口和 Grant 模型**已经存在**，但 `read`/`write` 的 scope 解析、权限根规范化与最终路径解析仍在更早的步骤阻断路径，没有到达正常申请并依 Grant 执行的完整闭环。当前仅缺“授权 scope 能表达外部根，并且文件执行器仅在该方向、该 Grant 覆盖范围内接受它”；无需另造授权通道或额外 `AllowedExternalReadFiles` 字段。

### 3.3 附件在多个地方被手填能力拦截

| 落点 | 当前问题 |
| --- | --- |
| `src/Madorin.AI.Runtime.Services/Tools/ToolResultPreview.cs:34` | 非文本 Blob 仅在 supportsFileBlobs=true 时保留，否则变成说明文字 |
| `src/Madorin.AI.Runtime.Modes.Expert/ExpertModeOrchestrator.cs:1018` | 传入 Provider.Capabilities.Files；随后把已降级结果写入会话，丢失原附件结构 |
| `src/Providers/Madorin.AI.Runtime.Providers.Abstractions/ProviderCapabilityValidator.cs:68` | 当前输入中的 Blob 按 Vision/Audio/Files 拒绝 |
| `src/Providers/Madorin.AI.Runtime.Providers.Abstractions/ProviderRequestCompatibilityProjector.cs:213` | 历史 Blob 按同一能力过滤 |
| `src/Providers/Madorin.AI.Runtime.Providers.Abstractions/ProviderRequestCompatibilityProjector.cs:314` | 当前调用的 Blob 也被拒绝，包括工具结果内嵌附件 |
| 各 Adapter 的 ValidateCapabilitiesAsync/CompleteStreamingAsync | 重复进入共享校验；只改 Orchestrator 不够 |

本次只移除附件相关的能力判断，不顺手重构 reasoning、structured_output、tool_calling、Skills 等既有规则。涉及附件的 schema、广告工具筛选也要检查，避免残留 Files 门禁。

### 3.4 Provider 实际路线

| Provider | 当前输入路线与证据 | 顶层 Blob 输入 | 工具结果附件 | 附件回包 |
| --- | --- | --- | --- | --- |
| OpenAI | `OpenAIProviderAdapter.cs:156` 默认 Responses API；`:446` 解析 Blob 为 DataContent | Adapter 有输入映射；仅有模拟测试，Worker 无 resolver | `:522` 序列化内容数组，没有递归解析 Blob | `:534` MapUpdate 无 DataContent/UriContent 分支 |
| Anthropic | `AnthropicProviderAdapter.cs:125` Minimal SDK；`:428` 解析顶层 Blob | Adapter 有输入映射；仅有模拟测试，Worker 无 resolver | `:504` 同样序列化内容数组 | `:517` MapUpdate 无附件分支 |
| OpenAICompatible | `OpenAICompatibleProviderAdapter.cs:152` 注入 Chat Completions IChatClient 复用 OpenAI 转换 | 共享 Adapter 有映射；模拟测试仅验顶层 Blob，Worker 无 resolver | 继承 OpenAI 缺口，但网络格式不是默认 Responses | 继承输出映射缺口 |
| AzureOpenAI | `AzureOpenAIProviderAdapter.cs:231` 按部署构建 Compatible；有 api-version Handler | 共享 Adapter 有映射；未找到此 Provider 附件专测，Worker 无 resolver | 继承 Compatible 缺口 | 继承 Compatible 缺口 |
| Aliyun | `AliyunProviderAdapter.cs:35` Compatible / DashScope 兼容端点 | 共享 Adapter 有映射；现有测试含手填 Vision=false 的拒绝断言，Worker 无 resolver | 继承 Compatible 缺口 | 继承 Compatible 缺口 |
| Gemini | `GeminiProviderAdapter.cs:35` 使用 OpenAICompatible，不是原生 generateContent | 共享 Adapter 有映射；未找到此 Provider 附件专测，Worker 无 resolver | 继承 Compatible 缺口 | 继承 Compatible 缺口 |
| GLM | `GlmProviderAdapter.cs:27` Compatible | 共享 Adapter 有映射；未找到此 Provider 附件专测，Worker 无 resolver | 继承 Compatible 缺口 | 继承 Compatible 缺口 |
| Ollama | `OllamaProviderAdapter.cs:33` Compatible，不是原生 /api/chat | 共享 Adapter 有映射；未找到此 Provider 附件专测，Worker 无 resolver | 继承 Compatible 缺口 | 继承 Compatible 缺口 |
| Kimi | `KimiProviderAdapter.cs:202` 独立同步 ConvertMessages | 没有 BlobRefContentBlock 分支，也没有 Blob resolver 参数 | `:313` 仅序列化内容数组 | 更新映射只处理文本/思考/工具/用量 |
| DeepSeek | `DeepSeekProviderAdapter.cs:193` 独立同步 ConvertMessages | 没有 BlobRefContentBlock 分支，也没有 Blob resolver 参数 | `:286` 仅序列化内容数组 | 更新映射只处理文本/思考/工具/用量 |

表中未写完整路径的 Adapter 均位于 `src/Providers/Madorin.AI.Runtime.Providers.{名称}/`。表述是源码观察，不是厂商支持承诺。

### 3.4.1 本轮 Provider 源码快照交叉核对

- 现有 `ProviderCompletionTests.CompleteStreamingAsync_BlobReferenceMapsToProviderData` 仅对 OpenAI、Anthropic、OpenAICompatible 的顶层 image/png `BlobRefContentBlock` 测试进程内 Adapter；Fake resolver 返回的是文本 marker bytes，Fake HTTP 仅断言 request body 含这段 Base64。它不是有效 PNG，不验证厂商 part 结构、图像理解、工具结果附件、Worker 或回包。
- OpenAI 与 Anthropic 的 Adapter 顶层 `BlobRefContentBlock` 分支调用 `IProviderBlobResolver`，验长度/SHA-256/MIME，再建 MEAI `DataContent`。同一 switch 遇到 `ToolResultContentBlock` 时先调用同步 `ConvertToolResult`；非纯文本内容被 source-generated JSON 序列化，因此嵌套 Blob 不走 resolver，仍只是 JSON。
- OpenAI 与 Anthropic 的 `MapUpdate` 仅映射 text/reasoning/function-call/usage；未见 DataContent/UriContent 的输出附件事件映射。Anthropic 另外有独立 `Protocol/AnthropicMessageMapper`，它只映射 Text/Reasoning/ToolCall/ToolResult，且不支持 BlobRef。
- OpenAI 另外有独立 `Protocol/OpenAIMessageMapper`，为同步纯映射，switch 未处理 BlobRef；不能把主 Adapter 的异步 resolver 分支当作该 Mapper 已支持。
- OpenAICompatible 封装 OpenAI Adapter 的 MEAI Chat Completions 路径，直接注入 Blob resolver 的构造可用同一顶层转换。AzureOpenAI、Aliyun、Gemini、GLM、Ollama 进程内包装也向 Compatible Adapter 传 resolver；但这只证明本地转换可传，不证明每个厂商兼容端点接收多模态工具续调或返回附件。
- Kimi、DeepSeek 的 `ConvertMessages` 均是同步实现，只处理 Text/Reasoning/ToolCall/ToolResult；没有 BlobRef 分支、没有 resolver。其 `ConvertToolResult` 对复杂结果同样序列化整个 ContentBlock[]，故嵌套 Blob 不会转为厂商附件 part。
- 十个 Worker `Program.cs` 均没有传 `blobResolver`。`ProviderWorkerIpc` 仍为 1、单帧上限 16 MiB；invoke 只带 RuntimeProviderRequest，不存在 Worker Blob read/write 帧。因实际运行路线为 Worker，Adapter 层的可注入 resolver 尚未接通生产路径。
- 本轮搜索未发现任何 Provider 回包 `DataContent`/`UriContent`/图片或音频内容映射；现有 Provider 事件联合类型也没有附件完成事件。故“Provider 能返回附件并在 Runtime/Host/Client 可取回”目前无代码闭环和对应自动化证据。
- `ProviderCapabilityValidator` 仍按 `Vision`/`Audio`/`Files` 对 Blob 拒绝；Compatible/包装 Provider 复用该行为。Aliyun 既有测试直接验证默认 `Vision=false` 时 Image Blob 验证失败。因此今天的源码行为与“由服务器返回不支持”决策仍冲突。

逐 Provider 判断只按上述代码路由归纳；未验证每家真实网络 endpoint。区分“主 Adapter 接受 DataContent”“Worker 可以解析并供数”“服务端实际接收/回应”“回包保真下载”四个不同层级，不将前一层记作完整支持。

### 3.4.2 阻断级问题：工具结果把附件引用当普通 JSON

这是附件发送链路的 P0 阻断，不是显示层瑕疵。

1. 当前 OpenAI、Anthropic、Kimi、DeepSeek 的工具结果转换会把包含附件引用的 `ContentBlock[]` 序列化为 JSON，然后作为 `FunctionResultContent` 的普通结果值。发出的主要是 `blobId`、长度、MIME 等**引用元数据**，不是 Blob 对应的图片/文件字节，模型拿不到可识别附件内容。
2. 这条当前路径本身没有把 Blob 字节内联进去，所以准确说，现状直接导致附件内容丢失/模型只看到引用描述。若为“修复”而把真实文件转成 Base64 或 byte[] JSON 塞入工具结果，则会把二进制扩大为 Base64 文本（约为原字节的 4/3，尚未计 JSON 与 tokenizer 额外成本）；大文件会造成请求体、token 与上下文预算急剧膨胀，还可能撞上 Provider 的请求/帧上限。
3. 两种行为都不接受：既不能只传 Blob 引用 JSON 假装已传附件，也不能把文件字节/Base64 塞入工具返回 JSON、prompt 或普通文本 part。引用描述可以作为简短、有限的审计元信息，但必须和真实附件 part 分开。
4. 正确路径是在 Provider 网络边界按对应 API 把已授权 Blob 解析为原生 image/document/audio/video/file 内容 part，或上传为厂商 `file_id` 后按协议引用。Runtime/Worker 间继续使用 Blob 引用与有界分块，不构造整文件 JSON 字符串。
5. 若厂商协议不支持在 function/tool result 中直接附附件，则在 Provider 的临时请求投影中按协议保留全部 tool-call/result 回执，再添加**与本轮工具调用关联**的真实附件输入 part。不得破坏多个并行工具调用的结果配对，也不能用 Blob 引用 JSON 冒充文件交给模型。
6. 通过 IPC/网关白名单流式读取 Blob 到 SDK/厂商附件上传端；不得为了兼容统一抽象先将大 Blob 复制到 JSON 字符串或无上限 byte[]。SDK 若最终只接受内存 Buffer，须明确验证单文件/请求配额和峰值内存，并在超限时失败；不得回退到 Base64 prompt。
7. 增加阻断回归：网络请求必须包含协议原生附件 part 或经验证的 `file_id`；只含 `blobId` 元数据 JSON 判失败。测试文件内容不可出现在整体 Base64、data URL 或 byte[] JSON 中；图片和大视频分别检查请求尺寸、token/context estimate、内存峰值。

结论：附件必须通过模型/厂商识别的附件通道传输。JSON 只承载控制字段、简短元数据或 Blob 引用，不承载完整附件字节。

### 3.5 Worker 与接收公共协议

- 十个 `src/Providers/Madorin.AI.Runtime.Providers.*.Worker/Program.cs` 的 BuildAdapter 均没有注入实际 Blob resolver。
- `ProviderWorkerHost.Invoke.cs:70` 的工厂只收到连接配置；`ProviderWorkerFrames.cs:37` 的 invoke 只携带请求与引用，没有附件读写通道。
- `ProviderWorkerIpc.cs:8` 单帧限制 16 MiB。不能直接把大视频编码后追加到 InvokeFrame，也不应通过增大上限掩盖问题。
- `src/Providers/Madorin.AI.Runtime.Providers.Abstractions/RuntimeProviderEvent.cs:6` 没有附件事件；`EventFrame` 因而无法承载模型返回的二进制内容。
- `src/Madorin.AI.Runtime.Contracts/Content/ContentBlock.cs:32` 已有 BlobRefContentBlock，但 Host 事件没有对应附件可用通知。
- `ConversationStore.cs:2153`、`:2209` 的部分读取转换/引用收集只遍历顶层 blob_ref；新增工具内嵌附件后，下载、删除保护、恢复与引用统计须逐处检验递归行为。
- `BlobStagingStore.GetReferenceAsync` 的内存元数据丢失后会回落为 application/octet-stream，不能依赖这条回落恢复原 MIME/文件名和权限。

## 4. 目标数据流

```text
模型调用 mcli_fs_attach(path)
  -> ToolGateway + 默认权限/外部只读 Grant
  -> 准备阶段规范化并固定文件句柄
  -> 受限流式读取 -> BlobStore（完整文件，真实 MIME，SHA-256）
  -> ToolResult（小型 JSON 元数据 + ResultBlob）
  -> 持久化原始 ToolResultContentBlock，保留嵌套附件
  -> Provider 附件映射（不看用户模型能力）
  -> Worker resolver 经 IPC 分块获取本次允许的 Blob
  -> 厂商要求的图像/文件/音视频内容 part 或 upload/file_id
  -> 厂商正常结果 / 厂商不支持错误

厂商回包附件（二进制/远程文件引用）
  -> Provider 解析并流式写入 Worker Blob sink
  -> IPC 分块写入 Runtime BlobStore -> 完成确认
  -> Provider 附件事件（引用）
  -> 原生会话历史 + Outbox 附件事件
  -> Host/Client 按 Blob 通道下载
```

“接收”分两项验收：厂商真正收到了输入字节；Runtime 真正保存并交付了厂商输出附件。只收到分析文字不算输出附件验收。

## 5. 工具与 Blob 改造

### 5.1 新工具契约

- 新增 ToolId：`mcli_fs_attach`，DisplayName：`Read file as attachment`。
- 输入第一版只要求 `path`，可选 `expectedHash`；不提供模型猜测的强制 MIME、Base64、provider、model、是否授权等字段。
- 无 encoding/startLine/endLine。附件不可按字节截断后伪装为完整文件。
- 能力为 filesystem.read，Risk 为 SensitiveRead，RequiresApproval=false。加入默认只读工具列表和子 Agent 合法委派工具范围，不绕过其父 Grant 交集。
- 输出 JSON：fileName、contentType、byteLength、sha256；结果另带 ResultBlob。文件名只用 basename，不向 Provider 发送完整本地绝对路径。
- 沿用单 ResultBlob；多文件可多次调用。暂不新增 create_many 或通用附件集合工具。

### 5.2 实现边界

1. 在 BuiltinFsToolExecutor 新增 `FileSystem/BuiltinFsToolExecutor.Attachments.cs`，复用现有句柄、准备执行与路径防护；Operations 大文件只补路由，不整文件重写。
2. 构造函数注入已有 IToolResultBlobStore 与时间源，RuntimeExecutionCore 同时装配；测试构造保持兼容或明确更新。
3. 为 IToolResultBlobStore 增加流式 StoreAsync/OpenReadAsync 路径；旧 ReadOnlyMemory 入口保留，允许委托流式实现。
4. 流式读取同时检查字节配额与 cancellation，增量计算 SHA-256；失败/取消必须 Abort，不能留可见未完成引用。
5. 以 MIME 签名识别结合文件扩展名判断内容类型；不把“用户要求的类型”当真实类型。未知类型保留 application/octet-stream，不假装可识图。
6. 附件沿用 RuntimeLimits 的 Blob/Run/Invocation 限制，不套 1 MiB 文本限制。视频大文件必须实测内存峰值与背压；Provider SDK 的整块内存限制另行记录。
7. 当 OutputJson 与 ResultBlob 同时存在，ResultHash 按现有网关规则对 JSON 计算，附件完整性用 ResultBlob.Sha256；不复用一个值做两种含义。
8. BlobReference 建议追加可选 FileName，在上传、完成、持久化、Host 下载及 resolver 中保留；同哈希不同文件名/MIME 的描述应属于引用，不用全局单条元数据覆盖。

## 6. 读取授权方案

### 6.1 工作区内

保持 CreateRestricted 的默认读根，新增 mcli_fs_attach 的默认许可；路径边界、危险路径、链接/重解析点、过期 Grant 等仍校验。不新增弹窗，也不新增“上传批准”阶段。

子 Agent 继续要求已有显式委派 Grant，沿用当前规则；本专项只消除同一已授权读取的重复用户审批，不取消 Agent 隔离或宿主硬上限。

### 6.2 工作区外

不新增权限 DTO、数据库字段或第二个授权流程。复用宿主已经返回并由 `ToolConsentCoordinator` 保存、绑定 Agent/Run/CallId 与上限交集的 Grant。ScopeResolver 先根据本次参数描述目标路径及所需读/写维度；宿主返回 Grant 后，Coordinator 继续用该 scope 收窄并交集，避免授权范围大于本次批准目标。Standalone 的 `CreateGrantFromScope` 复用相同 scope。一次调用还是 Run 级授权沿用 `CanGrantForRun` 和既有宿主策略，不在本方案假定额外 UI 控件或更宽目录 Grant。

修复范围是让显式批准的根到达现有 Grant 与文件执行器，同时保留默认拒绝和最小权限：

1. 把路径处理拆成“安全规范化”及“校验操作范围”。相对路径先在工作区内解析；工作区外相对穿越仍拒绝，避免 `../` 绕过。外部目录以清晰的绝对路径作为授权 scope。
2. 规范化函数仍拒绝设备命名空间、ADS、保留的 `.madorin` 状态路径、符号链接/重解析点以及其他现有危险路径；显式授权不是绕过这些安全检查的特权。
3. 调整 `BuiltinToolConsentScopeResolver`：对安全的外部 read、list、search 目标返回包含规范化目标根的 `AllowedReadRoots`；对 create/overwrite/append/edit 返回 `AllowedWriteRoots`，原有覆盖权限仍独立表达。该解析只处理路径并生成授权范围，不打开/读取目标文件内容。
4. `ToolAuthorizationService` 对新 scope 的覆盖检查继续核验读根、写根和操作 flags；授权缺失时进入现有 `NeedsPermission` 流程。不可安全规范化的路径仍直接 fail closed，不发送授权请求。
5. 修改 `ToolGrantCalculator.NormalizeRoots`：验证路径本身安全且绝对、规范化及操作相关限制，不再一概因其超出 WorkspaceRoot 而丢弃一份**显式宿主 Grant**。这一例外只对 ScopeResolver 已规范化的文件系统 scope 生效；其他工具的执行根仍不得因共用 Grant 而获得外部文件根。`Intersect`、`IsSubset`、父 Grant 链和 MaximumPermission 仍取最小交集；空根不能被解释成无限授权。
6. 默认 PermissionContext 仍只包含 WorkspaceRoot。授权 Coordinator / Standalone Grant 根据 scope 创建权限时，保留批准的外部根，不把外部根静默替换成 WorkspaceRoot，也不升级为盘符根或其父目录。
7. `BuiltinFsPathPolicy.ResolveReadPath` 在路径位于 WorkspaceRoot 内时使用现有默认读根；位于外部时仅当 AllowedReadRoots 明确覆盖规范化目标时放行。写路径对称使用 AllowedWriteRoots。路径“能规范化”不等于“已授权”。
8. 同一操作的读/写维度保持分离：读取外部文件只需要相应读授权；新建或写入需要写授权；overwrite、move、delete 另按既有 flags/risk/approval 校验。读根不能当写根，写根不能当读根。
9. Run Grant 的范围扩展仅可发生在已批准范围内：文件级 scope 不升级为整个 WorkspaceRoot，更不得扩大外部目录；一旦当前 Grant 是有限根集合，后续 Run 重用只能保留相同或更小根。Standalone 与宿主 Grant 行为保持一致。
10. 保存与回填沿用现有 Grant 状态/JSON 结构；执行时仍验证 Run、Agent、父授权链、过期、撤销、允许 ToolId/CallId。外部范围不能顺带追加 `process`、`mcp` 或其他 filesystem ToolId；外部读写只绑定这次获批的工具和 scope，overwrite/move/delete 仍检查各自的 flags/risk/approval。若现有宿主 Grant 无法安全收窄工具集合，则拒绝提升，不静默扩大。
11. scope 中暴露给用户的外部路径应足以识别目标，但不要记录文件内容；审计继续记录路径摘要、参数哈希和授权结论。
12. 等待授权期间如文件路径目标变化，授权后重新安全检查并固定句柄；可用 `expectedHash` 时验证内容未变，避免对同一路径被替换后的另一个文件执行已批准操作。

**确认结论：**已有宿主授权请求/Grant 回填链路，不需要新建；外部读写操作的 scope、根校验和路径执行仍未放通，必须在现有读/写根 Grant 上分别补齐。授权必须是宿主返回的显式、有限 Grant；不能仅凭文件位于 workspace 外就自行放行，也不能因此获得其他 filesystem、process 或 network 权限。

## 7. 移除附件能力门禁与保真历史

1. ToolResultPreview 删除 supportsFileBlobs 驱动的非文本附件降级。普通工具大文本仍保留可见预览，但新附件工具的附件正文即使是 text/plain，也必须按附件意图保留。
2. 在工具结果处理入口依据 ToolId/明确附件标记区分“大型文本结果 Blob”与“读取为附件的 Blob”，不能仅凭 MIME 误把文本附件解码为预览。第一版 mcli_fs_attach 可按工具身份处理，不必引入多个新 discriminator。
3. Orchestrator 先持久化原始结果，再对纯文本工具做有界模型预览；不得把附件降级结果写回权威历史。
4. ProviderCapabilityValidator 的附件 case 不按 Vision/Audio/Files 拒绝；Projector 的历史及当前调用规则均无条件保留合法 Blob，包括嵌套 Content。
5. ValidateCapabilitiesAsync 在所有入口保证同一附件语义，避免只修 CompleteStreamingAsync 而 SDK 单独预检仍拒绝。
6. 非附件能力判断不在本专项泛化放宽。对现有“未声明 Vision 拒绝图片”的测试定向改写；增加三项附件标记全 false/缺省/未知模型仍发出真实请求的回归。
7. 本地安全、哈希/租期、限流/配额、无效 MIME 与实际编解码错误仍可阻止传输。这类错误叫“传输/适配实现错误”，不得谎报“模型不支持”。未知文件类型使用合法 application/octet-stream，不能仅因类型未知就预判模型不支持。
8. 不同模型续接仍保留附件引用。服务端拒绝时保留错误、原文件与历史，允许用户自行选择模型后重试，不自动删除附件。
9. 原始历史持久化与 Provider 文本预览分离后，历史中的大型纯文本工具 Blob 仍需在请求副本生成有界文字预览；不能因为统一保留 Blob 而把原本正常的文本工具结果变成错误的文件输入。附件意图来自明确的工具/事件身份，MIME 本身不决定是否降级。

## 8. Worker 双向 Blob IPC

在现有 stdin/stdout 分帧协议扩展，不引入工作目录文件交换，不把整个 Blob 当作一条 JSON invoke。

- 增加带 InvocationId、TransferId、BlobId 的 read.request/read.chunk/read.complete 和 write.begin/write.chunk/write.complete/write.abort 消息；名称为设计建议，实施时一次冻结。
- 分块大小建议 256 KiB，有界缓冲、单帧上限和在途请求数；小块若使用 JSON byte[] 会有 Base64 放大，仍须计算编码后帧大小。
- Runtime 为每次 invoke 注册输入 Blob 白名单，递归收集当前输入与可访问历史；Worker 不能按猜测的 BlobId 读取别的 Session/Run 内容，也不能传本地 path 请求 Runtime 任意读盘。
- 输出写入与当前 invoke 绑定。Runtime 检查配额、总长度、SHA-256 后才返回正式 BlobReference；事件必须在完成确认后发送。
- 使用 input.read 与 output.write 两类不同令牌/传输身份；输出上传接口不得借文件名创建工作区外路径，input 请求只读取登记的快照 Blob，而非重新读原始文件。
- 扩展 Worker 工厂上下文，提供 invocation-scoped resolver 与 sink，十个 Program.cs 全部接入；不能把并发 invoke 的 resolver 存成无隔离全局变量。
- 新增流式 resolver/sink 契约，保留现有 IProviderBlobResolver 的适配兼容入口。大视频网络发送优先流/上传文件 ID，明确记录 SDK 必须缓冲的分支与上限。
- 读写响应、cancel、ping、event 共用通道时分离读循环与执行任务，避免在唯一读循环内等待另一个 IPC 响应造成死锁；写端串行保障帧完整性。
- 同一 InvocationId 在多轮 provider 调用期间绑定新的 request generation/传输上下文，防止旧响应串入下一轮；重试、并发、Worker 重启与取消须回收挂起传输。
- Worker IPC 当前 Version=1，本次双向帧为内部协议变更，升级至新版本并保持 CLI/Worker 成套发布。旧 Worker 明确拒绝协商，不以“缺 resolver”运行到半途。

## 9. 每个 Provider 的网络与回包改造

### 9.1 通用规则

顶层 Blob 和 ToolResultContentBlock 内 Blob 必须都解析为真实厂商内容，不能序列化出 `{type:blob_ref,blobId:...}` 就算完成。

对工具附件优先使用厂商原生支持的 tool result 多模态 part。网络格式不允许时，在 Provider 请求副本中先完整保留所有 tool_call 对应结果，再添加关联 CallId 的原生附件输入 part；不更改权威历史，不把 Blob 引用 JSON、Base64 字符串或伪造的用户文字当作文件内容，不能打断多个 tool_call/结果的配对顺序。

严禁附件原始字节、Base64、data URL 或逐字节 JSON 数组进入普通 prompt / FunctionResultContent JSON。JSON 元信息保持小而有界；完整文件只走 Blob / Provider 附件数据通道。Unsupported-media 错误沿用厂商返回，不以 JSON 内联降级。

附件编解码策略只由已实现的厂商协议/API 路线决定，不由手填模型能力决定。厂商需要上传时缓存 file_id 的键必须包含 Provider/Endpoint/账号、哈希与租期，不能跨 Key/地域复用。

回包解析遇到二进制 part、带 MIME 的 data URI、文件 ID/URL 时，保存原字节与 MIME/文件名，不仅拼接成 TextDelta。远程 URL 必须安全下载：限定 HTTP(S)、重定向与响应上限，防 SSRF，不向任意返回域名转发模型 API Key；私有 file_id 走对应已配置厂商 API。纯文本里的链接不自动抓取。

### 9.2 逐 Provider 验证表

| Provider | 输入必须单独核对 | 输出必须单独核对 | 不得作出的假设 |
| --- | --- | --- | --- |
| OpenAI | 当前 Responses 的 image/file/audio 等 part；function_call_output 能否直接带附件，必要时请求副本投影 | Responses 事件中的原始内容/文件引用及 SDK 是否暴露 | 默认路线不是 Chat Completions；SDK DataContent 不保证任意 video/mp4 被编码 |
| Anthropic | Minimal SDK 图片/document 与 tool_result.content 的真实序列化；必要时扩展 Minimal 包 | SDK 返回非文本内容的保留及 Runtime 映射 | 图片/PDF 输入不代表厂商能返回任意视频/音频 |
| OpenAICompatible | Chat Completions image_url/input_audio/file/vendor part，嵌套工具附件投影 | 返回 audio/image/file 等兼容字段与私有扩展 | 所有兼容 endpoint 不是完整同一协议；不强制改 /responses |
| AzureOpenAI | 当前部署路径/api-version/Header、图片文件格式，需换 API 路线时独立实现 | Azure 回包附件和私有下载身份 | 不把 public OpenAI 路径/上传 API 直接拼到 Azure |
| Aliyun | DashScope 兼容图片、视频、音频/文件扩展，必要时厂商专用 codec | 同一实际 endpoint 的多模态字段，单独测试 | 不能沿用未声明 Vision 就拒绝；兼容路由不自动拥有其他生成 API |
| Gemini | 当前 OpenAI 兼容端点逐格式测试；若实现视频/Files 需原生 generateContent/Files 路线，保留明确路由 | 原生/兼容回包 inlineData/fileData 等各自解析 | 现有 GeminiAdapter 是 Compatible wrapper，不是原生 Gemini SDK |
| GLM | 当前兼容路线图像/视频厂商 part 与工具续调 | 真实返回字段与 SDK 保留情况 | 不能把视频作为普通 file part 就宣布已支持 |
| Ollama | 当前 /v1 兼容图像；若切原生 /api/chat 则单独 codec 与协议测试 | 原生/兼容实际非文本回包，不存在则明确记录 | 本地服务不等于任何本地模型都懂附件，也不等于生成附件 API 已实现 |
| Kimi | 新增异步 Blob 转换与 resolver；保留 reasoning/tool/maxTools 行为，核对 Moonshot 图像/视频/文件格式 | 独立 SDK 回包解析与 sink | 缺输入分支不能用模型能力或通用文本转换代替 |
| DeepSeek | 新增异步 Blob 转换与 resolver，保留 reasoning Handler，按实际 endpoint 尝试合法附件格式 | 独立回包解析，服务端无附件输出时明确记录 | 当前常用模型是否支持由服务端反馈，不在 Runtime 写模型黑名单 |

实施前需核对使用中的 SDK 版本和厂商当前文档；本文没有进行真实厂商支持矩阵认证。某 SDK/API 路线无法表达一种模态时，应补厂商 codec 或明确报告实现限制，而不是按用户能力 flag 丢弃。

## 10. 接收、存储与 Host/Client 交付

1. 新增 Provider 层附件完成事件（拟名 BlobProducedProviderEvent），只携带已确认 BlobReference 与附件标识；流式 bytes 走 Blob sink/IPC，不走 ProviderEvent JSON。
2. OpenAI/Anthropic/Kimi/DeepSeek 的更新映射补非文本内容；其余 wrapper 经共享路线也要逐个回包测试。SDK 不暴露的厂商字段须扩展/补 codec，不能落入无 default 的 switch 静默丢弃。
3. Orchestrator 在同一 assistant 消息中保留文本与 BlobRefContentBlock 的顺序，并纳入 partial persistence、取消/失败恢复、续接和摘要保护。
4. 增加可协商的 Host 附件可用事件（拟名 AttachmentAvailableEvent），进入持久化 Outbox/GSN；客户端可按已有 Blob 通道下载，不在事件写 bytes。
5. 新旧协议兼容必须显式设计：旧 Host 可保留正常 Run 生命周期并从支持的历史查询获取附件引用；新实时事件仅发给协商成功的客户端。不把这一运行时传输协商混同模型 Vision/Files 配置。
6. Runtime Blob 存储需持久保留原 MIME、文件名和访问归属。恢复不能仅凭 blob 文件存在或内存 _references 推断权限；按持久化会话/调用关联与描述校验。
7. 附件引用递归参与备份、历史 Metadata/Omit/Stream、Session 删除保护、回收、跨 Session 同哈希保护和工具结果恢复；二进制不能被 UTF-8 GetString 强行展开。
8. 第一版不新增图片/视频生成工具与独立生成端点；接收范围是现有对话 endpoint 的实际附件回包。某 Provider/API 没有这种回包时，登记“该路线不提供”，不造成功记录。

## 11. 协议、数据与文件修改清单

### 11.1 公共协议预登记

- 当前真实版本：Host 协议 1.1/兼容 1.0，DbSchemaVersion=16，Worker IPC=1；以 `ProtocolVersions.cs` 与 WorkerIpc 为准，不沿用旧模板中的 1.0 注记。
- 拟追加：BlobReference.FileName、附件可用事件/协商表面；外部路径授权复用现有 Grant 的 `AllowedReadRoots` / `AllowedWriteRoots`，不新增外部路径权限字段。
- 新增联合事件不能假定旧源生成客户端忽略未知 discriminator，必须按协商投影或协议版本隔离；实施前使用现有协议变更清单逐项冻结。
- 外部路径授权本身不新增 Grant/数据库字段，因此不单独引起 Db Schema 迁移；新增附件 Blob 文件名或历史关联若确需持久化字段，再按真实存储设计评估 Schema 和回退。
- Source-generation 同步 RuntimeJsonContext、RuntimeProviderJsonContext、ProviderWorkerJsonContext 与协议快照；禁止反射兜底。
- 本专项不改变 GSN 确认、CallId 幂等、Run 单终态和 Agent 权限继承。

### 11.2 代码落点

| 项目/目录 | 计划修改 |
| --- | --- |
| Tools.Builtin | Registry、FsToolExecutor、新 Attachments partial、只读路径分支、ConsentScopeResolver |
| Tools.Abstractions | 流式 BlobStore；单 ResultBlob 保留。ToolRequestedScope 只在实际证明现有路径目标无法表达时才需演进，不新增外部权限字段 |
| Services/Tools | 默认附件读取许可、外部 ScopeResolver/根交集和 Grant 收窄、Run 根扩展规则、文本预览、Blob 校验 |
| Contracts | BlobReference、Host 事件/协商、RuntimeJsonContext、ProtocolVersions（按冻结结果）；外部读/写授权复用现有 Grant 根字段 |
| Persistence.Abstractions / Persistence.Sqlite | 外部路径授权不新增持久化字段或 Schema 迁移；仅在 Blob 引用元数据确需持久化时评估迁移 |
| Persistence.Files | 二进制引用保真、递归遍历、删除保护、Metadata/Omit/Stream |
| Server | RuntimeExecutionCore 装配、BlobStagingStore 流式/归属、ProviderWorkers 双向 IPC、事件/下载 |
| Cli/Tools | StandaloneToolGrantFactory、现有权限提示与 Grant 根回填；保留宿主已批准的读/写根及覆盖等独立权限维度 |
| Client / Transport.NamedPipes | 可协商附件事件、上传下载和文件名保留；复用 FramedBlobChannel |
| Providers.Abstractions | 附件能力放行、保真投影、resolver/sink、Provider 附件事件、序列化 |
| Providers.WorkerHost | 新帧、Version、读写分派/背压、工厂上下文、JsonContext |
| 十个 Provider 与十个 Worker | 输入/工具附件/输出 codec 与 resolver/sink 注入，厂商专测 |
| tests / docs / build | 新旧协议、权限、逐 Provider 矩阵、Worker/AOT 发布门禁、工具契约与版本目录 |

上述为后续实施范围，本次没有修改这些代码文件。每次实施前仍须检查目标目录 README 与实际最新源码，不依据旧行号直接编辑。

## 12. 执行批次与验收清单（当前 0%）

### P0 契约冻结与最小失败证据

- [ ] 冻结 mcli_fs_attach、现有读/写根 Grant 的外部路径语义、附件事件、Worker 帧及兼容策略；工具结果附件禁止引用 JSON 冒充真实附件，也禁止 Base64/字节 JSON 内联。
- [ ] 用真实 PNG/JPEG/PDF/MP4 fixture 复现工具结果只传 JSON、Worker 无 resolver、能力 false 过滤、宿主批准的外部读写根仍被执行层拒绝、回包丢弃。
- [ ] 建十个 Provider 双向验收表，分开“格式代码/模拟通过/真实通过/该路线不提供”。

每个 Provider 的最终验收记录至少包含：实际 endpoint、API 路线、模型、SDK/版本、附件 MIME、顶层输入与工具续调请求结构、能力配置为空/false 时的行为、Worker 传输、输出字节哈希或厂商明确不提供的证据。HTTP 200、模型答复“收到文件”、请求中出现 Base64 都不能单独当作附件协议成功证明。

### P1 工具、授权和 Blob

- [ ] 单文件附件工具、流式存储、MIME/文件名、哈希和配额完成。
- [ ] 工作区内部主 Agent 0 次用户审批；子 Agent 沿用委派 Grant。
- [ ] 工作区外未授权读操作进入现有宿主授权；批准根后可读，拒绝/过期/撤销不可读。
- [ ] 外部创建/写入按写根授权，覆盖/移动/删除仍检查各自的独立 flags 与审批；只读许可不隐式升级为写。
- [ ] 外部授权严格遵守实际批准根，不扩盘根、不扩工具集合、不跨 Agent/Run；不能提升的高风险操作保持现有拒绝/审批行为；危险路径仍拒绝。

### P2 Worker 双向管道

- [ ] 输入白名单读取、输出流式写入、有界分块、确认后发布引用完成。
- [ ] 十个 Worker 全部装配；取消、重启、并发多轮、串 Key/Session、假 BlobId、超配额回归。
- [ ] 超 16 MiB 附件不形成超大 invoke；端到端文件长度/SHA-256 一致。

### P3 保真与全部 Provider 输入

- [ ] Vision/Audio/Files 全 false/缺省仍发出合法网络请求；嵌套工具附件和历史续接同样保留。
- [ ] 十个 Provider 每个核对顶层输入、工具调用后下一轮、多工具结果顺序、真实 HTTP path/header/content part。
- [ ] 含嵌套附件的 FunctionResultContent 必须映射为原生附件 part/file_id，而非仅 Blob 元数据 JSON；断言正文不含整体 Base64/data URL/byte[] JSON，并测 payload 与 context 大小。
- [ ] 图片、PDF、音频、视频分开记录；无法表达的协议补 codec，不按模型名单过滤。
- [ ] 服务端返回不支持时可观测且保留厂商错误、引用和历史，无自动模型切换/转换。

### P4 全部 Provider 输出、恢复与 SDK

- [ ] 十个 Provider 逐个验证实际输出附件路线；文字回答与附件下载验收分开。
- [ ] 验证 Provider 输出原生附件 part、data URI、file_id/URL、分块中断、伪造 MIME/长度/哈希和危险下载地址；data URI 仅按厂商协议解析回包，不作为输入 JSON 化方案。
- [ ] assistant 混合内容持久化、Host 事件重放/确认、Client 原字节下载、嵌套引用删除保护、重启续接。
- [ ] 旧 Host/新 Runtime、新 Host/旧 Runtime、旧 Grant、旧 JSON 与 Worker 版本失配测试。

### P5 发布门禁

- [ ] 新源码 Debug/Release 构建与受影响测试通过，逐项评审协议快照。
- [ ] Windows win-x64 Native AOT 的 CLI + 十个 Worker 成套发布，源生成与 SDK 非文本转换无新增裁剪问题。
- [ ] 真实厂商/账号/endpoint/model/API 路线分别留下证据；无凭据的项目标记未验收，不用模拟结果顶替。
- [ ] 记录视频输入/输出内存峰值、取消后临时数据、IPC 帧峰值、跨会话安全和发布回退结果。
- [ ] 同步工具契约、宿主集成指南、运行期错误说明、目录版本与文档导航，最终报告所有实现文件。

不能以“三个已有进程内测试通过”或“十个 Worker 能启动”替代本清单完成。

## 13. 本次验证记录

```powershell
dotnet test tests/Madorin.AI.Runtime.Provider.Tests/Madorin.AI.Runtime.Provider.Tests.csproj `
  --no-build --no-restore `
  --filter "FullyQualifiedName~CompleteStreamingAsync_BlobReferenceMapsToProviderData" `
  --verbosity minimal
```

2026-10-31 本机结果：通过 3、失败 0、跳过 0。测试只涉及 OpenAI、Anthropic、OpenAICompatible 的顶层 image/png Blob + Fake resolver + Fake HTTP server；断言请求包含 Base64。当前 fixture 实际是文本 marker，不是有效 PNG，且没有断言厂商全部内容结构，因此只能算序列化回归证据。

本次未做真实厂商调用、未新增实现代码、未构建最新源码、未执行 AOT 发布。后续真实验收不需要用户先填写模型附件能力，仍需要合法 Provider 凭据及用户明确选择的实际 endpoint/model。
