# 阿里云百炼万相 2.7 视频协议

核对日期：2026-11-10。

- https://help.aliyun.com/zh/model-studio/text-to-video-api-reference
- https://help.aliyun.com/zh/model-studio/image-to-video-general-api-reference
- https://help.aliyun.com/zh/model-studio/error-code

## 映射

| 项目 | 实现 |
| --- | --- |
| 提交 | `POST /api/v1/services/aigc/video-generation/video-synthesis`，必须带 `X-DashScope-Async: enable` |
| 查询 | `GET /api/v1/tasks/{task_id}`，由任务 ID 和调用方 API 根地址组成 |
| 认证 | Bearer，只来自 argv |
| 默认模型 | 文生视频 `wan2.7-t2v`；图生视频 `wan2.7-i2v-2026-04-25` |
| 有声 | 不传音频时官方自动配音；可选公网 HTTPS 音频分别映射到 `input.audio_url` 或 `media.driving_audio` |
| 时长/分辨率 | 2 至 15 秒，默认 5；`720P` 或 `1080P`，默认 `1080P` |
| 比例 | 仅文生视频，`aspect_ratio` 映射为官方 `ratio`；图生视频跟随首帧 |
| 单首帧 | HTTPS URL 原样提交；本地 png/jpeg/webp 只在正式执行编码为 data URI |
| 状态 | `PENDING`、`RUNNING`、`SUCCEEDED`、`FAILED`、`CANCELED`、`UNKNOWN` |
| 产物 | 只下载 `output.video_url` 指向的 MP4，并用 ffprobe/ffmpeg 验证 |
| 异步 | `supports_async=true`；上下文不含地址、密钥、提示词和结果 URL |
| 取消 | 本批未调用管理端点，`remote_cancellation=unsupported` |

旧版首帧接口、首尾帧和视频续写不在本批。官方允许 HTTP 和 `oss://` 素材，本批只接受无用户信息的 HTTPS。结果 URL 未公布完整主机白名单，不把 URL 列为产物。
