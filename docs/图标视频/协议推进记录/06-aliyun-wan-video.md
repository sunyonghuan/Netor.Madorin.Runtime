# 阿里云百炼万相 2.7 视频协议

## 批次状态

- 状态：官方核对完成，视频技能源码已按本记录落盘；未运行构建、测试、发布或真实 API。
- 记录日期：2026-11-10。
- 技能：`skill-aliyun-video`。图片仍由 `skill-aliyun-image` 负责，不合并为 `ImageVideo`。
- 不在本批：万相 2.6 及以下旧版视频协议、首尾帧、视频续写、本地音频上传、临时 OSS 地址、回调、远端取消、静态模型目录、Runtime 集成、正式发布。

## 官方核对

核对日期：2026-11-10。不以参数统计稿代替。

- [万相 2.7 文生视频](https://help.aliyun.com/zh/model-studio/text-to-video-api-reference)
- [万相 2.7 图生视频](https://help.aliyun.com/zh/model-studio/image-to-video-general-api-reference)
- [错误码](https://help.aliyun.com/zh/model-studio/error-code)
- 旧版首帧页 [图生视频-基于首帧（2.1-2.6）](https://help.aliyun.com/zh/model-studio/image-to-video-api-reference) 只用于确认不纳入。

新版协议只支持 wan2.7。HTTP 必须异步，请求头 `X-DashScope-Async: enable`。缺该头时官方返回 “current user api does not support synchronous calls”。

- 提交：`POST {api-root}/services/aigc/video-generation/video-synthesis`
- 查询：`GET {api-root}/tasks/{task_id}`
- 认证：`Authorization: Bearer <API Key>`
- 默认北京根地址：`https://dashscope.aliyuncs.com/api/v1`。现有域名仍可用；Workspace 专属域名和新加坡地址由调用方通过 `--api-url` 提供。模型、地址和 API Key 必须同地域。

创建响应取 `output.task_id` 与 `output.task_status`。`task_id` 查询有效期 24 小时。成功结果在 `output.video_url`，官方写明为 MP4（H.264），链接有效期 24 小时。示例主机包括 `dashscope-result-sh.oss-cn-shanghai.aliyuncs.com` 与 `dashscope-result-sh.oss-accelerate.aliyuncs.com`，未公布完整主机白名单。

状态为 `PENDING`、`RUNNING`、`SUCCEEDED`、`FAILED`、`CANCELED`、`UNKNOWN`。`UNKNOWN` 表示任务不存在或超过查询有效期。官方建议轮询间隔约 15 秒，查询接口默认 RPS 为 20。管理异步任务页提到取消，但本批未核对取消端点，不声明远端取消。

文档没有通用模型目录。`list-models` 在校验 request-id、HTTPS 地址和 api-key 后返回 `provider_error`，不回退静态清单。模型 ID 原样提交。

## 有声视频

万相 2.7 文生视频和图生视频在不传音频时，都会按画面自动生成背景音乐或音效。这就是本批接入的有声能力，没有单独的静音开关。

自定义音频只接受无用户信息的公网 HTTPS URL：

- 文生视频写入 `input.audio_url`。
- 图生视频写入 `media[]` 的 `type=driving_audio`，并且必须同时有一张首帧。

官方还允许 HTTP 和 `oss://` 临时地址。本批不实现上传文件换临时 URL，因此拒绝这两类地址。音频格式、时长和大小由服务端校验，本地不读取音频文件。

## 能力结论

| 能力 | 结论 | 依据 |
| --- | --- | --- |
| 文生视频 `text-to-video` | 纳入 | `wan2.7-t2v`，`prompt` 必填 |
| 单首帧图生视频 `image-to-video` | 纳入 | `media` 恰好包含一个 `first_frame`；`prompt` 可选 |
| 自动配音 | 纳入 | 省略音频 URL 时官方自动生成 |
| 自定义音频 URL | 纳入 | 仅 HTTPS；不读本地音频 |
| 首尾帧、视频续写 | 官方确认，本批不纳入 | 需要 `last_frame` 或 `first_clip`，留待后续批次 |
| 公开异步 | true | 查询路径可由任务 ID 和调用方 API 根地址重建 |
| 部分成功 | 不纳入 | `video_count` 固定为 1，`supports_partial=false` |
| 远端取消 | 不支持 | 取消端点本批未核实 |

## 字段

| 公共或 CLI 字段 | 官方字段 | 规则 |
| --- | --- | --- |
| `model` | `model` | 文生视频默认 `wan2.7-t2v`；图生视频默认 `wan2.7-i2v-2026-04-25`。非空且无控制字符即原样提交 |
| `prompt` | `input.prompt` | 文生视频必填。按码点计数，超过 5000 失败，不截断 |
| `negative_prompt` | `input.negative_prompt` | 可选，超过 500 个码点失败，不截断 |
| 单个 `first_frame`、`reference` 或 `source` | `input.media[].type=first_frame` | 图生视频恰好一张。远程 HTTPS 原样提交；本地 png/jpeg/webp 只在正式执行编码为 `data:{mime};base64,...`。try-run 不读媒体 |
| `provider_parameters.aliyun.audio_url` | 文生视频 `input.audio_url`；图生视频 `driving_audio` | 可选 HTTPS。带查询的签名 URL 在预览和回显中脱敏 |
| `resolution` | `parameters.resolution` | `720P` 或 `1080P`，缺省 `1080P`。小写 `p` 规范化为大写并记录转换 |
| `aspect_ratio` | `parameters.ratio` | 仅文生视频。`16:9`、`9:16`、`1:1`、`4:3`、`3:4`，缺省 `16:9`。图生视频传入即失败 |
| `duration` | `parameters.duration` | 2 至 15 的整数秒，缺省 5 |
| `provider_parameters.aliyun.prompt_extend` | `parameters.prompt_extend` | 缺省 true |
| `provider_parameters.aliyun.watermark` | `parameters.watermark` | 缺省 false |
| `provider_parameters.aliyun.seed` | `parameters.seed` | 仅显式传入才发送，范围 0 至 2147483647 |

不把 `width`、`height`、`size` 或 `fps` 猜测成 `resolution`/`ratio`。传入这些字段失败。

## 响应与恢复

提交后保存任务 ID。`PENDING` 和 `RUNNING` 继续查询，间隔 15 秒，并受同一次调用预算约束。`SUCCEEDED` 才下载 `video_url`。下载关闭自动重定向，最多手动跟随 3 次 HTTPS 跳转，再用公共视频验证器确认 MP4。URL 本身不是产物。

任务上下文只保存技能、Provider、任务 ID、任务、模型和单个输出槽位。不保存 API 地址、密钥、提示词、音频 URL 或结果 URL。查询和下载必须再次提供连接参数与 `--task-context-file`。

`FAILED` 归一为 `provider_error`。`CANCELED` 表示远端已取消，不改写成本地 `cancelled`。`UNKNOWN` 表示无法恢复。错误体、签名 URL 和 data URI 不回显。

## 错误映射

| 官方信号 | 公共错误 |
| --- | --- |
| HTTP 401/403，`InvalidApiKey`、`invalid_api_key`、`AccessDenied`、`access_denied` | `authentication_failed` |
| `Arrearage`，HTTP 402 | `insufficient_balance` |
| HTTP 429，`Throttling*`、`insufficient_quota` | `rate_limit_exceeded` |
| 错误码包含 `DataInspection`，或为 `IPInfringementSuspect` | `content_policy_violation` |
| HTTP 408/504 | `timeout` |
| 其他，包括模型不存在和参数被远端拒绝 | `provider_error`，保留安全的官方 code |

## 源码路径

- `src/Multimedia/Madorin.AI.Aliyun.Video/`
- 复用 `Madorin.AI.Multimedia.Core`，本批不修改公共库。

## 验证边界

按协议推进规则，本阶段不运行 `dotnet build`、`dotnet test`、AOT、发布脚本或真实 API。未验证账户权限、结果 URL 实际主机、重定向、自动配音是否出现音轨、自定义音频 URL、视频容器和取消终态。本地 PNG 透明通道未单独拒绝。
