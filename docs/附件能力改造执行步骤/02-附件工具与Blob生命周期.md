# 执行步骤 02：附件工具与 Blob 生命周期

> 状态：进行中，先实施流式 Blob 存取。前置：[01-P0 工具结果附件映射](./01-P0-工具结果附件映射与上下文保护.md)本地门禁通过，真实厂商支持未验收。

## 1. 目标与边界

新增 `mcli_fs_attach(path)`，把被授权的本地文件作为独立 Blob 结果交给已完成 P0 映射的 Provider 管道。保留 `mcli_fs_read` 的文本/按行语义，不把二进制、图片、视频混入文本解码工具。

第一版每次读取一个文件，结果包含有界 JSON 元数据与一个 `ResultBlob`。多文件通过多次工具调用完成；不新增 Base64 字段或一口气返回整目录附件的工具。

## 2. 契约

- ToolId：`mcli_fs_attach`，文件读取类风险和 `filesystem.read` 权限。加入 builtin registry、executor、工具筛选和 Source Generation 所需的契约。
- 输入：必需 `path`，可选 `expectedHash`。不接受模型声明为真实 MIME 的字段，不在参数里指定 Provider/模型。
- 输出 JSON：basename `fileName`、检测后 `contentType`、`byteLength`、SHA-256。文件绝对路径不进工具结果或 Provider prompt。
- 二进制：`ToolResult.ResultBlob`。如元数据 JSON 和 Blob 同时返回，JSON 的 `ResultHash` 与 Blob 的 `Sha256` 语义分别保持，不混用。
- 文件名/MIME 若新增公共字段，必须检查协议快照、序列化、恢复与访问授权；未证明必要前不要另造一套附件 Contract。

## 3. Blob 读写

1. 检查 `IToolResultBlobStore`、`BlobStagingStore` 当前接口和限制，再增加流式 Store/OpenRead 路径；保留兼容旧小对象的 `ReadOnlyMemory<byte>` API。
2. 从 `BuiltinFsPathPolicy.PinReadFile` 得到文件句柄后再读，避免仅凭路径重新打开导致 TOCTOU。
3. 按有界 chunk 流式复制，边读边算 SHA-256，累计单 Blob、Run、Invocation 配额；取消/异常时中止上传、删除 staging 文件并释放句柄。
4. Hash/MIME/length 全部完成验证后才发布不可变 BlobReference。未完成 uploadId 不得出现在 ToolResult、history 或 worker 读取白名单中。
5. MIME 用内容签名加扩展名推断；保留获知的媒体类型，不信任文件扩展名单独给二进制“验明身份”。无法识别时使用合法 `application/octet-stream`，不是拒绝附件。
6. 依现有 Blob quotas 和 file-size policies 设清晰边界。没有流式 provider upload 能力的 SDK 必须把内部缓冲限制在配置配额内，不允许无限 MemoryStream 或二进制 JSON。
7. 明确同 hash 不同文件名/MIME 的归属模型；不能通过全局 `BlobId -> 单个 mutable MIME/FileName` 让 Session/Run 相互覆盖元数据。

## 4. 结果和历史

- ToolGateway 验证 ResultBlob 合法引用，并在幂等恢复、持久化、工具回执恢复时保留类型/名称/授权范围/期限。
- 按 `ToolId=mcli_fs_attach` 识别“意图为文件附件”的 Blob，即使其 MIME 为 `text/plain` 也不能被通用 ToolResultPreview 展开成行文本。
- 普通过大文本工具结果仍可生成有界预览副本供模型续调；权威 history 保留原始 JSON/Blob 引用，不持久化被 Provider 降级后的短文本。
- ConversationStore 的 Blob Collect/Transform/Session delete protection 扩展为递归 traversal，避免 ToolResultContentBlock 内嵌 Blob 被遗漏、错误删除或 UTF-8 解码。
- Tool call/result history 持有的附件引用期限要覆盖至少调用续接和配置的 Session retention；清理按关联引用而非只看 Run 结束立即删除。

## 5. 测试

- PNG、JPEG、PDF、普通二进制和大文件流经 ToolExecutor -> Gateway -> Store -> ToolResult 的字节、长度、Hash 完全一致。
- 文件增长/缩短、读取过程中取消、配额溢出、磁盘满、SHA 不一致及写入完成前崩溃均不产生有效引用或留 staging 垃圾。
- `text/plain` 附件仍保留 Blob intent；过大普通文本仍能生成小预览，history 保真。
- JSON 元数据恒定受限，不含 Base64/file body；单 Blob 内存峰值受 chunk 与 SDK buffering 上限约束。
- 多 Session 同 hash、不同名称/MIME、删一个 Session、清理/恢复、历史 Metadata/Stream/Omit 均通过归属测试。

## 6. 验收

工具对合规工作区文件创建完整 Blob 引用；Blob 完整传输、取消清理、配额、哈希及历史生命周期闭合。未完成 P0 Provider 请求映射前，不得把此步骤描述为“模型已读附件”。

## 7. 实施进度

- 已核对 `IToolResultBlobStore`、`BlobStagingStore`：改造前只有整块内存 Store/Read，底层已有 staging 分块写入、SHA-256、单 Blob/Run/全局配额和 Abort 清理；流式接口复用这条路径。
- 已增加流式 Store/OpenRead；复制缓冲为 128 KiB，租用缓冲在结束时清零归还，输入流所有权留给调用方。发布前核对预期长度/哈希；取消、源读取异常、配额溢出都会 Abort，不发布引用。
- 已增加 `mcli_fs_attach` registry/executor/consent scope/Runtime 接线。它只读取 `PinReadFile` 固定的句柄，输出 basename、MIME、长度、SHA-256 与独立 ResultBlob；`mcli_fs_read` 仍是文本工具。MIME 已覆盖 PNG/JPEG/GIF/WebP/PDF/WAV/ID3 和带 ftyp 签名的 .mp4，未知类型用 octet-stream，不接受调用方自报 MIME。
- 已将新工具接入现有默认只读工具授权与临时 Agent 工具筛选；Gateway 测试首调不提供宿主 Grant，确认工作区内沿用默认读取权限。外部路径提权仍留给步骤 03 验证，不增设附件发送授权。
- 附件暂沿用 ConversationStore 的 365 天引用租期；这是当前保留基线，不等于已完成可配置 Session retention。文件名留在每条工具元数据，不在全局 hash 上新增可变文件名。
- 已验证 Gateway 同时保存元数据 JSON 和 Blob，ResultHash 对应 JSON、Blob.Sha256 对应文件；删除源文件后同 CallId 重试从持久化结果恢复，不重新读取源文件。
- ToolResultPreview 保留 attach 工具的文本类 Blob intent，不按手填 Files 能力替换二进制附件；普通过大文本仍生成预览。ConversationStore 已递归收集/转换嵌套工具 Blob，Stream 模式不把附件 UTF-8 解码，Omit 只操作读取副本，删除 Session 时保护另一 Session 的嵌套共享 Blob。
- Debug 定向 78 项通过，ConversationStore 测试 23 项通过。包括 2 MiB 分块读取、失败清理、真实文件字节/哈希、扩展名伪装、Gateway 幂等恢复与嵌套历史读取/共享删除保护；JPEG/PDF 夹具只验证签名和字节保存，不能充当厂商可读文件验收。
- Release 附件/工具/授权联合回归 144 项通过，ConversationStore Release 23 项通过，Protocol 49 项通过，构建零警告。补测授权与 Expert 共 38 项，35 通过、3 项既有 summary/compaction 失败，与步骤 01 全量记录相同；未修改 summary 业务。
- BlobTransferTests 端到端回归：5 项全部通过。初跑超大内联文本测试等待 Provider 引用超时；读取事件后定位为测试 Provider 未声明 ContextWindow 导致 `context_window_unknown`。仅补夹具 ContextWindow 和 run.failed 诊断，不放宽生产上下文检查，复跑通过。此回归验证既有 Blob 通道，不等于附件工具经 Worker 到真实模型的端到端验收。
- 2026-10-31 后续修正：用户确认未知上下文窗口不应禁止调用，现已删除预算函数的硬阻断，并撤回上述测试夹具的窗口补值；测试 Provider 保持未知窗口时 BlobTransferTests 仍为 5 项全部通过。全局配置不变的真实 DeepSeek CLI 文本调用成功，附件 Worker/厂商收发仍未验收。详见[上下文兼容修复记录](../执行步骤/上下文压缩与手动压缩命令修复方案.md)。

### 未关闭项

- 现有 BlobStagingStore 的 `BlobId -> BlobReference` 下载元数据仍可能被同 hash 不同 MIME 的后续写入覆盖；引用本身和工具 JSON 保留各自 MIME，但下载接口的归属/恢复问题尚未关闭，不能标记本步骤完成。
- Invocation 配额的累计预留、崩溃重启后的 staging 回收、磁盘满注入、可配置 Session retention、完整跨 Session 清理恢复仍需补齐。
- Provider SDK 的整块附件缓冲上限和 Worker invocation-scoped 通道在步骤 04 接通；当前工具读取不等于已在实际 Worker 中把附件交给模型。
