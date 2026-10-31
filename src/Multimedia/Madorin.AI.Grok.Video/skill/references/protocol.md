# xAI Grok Imagine Video API 接入

## 当前范围

| 项目 | 规则 |
| --- | --- |
| Provider | xAI，API 根地址默认为 `https://api.x.ai/v1` |
| 提交 | `POST /videos/generations`，响应取 `request_id` 作为任务 ID |
| 查询 | `GET /videos/{request_id}`，状态为 `pending`、`done`、`failed`。其他值视为未知状态 |
| 下载 | `done` 时读取 `video.url`，必须是 HTTPS。下载前确认本机有 ffprobe 与 ffmpeg，再用二者校验 MP4 |
| 任务 | 仅 `text-to-video` |
| 模型 | 目录从目标 API 查询；`grok-imagine-video-1.5` 仅为兼容默认 |
| 附件 | 先校验结构和路径，再按原索引记录 `unsupported_attachment_role`，不读取媒体 |
| 异步 | `supports_async=true`，`task_context_required=true`。宿主保存返回的 task_context 对象；query/download 必须提供上下文文件与 argv 连接参数，恢复原模型、任务和全部槽位，并在远端返回模型时交叉核对 |

## 认证与端点

| 输入 | 含义 |
| --- | --- |
| `--api-key` | xAI API Key，作为 `Authorization: Bearer <key>` 发送；不得写入请求文件或输出 |
| `--api-url` | API 根地址。必须为 HTTPS；仅本机回环地址允许 HTTP，且不得包含用户信息、查询参数或片段 |

视频下载 URL 不附带 API Key。该 URL 可能带签名，只在内存中用于下载，不写入响应、诊断或任务上下文。

## 请求字段

| 公共字段 | Provider 字段 | 类型/默认 | 说明 |
| --- | --- | --- | --- |
| `prompt` | `prompt` | string，必需 | 原文透传 |
| `model` | `model` | string，默认 `grok-imagine-video-1.5` | argv → 文件 → 默认；非空且无控制字符即原样传递 |
| `parameters.duration` | `duration` | integer，默认 8，范围 1-15 | 单位秒 |
| `parameters.aspect_ratio` | `aspect_ratio` | string，默认 `16:9` | `1:1`、`16:9`、`9:16`、`4:3`、`3:4`、`3:2`、`2:3`、`21:9`、`5:2` |
| `parameters.resolution` | `resolution` | string，默认 `480p` | `480p`、`720p`、`1080p` |

`enable_audio`、`fps`、图生视频字段和其他 Provider 参数记录为 `ignored_parameters`。官方默认会生成音轨；本批不发送 `generate_audio`。非法类型、无效枚举和认证错误直接失败。

## 响应与产物

提交成功只保证拿到任务 ID。`status=done` 且 `video.url` 可下载、ffprobe 与 ffmpeg 均确认是可解码 MP4 后，才保留产物。文件名从 `video_001.mp4` 起避开已有文件，响应 `index` 固定为原任务槽位 1，与文件名序号独立。响应 `duration_seconds` 来自本地探测。公共 `response_id` 仍是本次 CLI 调用 ID。Provider 的 `request_id` 映射为顶层 `task_id`。HTTP 头 `x-request-id` 若存在，写入 `provider_metadata.request_id`。

查询响应省略 `model` 时，沿用已校验的上下文模型；返回非法或不同模型时为 `provider_error`，保留原上下文。成功查询始终返回完整 task_context，后续失败也保留已验证的任务 ID 和上下文。`list-models` 未传 `--timeout` 时总预算为 300 秒。

轮询间隔 5 秒，总时长受 `--timeout` 约束，默认 300 秒。本机取消或超时停止本地操作，不撤销远端任务；响应的 `remote_cancellation` 为 `unsupported`。JSON 响应限制为 4 MiB，拒绝非法 UTF-8、非对象及重复字段，不输出原始响应体。生成等待/下载遇到远端失败返回公共失败对象；query 的远端失败返回失败任务视图。

## 错误映射

| Provider 情况 | 公共错误 |
| --- | --- |
| HTTP 401/403 | `authentication_failed` |
| HTTP 408/504 | `timeout` |
| HTTP 429 | `rate_limit_exceeded` |
| 网络/TLS 传输失败 | `network_error` |
| 查询时状态仍为 `pending` 且命令是 download | `task_not_ready` |
| 其他非成功响应、未知状态、无效 JSON、非 MP4 | `provider_error` |
| 本地参数、请求文件或路径错误 | `invalid_parameter` |

协议依据：[xAI Video Generation](https://docs.x.ai/developers/model-capabilities/video/generation)，核对日期 2026-10-22。
