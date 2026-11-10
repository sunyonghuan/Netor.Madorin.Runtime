# 可灵视频协议引用

核对日期：2026-11-10。来源：[可灵 AI 开放平台](https://klingai.com/document-api) 的 Kling 4.0 文生视频、图生视频、任务查询和错误码页面。

- 根地址默认 `https://api-beijing.klingai.com`。认证为 `Authorization: Bearer <API Key>`。
- 文生视频：`POST /text-to-video/{model}`。请求体为 `prompt`、`settings.duration`、`settings.resolution`、`settings.aspect_ratio`、`options.watermark_info`。
- 图生视频：`POST /image-to-video/{model}`。`contents` 只发送一个 `first_frame`，可选一个 `prompt`。本地图片在正式执行时编码为不带 `data:` 前缀的 Base64。
- 查询：`GET /tasks?task_ids={task_id}`。状态为 `submitted`、`processing`、`succeeded`、`failed`。成功后只下载 `outputs` 中 `type=video` 的 `url`。
- 模型原样进入路径，不改写到旧版 `/v1/videos/*`。没有通用模型目录端点，也没有取消端点。
- 任务上下文不保存地址、密钥或提示词。结果 URL 约 30 天有效。官方未公布主机白名单。下载只接受无用户信息的 HTTPS，最多跟随 3 次重定向，并校验 MP4。
