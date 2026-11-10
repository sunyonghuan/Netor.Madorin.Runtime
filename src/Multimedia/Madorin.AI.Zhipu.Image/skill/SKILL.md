---
name: skill-zhipu-image
description: 通过智谱 CogView 生成图片。
---

# 智谱图片生成

当前实现 CogView 文生图。支持 `metadata`、`list-models`、`version`、`generate`。图片接口同步返回 URL，本技能下载并验证后只列出本地产物；不提供 `query` 或 `download`。

## 调用

```text
skill-zhipu-image metadata --request-id <id>
skill-zhipu-image list-models --request-id <id> --api-key <key>
skill-zhipu-image version --request-id <id>
skill-zhipu-image generate --request-id <id> --prompt <text> --api-key <key> --output-dir <dir> [--model cogview-4]
```

连接地址缺省 `https://open.bigmodel.cn/api`。API Key 仅通过 `--api-key` 输入，并以 Bearer 请求头发送。默认尺寸 `1024x1024`，默认质量 `standard`。`width` 与 `height` 成对转换为 `size`。本批不接受参考图。try-run 不联网、不读取媒体、不写文件。

`list-models` 因官方没有通用模型目录端点而返回 `provider_error`，不会给出静态模型清单。

请求结构见 [request.schema.json](references/request.schema.json)，字段依据见 [protocol.md](references/protocol.md)。
