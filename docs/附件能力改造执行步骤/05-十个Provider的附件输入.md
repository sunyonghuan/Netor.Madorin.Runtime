# 执行步骤 05：十个 Provider 的附件输入

> 状态：待执行 · 0%。前置：[01 P0](./01-P0-工具结果附件映射与上下文保护.md)、[04 Worker Blob 管道](./04-ProviderWorker双向Blob管道.md)。

## 1. 目标

把顶层用户附件与工具结果附件分别转换为当前 Provider/API 实际要求的原生图像、文档、音频、视频附件 part 或厂商 file upload/reference。移除手填模型能力对附件的本地阻断；仍验证本地授权、Blob 完整性、Content-Type 与大小约束。

## 2. 十 Provider 阶段门禁

| Provider | 当前 Adapter 主路线 | 必做方向 |
| --- | --- | --- |
| OpenAI | Responses API + MEAI/OpenAI SDK | 顶层 Responses image/file/audio part；核验 `function_call_output` 的内容结构能否直接表达附件，不支持时再做保持调用关联的请求副本投影；按 Responses 文件输入契约验证上传引用。官方：[图像输入](https://developers.openai.com/api/docs/guides/images-vision)、[文件输入](https://developers.openai.com/api/docs/guides/file-inputs)、[Responses API](https://developers.openai.com/api/reference/responses/create) |
| Anthropic | Netor.Anthropic.Minimal 0.1.5 / Extensions | image/document block；核验 `tool_result.content` 的多模态 block 与 SDK 实际序列化。图片 Base64 若位于协议原生 image source 字段是合法网络编码，不得复制进普通 tool-result 文本。官方：[Tool use](https://platform.claude.com/docs/en/agents-and-tools/tool-use/handle-tool-calls)、[Vision](https://docs.anthropic.com/en/docs/build-with-claude/vision)、[PDF support](https://docs.anthropic.com/en/docs/build-with-claude/pdf-support) |
| OpenAICompatible | Chat Completions 兼容 API | `image_url`、audio、file vendor extension；请求明确保持 chat/completions，不能强改 Responses。兼容只代表具体服务承诺的子集，逐 endpoint 记录扩展和缺项。 |
| AzureOpenAI | Azure 部署路径 + API version + Compatible SDK | 按当前部署 Chat 路线核对支持的图像/音频/文件 content item、deployment、api-version/header 和 endpoint；不得把 Public OpenAI Responses 路径或上传路由直接套用到 Azure。官方：[Azure OpenAI API reference](https://learn.microsoft.com/en-us/azure/ai-services/openai/reference#chat-completions) |
| Aliyun / DashScope | Compatible mode endpoint | 依具体百炼兼容端点核对 `image_url`、音频、视频等消息项与工具续调格式；现有 `Vision=false` 拒绝测试需改为真实请求测试，不以能力字段提前拒绝。官方：[OpenAI 兼容 Chat Completions](https://help.aliyun.com/zh/model-studio/qwen-api-via-openai-chat-completions) |
| Gemini | 当前 GeminiAdapter 实际是 OpenAICompatible | 只把当前配置的 OpenAI 兼容端点当作当前路线；官方兼容层不等价于 Gemini 原生 Files API，不能据原生 Files/视频文档推断 Compatible adapter 可用。若支持原生 `generateContent`/Files，须作为独立 route/mapper。官方：[OpenAI compatibility](https://ai.google.dev/gemini-api/docs/openai)、[Files API](https://ai.google.dev/gemini-api/docs/files) |
| GLM | OpenAICompatible | 依配置 BaseUrl 核验图像及厂商扩展；不能从 OpenAI-compatible 声明推断视频/文件工具结果或回包格式。官方：[GLM API 文档](https://docs.bigmodel.cn/) |
| Ollama | 当前 /v1 Chat Completions compatible | 官方兼容范围为 OpenAI API 子集；据当前 `/v1` 路线逐项验图像格式，不外推 PDF、视频、音频或附件回包。Ollama 原生 `/api/chat` 是另一条路线；具体模型不支持时保留服务器错误。官方：[OpenAI compatibility](https://docs.ollama.com/api/openai-compatibility) |
| Kimi / Moonshot | 独立 MEAI/OpenAI chat client | 加异步 BlobResolver 与输入映射，保留 reasoning、tools、maxTools；按当前 endpoint 核对图像、视频及上传引用。文档全文提取后写入 system/prompt 的问答示例不等价于附件传输，不得作为大文件附件的回退。官方：[Vision](https://platform.moonshot.cn/docs/guide/use-kimi-vision-model)、[文件理解](https://platform.moonshot.cn/docs/guide/use-kimi-k2-5) |
| DeepSeek | 独立 OpenAI-compatible MEAI client | 加异步 BlobResolver/Mapping；当前 Chat 消息内容契约允许多模态内容项，需据实际 endpoint 验证 tool 消息嵌套图片/文件结构，不预先维护模型支持黑名单。官方：[Create Chat Completion](https://api-docs.deepseek.com/api/create-chat-completion) |

每个 Provider 的表格证据需要跟着 API/SDK 版本。以上路线是当前源码，不代表截至某日该模型厂商已声明支持所有模态。

## 3. Adapter 共同实现

1. 在 Provider 边界读取 Blob bytes 或按厂商路径上传。先核验 authorization scope、Blob length/hash/MIME，再编码 API content part。
2. 请求副本转换必须异步以支持 resolver/network upload；取消令牌贯穿 Blob reader、上传、API 调用。
3. 对 tool result 附件优先用真实 tool-result 多模态结构；不支持时按厂商 tool-call/result 语义生成临时请求转换，保持调用与回执完整顺序。
4. Resolver 缺失、内容映射没有 codec、伪造 MIME 或超 Provider 上限均清晰失败，不返回“已发送”文字，不丢附件引用继续推理。
5. 所有 Capability validator、CompatibilityProjector、Provider wrapper 必须一致保留 Blob。user capabilities/model config 的 false/null 不阻断；不相关能力行为原样维持。
6. Provider 抛出的“不支持媒体类型/尺寸”等服务端错误原样归类或结构化保存，不自动切模型、切协议、抽帧/转码。
7. 请求日志、异常和诊断不记录 Base64、API Key、附件正文；body recorder 只在测试环境允许捕获，并对 production 禁用。

## 4. 每 Provider 测试要求

对每一个 Worker 真实入口和一个进程内 Adapter 测试：

- 图片顶层输入、图片作为工具结果输入、多个工具结果同时有附件、历史续接带图片。
- 能力 Flags 全 false/缺省时 HTTP 请求仍生成附件 part，而不是 Adapter 本地抛错或过滤。
- 捕获请求验证 API route、鉴权 Header、内容 JSON discriminator、MIME、Base64/URI/file_id 字段是该协议要求而非 Madorin 内部 Blob JSON。
- 选一份非文字 fixture 做内容解码/哈希检查；仅断言 body 包含“Base64 marker”不够。
- Worker Invoke JSON、普通 prompt 与工具结果文本不包含完整附件字节或本地 Blob 路径。Provider 网络请求若按该 API 要求使用 JSON Base64/data URL 原生附件 part，不应被误判为上下文内联；逐项证明附件落在协议附件字段、没有复制进普通文本，并记录请求体、文本 token estimate 与缓冲上限。
- Stub Server 明确返回不支持时保留错误、对应 CallId、历史附件引用。

在可用真实 endpoint 时验证原始图片通过协议附件字段发送、端点接受及回包可见性；按用户 2026-10-31 确认的口径，不以颜色/物体描述准确率作为附件传输门禁。没有 credentials 时将厂商验证状态标成“未验证”，不可用 Compatible SDK 模拟取代真实结论。

## 5. 阶段通过矩阵

每家 Provider × 每类附件分别记：源码 Mapper、请求 path/header/part 测试、Worker resolver 测试、真实 endpoint 测试、服务端错误回归、版本/endpoint 和对应官方接口依据。状态枚举：未实现、代码实现未测试、模拟通过、真实调用已验证、厂商路线不支持且显式错误。官方文档只能证明接口契约，不能代替目标账号、endpoint、模型的真实调用验收。

不要把 OpenAI/Anthropic/OpenAICompatible 共用转换的单个测试复制/参数化成十个全绿结果，而不检查每家 Worker 和实际协议变体。

## 6. 前置实测记录（2026-10-31）

已使用全局 8 个连接进行 CLI 预检，并对 5 种协议的进程内 Adapter 运行真实 PNG 顶层/工具结果测试。OpenAI 本机网关的两个方向通过原生字段字节验证和图案描述；Anthropic 本机网关顶层结构可识别、工具结果多次错图；DeepSeek/Aliyun 当前模型未识别；Kimi 新 ID 有结构回包但颜色不稳定。所有 CLI Worker 图片续调仍未通过，5 种未配置协议没有真实结果，不能宣布本步骤完成。详见[完整验证矩阵与失败证据](./验证记录-20261031-CLI与真实Provider附件.md)。

最新补测：用户确认 axis-code 欠费已充值，并指定视觉测试选型后，7 个连接的进程内顶层/工具结果 PNG 原生字段、原始字节及端点接受均通过；识别描述不准确仅作备注。Aliyun 用接口实际返回的 `qwen3.8-max`，DeepSeek 用 `deepseek-v4-flash`，Anthropic 用 Opus 4.6/4.8。此前错图不能直接归为映射错误，按[记录第 6 节](./验证记录-20261031-CLI与真实Provider附件.md)修订当前结论。CLI Worker 链路仍未通过，输出及其他模态未测，本步骤状态不变。

Worker 后续修复：输入分块桥与十家 Resolver 装配已实现，五条真实 CLI PNG 工具续调成功，见[记录第 7 节](./验证记录-20261031-CLI与真实Provider附件.md)。这替代上一时间点的 CLI 输入失败结论；其他模态、全十家真实账号及输出仍未验证。
