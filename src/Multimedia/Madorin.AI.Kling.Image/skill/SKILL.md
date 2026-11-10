---
name: skill-kling-image
description: 通过可灵 AI 生成图片。
---

# 可灵图片生成

当前实现可灵文生图。支持 `metadata`、`list-models`、`version`、`generate`、`query`、`download`。提交后用系统任务 ID 跨进程查询，下载并验证后只列出本地产物。

## 调用

```text
skill-kling-image metadata --request-id <id>
skill-kling-image list-models --request-id <id> --api-key <key>
skill-kling-image version --request-id <id>
skill-kling-image generate --request-id <id> --prompt <text> --api-key <key> --output-dir <dir> [--model kling-v3]
skill-kling-image query --request-id <id> --task-id <task> --task-context-file <file> --api-key <key>
skill-kling-image download --request-id <id> --task-id <task> --task-context-file <file> --api-key <key> --output-dir <dir>
```

连接地址缺省 `https://api-beijing.klingai.com`。API Key 仅通过 `--api-key` 输入，并以 Bearer 请求头发送。不实现 Access Key / Secret Key JWT。默认分辨率 `1k`，默认比例 `16:9`，默认数量 `1`。本批不接受参考图。try-run 不联网、不读取媒体、不写文件。

`list-models` 因官方没有通用模型目录端点而返回 `provider_error`，不会给出静态模型清单。图片成功状态是 `succeed`。官方没有取消端点。

请求结构见 [request.schema.json](references/request.schema.json)，字段依据见 [protocol.md](references/protocol.md)。
