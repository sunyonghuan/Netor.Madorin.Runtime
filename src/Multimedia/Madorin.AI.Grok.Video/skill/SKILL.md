---
name: skill-grok-video
description: 通过 xAI Grok Imagine Video API 生成视频，支持文生视频、异步查询和 MP4 下载。
---

# Grok 视频生成

当前实现异步 `text-to-video`。命令包括 metadata、list-models、version、generate、query、download。不支持图生视频、参考图、首尾帧、编辑和延长。

## 调用

```text
skill-grok-video metadata --request-id <id>
skill-grok-video list-models --request-id <id> --api-url <url> --api-key <key>
skill-grok-video version --request-id <id>
skill-grok-video generate --request-id <id> --prompt <text> --api-key <key> --output-dir <dir>
skill-grok-video generate --request-id <id> --prompt <text> --api-key <key> --no-wait
skill-grok-video query --request-id <id> --task-id <task> --task-context-file <context.json> --api-key <key>
skill-grok-video download --request-id <id> --task-id <task> --task-context-file <context.json> --api-key <key> --output-dir <dir>
```

也可使用 `--request-file`。支持 `--duration`、`--aspect-ratio`、`--resolution`。API 根地址缺省为 `https://api.x.ai/v1`。提交追加 `videos/generations`，查询和下载使用 `videos/{task_id}`。API Key 只作为 argv 参数传递。

模型按 `--model`、文件 `model`、默认 `grok-imagine-video-1.5` 选择。默认值只给省略模型的调用使用。实际目录由本次 API 返回。

默认生成会提交、按 5 秒间隔轮询，完成后下载 MP4。`--no-wait` 只返回任务 ID 和 `task_context`。宿主将返回的 task_context 对象保存为 JSON 文件；任务恢复必须传原任务 ID、--task-context-file、同一 API 地址和有权限的 Key。上下文恢复原模型与输出槽位，查询不会重新选择模型。默认总预算 300 秒，--timeout 可覆盖。

`--try-run` 不联网、不读取媒体、不创建输出目录。预览包含提示词，按敏感内容处理。

请求文件见 [request.schema.json](references/request.schema.json)，协议映射见 [protocol.md](references/protocol.md)。
