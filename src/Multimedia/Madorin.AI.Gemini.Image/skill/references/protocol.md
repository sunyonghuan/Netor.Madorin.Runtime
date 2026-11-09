# Google Gemini 原生图片生成 API 接入

## 当前范围

| 项目 | 规则 |
| --- | --- |
| Provider | Google Gemini，API 根地址默认为 `https://generativelanguage.googleapis.com/v1beta` |
| 端点 | `POST /models/{model}:generateContent` |
| 任务 | 仅 `text-to-image`，同步等待并落盘 |
| 模型 | 模型目录从目标 API 查询；`gemini-2.0-flash-exp` 仅为兼容默认，不是白名单 |
| 附件 | 当前不实现 image-edit；统一附件先校验，再按原索引记录 `unsupported_attachment_role`，不读取媒体 |
| 异步 | 不声明 `supports_async`，不提供 query/download/no-wait |

## 认证与端点

| 输入 | 含义 |
| --- | --- |
| `--api-key` | Google Gemini API Key，作为 `x-goog-api-key: <key>` 发送；不得写入请求文件或输出 |
| `--api-url` | API 根地址；默认 `https://generativelanguage.googleapis.com/v1beta`，程序追加 `models/{model}:generateContent`。地址必须为 HTTPS；仅本机回环地址允许 HTTP，且不得包含用户信息、查询参数或片段 |

## 请求字段

| 公共字段 | Provider 字段 | 类型/默认 | 说明 |
| --- | --- | --- | --- |
| `prompt` | `prompt` | string，必需 | 原文透传；不翻译、不截断 |
| `model` | `model` | string，默认 `gemini-2.0-flash-exp` | 按 argv → 文件 → 默认选择；非空且无控制字符即原样传递，由服务判断可用性 |
| `parameters.aspect_ratio` | `generationConfig.imageConfig.aspectRatio` | string，默认 `auto` | 支持 `auto`、`1:1`、`16:9`、`9:16`、`4:3`、`3:4`、`3:2`、`2:3`、`2:1`、`1:2`、`19.5:9`、`9:19.5`、`20:9`、`9:20`、`21:9`、`5:2` |
| 生成数量 | `candidateCount` | 固定 `1` | 当前只声明一个图片产物槽位 |
| `responseModalities` | `generationConfig.responseModalities` | 固定 `[TEXT, IMAGE]` | 请求要求返回文本和图片片段；CLI 只接受图片 inlineData 作为产物 |

`size`、`width`、`height`、`quality`、`output_format` 等不属于当前 Gemini 适配器的参数会被记录为 `ignored_parameters`，不会伪装成等价转换。非法类型、无效枚举、任务/模型冲突和认证错误仍直接失败。

## 响应与产物

服务端同步成功响应应包含 `candidates[].content.parts[].inlineData.data`，可选 `mimeType`（如 `image/jpeg`）。缺少 MIME 时从 PNG/JPEG/WebP 文件签名探测格式。CLI 解码并验证完整容器和像素后写入 `image_001.jpg` 等不覆盖文件；响应仅列出已写入且验证通过的文件。URL-only 响应不视为本地成功产物。

本技能不发送 `deferred=true`，不支持 Google Gemini Deferred Generation。若 Provider 返回 HTTP 202、`status=pending/failed` 或仅有远端 `request_id`，统一返回 `provider_error`，不会把 Provider 请求追踪 ID 误当作本地关联 ID 或公开异步任务 ID。

## 错误映射

| Provider 情况 | 公共错误 |
| --- | --- |
| HTTP 401/403 | `authentication_failed` |
| HTTP 408/504 | `timeout` |
| HTTP 429 | `rate_limit_exceeded` |
| 网络/TLS 传输失败 | `network_error` |
| 其他非成功响应、HTTP 202 异步响应、URL-only 响应、无效 JSON、数量不匹配、base64/图片无效 | `provider_error` |
| 本地参数、请求文件或路径错误 | `invalid_parameter` |

原始响应体和 API Key 不输出；服务端 `x-request-id`（如存在）仅映射为 `provider_metadata.request_id`，不与公共 `response_id` 或任务 ID 混用。

协议依据：[Google Gemini Image Generation](https://ai.google.dev/gemini-api/docs/image-generation)，核对日期 2026-11-09。

## 模型发现协议

list-models 为联网命令，接受 --request-id、--api-url、--api-key、--timeout、--output-format json 与 --verbose。使用同一基础地址后的 GET models 和 x-goog-api-key 认证；仅保留服务实际返回的模型 ID，返回 model_source=api。当前模型 API 未提供可靠任务信息，故 models[].tasks=null；不按名称推断或过滤。重复/无效 ID、错误结构、非成功 HTTP 按公共错误失败，不回退静态清单。

metadata 离线返回 models=[]、顶层 tasks 协议定义和 model_discovery。生成不预先强制查询模型，try-run 只做 ID 结构和协议参数预检；模型存在性、权限与真实能力由服务决定，不使用静态白名单。





