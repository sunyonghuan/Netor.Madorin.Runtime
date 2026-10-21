---
name: skill-grok-image
description: 通过 xAI Grok Imagine Images API 生成图片，支持文生图及受支持的图片宽高比。
---

# Grok 图片生成

当前实现同步 `text-to-image`，支持 metadata、list-models、version、generate；不支持图片编辑、参考附件、异步查询或下载。

## 调用

```text
skill-grok-image metadata --request-id <id>
skill-grok-image list-models --request-id <id>
skill-grok-image version --request-id <id>
skill-grok-image generate --request-file <json> --output-dir <dir> --api-key <key> --model grok-imagine-image-2.0 --request-id <id>
```

也可直接传 `--prompt <text>`；支持 `--aspect-ratio` 和 `--num-images`。API 根地址缺省为 `https://api.x.ai/v1`，程序追加 `images/generations`。API Key 仅作为 argv 参数传递，不写入请求文件或输出。

模型按 `--model`、文件 `model`、默认 `grok-imagine-image-2.0` 顺序选择。当前声明 `grok-imagine-image-2.0`；模型可用性最终由 xAI 账户决定。

请求强制要求 `response_format=b64_json`，响应中的 JPEG/PNG/WebP 数据经公共解码验证后落盘；URL 响应不是本地已验证产物，不接受为成功结果。附件当前仅按公共契约校验并记录忽略。

使用 `--try-run` 时不联网、不读取媒体、不创建输出目录。预览会包含提示词，按敏感内容处理。

请求文件格式见 [request.schema.json](references/request.schema.json)，协议映射见 [protocol.md](references/protocol.md)。
