# 执行步骤 06：Provider 输出附件与 Host/Client 交付

> 状态：Provider 二进制回包、Worker 输出和 CLI 落盘子任务进行中；其余门禁未关闭。前置：[02 Blob 生命周期](./02-附件工具与Blob生命周期.md)、[04 Worker 双向 Blob 管道](./04-ProviderWorker双向Blob管道.md)。

## 1. 范围

此阶段的“输出附件”专指 Provider 对话 endpoint 实际返回的附件内容/文件引用；不含调用独立 image/video generation API，也不把 Markdown 链接自动下载成附件。厂商如果不支持该回包类型，记录这条 API route 不提供。

## 2. Provider 接收与 Blob 化

1. 对每个 SDK/协议检查响应 union 类型：二进制 data part、带 MIME data URI、厂商 file id/URL。文字内容与附件内容保留响应原始先后次序。
2. 新增 Provider 附件完成事件，仅传 Runtime BlobReference/顺序/关联 message id；正文由 Worker output sink 经 Blob IPC 流入 Runtime store。
3. 写入过程限制 size、mime allowlist、hash、time、rate；完整性没确认前不能发“附件可用”事件。
4. 专有 file_id 需要厂商 API 取回时，通过该 Provider 连接的身份 API 拉取；仅允许安全地址，不将模型 Key 转发给响应指定的任意域。
5. 普通文本中出现 URL 不等于附件。只有协议明确定义附件 URL/file id 的字段可触发下载/解析。
6. 无 response binary API/SDK 类型时需要补厂商 codec；不要在内容 mapping 的 default case 静默丢弃。

## 3. 会话和事件交付

2026-10-31 用户明确修订当前交付方式：Provider 回到 CLI 的附件由 CLI 存入主程序启动目录 `AppContext.BaseDirectory` 下的 `.attach`（不存在就创建），每个文件使用 GUID 唯一文件名。落盘完成并校验长度/哈希后，图片生成 Markdown 图片引用，其他附件生成 Markdown 链接，按原内容顺序组装到回复并复用文字事件推送宿主。权威历史保留 BlobRef 和交付 Markdown。当前首批不发送未经协商的新 Host 事件；下列新事件/下载能力作为后续兼容门禁保留，不替代用户指定的默认落盘交付。

- Orchestrator 在 assistant message 中按顺序保存 TextBlock 与 BlobRefContentBlock，支持仅附件、文本+附件和多附件。
- Partial response persistence、取消、provider failure、Run 重试、Session resume、history projection 都保留已确认的附件和未完成 transfer 状态。
- 工具结果内 Blob 与 Provider 输出 Blob 有不同 owner/生命周期，权限验证和删除保护必须准确归属 Session/Run/Invocation。
- `ConversationStore` 对 nested content 的 Blob Collect/Read Metadata/Stream/Omit/Session delete traversal 全面递归。
- Host 在线附件事件用已协商的新协议事件，携带可下载引用，不把文件字节塞进 GSN event JSON。事件持久化在 Outbox，ack/replay 语义遵守现有 GSN。
- Client 通过现有 `FramedBlobChannel` 下载，验证 byte length/SHA/MIME/name；支持取消和 partial download retry，不泄漏跨 Session 引用。
- 旧 Host 无附件事件能力时保持 Run 生命周期兼容；可通过支持的 history query 拿到 BlobRef/metadata。禁止给旧 Client 发送未知 discriminator 然后期待其忽略。

## 4. 验收测试

- Provider Fake 返回 binary/data URI/private file_id/URL，各路径实际生成 Blob 且 hash 原字节一致。
- MIME mismatch、超时、下载 redirect SSRF、私有 IP、DNS rebinding、超配额/长度不符、恶意 MIME 均阻断且不留下用户可见 Blob。
- 文本和附件顺序、tool call 后 Provider 回包、partial text stream + attachment、多个附件顺序正确。
- Run 事件断连/重连/replay、event ack、history query、Client 下载大小和 hash 精确一致。
- Session 删除、Blob duplicate hash 跨 Session 引用、过期回收和持久化恢复不误删另一会话仍被引用的内容。
- 十个 Provider 分别通过真实输出格式测试；无法输出的 endpoint 标为不支持/不提供，不计入可接收成功数。

## 5. 实施进度

- 已增加进程内 StoreOutputBlob 委托和确认后的 Provider 附件引用事件，不序列化委托、磁盘路径或完整附件正文到 Worker Invoke/event。
- 已从共享 MAF 边界提取 SDK DataContent，调用 output sink；四个实际 Adapter 映射引用事件，其余六家复用 Compatible Adapter。URL/file_id 未实现安全下载 codec 时明确失败，不自动下载模型字符串。
- 已实现 Worker IPC 3 输出分块、确认与调用结束/取消清理；CLI 交付使用 `.attach` 唯一文件和 Markdown 引用。音频 DataContent 分片在终态合并，保留前后文字顺序；无完成标记的音频不能按成功附件交付。
- 已通过真实 Worker 子进程配合模拟 HTTP 的输出测试，并贯通 OpenAI Worker → LocalRuntime → 启动目录 `.attach` → 宿主文字事件；不存在 Blob 的落盘失败返回 `AttachmentDeliveryFailed`，不推失效链接。此处不是厂商联网生成验收，也不是外部宿主 UI 渲染验收。

### 5.1 当前十个 Provider 输出覆盖

| Provider | 当前对话协议 / SDK 边界 | 本批输出验证 | 保留缺口 |
| --- | --- | --- | --- |
| OpenAI | Responses；补取 SDK 原生 `response.output_item.done` 的完成 image_generation_call 字节 | 独立 Worker 图片 300,001 字节、配额及完整 Markdown 链路通过 | 真实厂商输出未测试；不启用独立生成业务 |
| OpenAICompatible | Chat Completions / MEAI DataContent | 独立 Worker 分片音频写回通过 | 具体厂商是否提供此响应未验收 |
| AzureOpenAI | Chat Completions / MEAI DataContent | 独立 Worker 分片音频写回通过 | 同上 |
| Aliyun | Chat Completions / MEAI DataContent | 独立 Worker 分片音频写回通过 | 同上；原生 DashScope 专属输出未实现 |
| Gemini | 当前代码走 OpenAI Compatible route | 独立 Worker 分片音频写回通过 | Gemini 原生 generateContent inlineData 并非本项目当前 route，未覆盖 |
| GLM | Chat Completions / MEAI DataContent | 独立 Worker 分片音频写回通过 | 具体厂商是否提供此响应未验收 |
| Ollama | 当前代码走 OpenAI Compatible route | 独立 Worker 分片音频写回通过 | 原生 Ollama 专属输出未覆盖 |
| Kimi | Chat Completions / MEAI DataContent | 独立 Worker 分片音频写回通过 | 具体厂商是否提供此响应未验收 |
| DeepSeek | Chat Completions / MEAI DataContent | 独立 Worker 分片音频写回通过 | 具体厂商是否提供此响应未验收 |
| Anthropic | Messages / Netor.Anthropic.Minimal 0.1.5 | 已检查 SDK mapper；没有二进制输出成功证据 | SDK 仅映射 text/thinking/redacted/tool_use；其他 block 可能被静默丢弃，专属文件输出 codec 未实现 |

Anthropic [Files API 官方文档](https://platform.claude.com/docs/en/build-with-claude/files)明确：由 Skills 或代码执行工具生成的文件，其 file_id 可出现在 Messages 的 bash_code_execution_tool_result 中，通过 Files API 下载；用户上传文件不能按同样方式下载。当前 Runtime 不启用这些服务端生成能力，不能把普通 Messages 文字成功当成文件收件成功，也不能宣称所有 Anthropic 服务都不输出文件。专属响应 block 映射和按连接身份取回文件的缺口保留为后续逐协议处理项。

### 5.2 本批边界

- 已支持 SDK 实际提供的二进制 DataContent 和 OpenAI 完成图片；HostedFileContent/UriContent 遇到未实现的下载 codec 明确失败，普通文本 URL 不下载。
- SDK 需要完整缓冲的内容仍受 invocation 上限控制；Worker IPC 和 Runtime Pipe 有界分块不代表上游 SDK 常量内存。
- `.attach` 路径取 `AppContext.BaseDirectory`，不取工作目录/Provider 文件名；GUID + MIME 扩展名、CreateNew 防覆盖，长度/SHA 校验后才组装 Markdown。失败删除本次创建文件。
- 多附件、音视频实际厂商输出、取消/崩溃恢复、Outbox 重放、Session 生命周期、正式 AOT 仍按既有门禁逐项验收，不将本批落盘交付通过等同本步骤整体完成。
