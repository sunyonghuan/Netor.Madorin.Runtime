---
name: skill-kling-video
description: 通过可灵 AI 生成视频。
---

# 可灵视频生成

当前实现 Kling 4.0 文生视频和单首帧图生视频。支持 `metadata`、`list-models`、`version`、`generate`、`query`、`download`。提交后用系统任务 ID 跨进程查询，下载并验证后只列出本地 MP4。

## 调用

```text
skill-kling-video metadata --request-id <id>
skill-kling-video list-models --request-id <id> --api-key <key>
skill-kling-video version --request-id <id>
skill-kling-video generate --request-id <id> --prompt <text> --api-key <key> --output-dir <dir> [--model kling-4.0]
skill-kling-video generate --request-id <id> --task image-to-video --image-url <https-url> --api-key <key> --output-dir <dir>
skill-kling-video query --request-id <id> --task-id <task> --task-context-file <file> --api-key <key>
skill-kling-video download --request-id <id> --task-id <task> --task-context-file <file> --api-key <key> --output-dir <dir>
```

连接地址缺省 `https://api-beijing.klingai.com`。API Key 仅通过 `--api-key` 输入。模型原样放入 `/text-to-video/{model}` 或 `/image-to-video/{model}`，不改写旧版端点。默认清晰度 `1080p`。时长只在显式传入时发送。本批不接受中间帧、尾帧和多图。try-run 不联网、不读取媒体、不写文件。

`list-models` 返回 `provider_error`。视频成功状态是 `succeeded`。官方没有取消端点。

请求结构见 [request.schema.json](references/request.schema.json)，字段依据见 [protocol.md](references/protocol.md)。
