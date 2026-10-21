---
name: skill-openai-image
description: 通过 OpenAI Images API 生成图片。用于受支持 GPT Image 模型的文生图。
---

# OpenAI 图片生成

当前支持 `text-to-image`；图像编辑、异步任务和其他图片任务尚未实现，不得将其视为支持能力。

## 调用

```text
skill-openai-image metadata --request-id <id>
skill-openai-image list-models --request-id <id>
skill-openai-image version --request-id <id>
skill-openai-image generate --request-file <json> --output-dir <dir> --api-url https://api.openai.com/v1 --api-key <key> --model gpt-image-1 --request-id <id>
```

也可直接传 `--request-id <id> --prompt <text>`。`--api-url` 缺省为 `https://api.openai.com/v1`。生成支持 `--model`、`--size` 或成对 `--width/--height`、`--quality`、`--num-images`。`--output-format json` 控制协议输出；图片编码用 `--provider-openai-output-format png|jpeg|webp`。API Key 仅作为 argv 参数，不要写入请求文件或日志。

本地兼容服务可使用 HTTP，主机仅限 .NET URI 识别的本机回环地址（如 `localhost`、`127.0.0.1`、`[::1]`）；其他地址必须使用 HTTPS。API 地址不得包含用户信息、查询参数或片段，程序在基础路径后追加 `images/generations`。

跨 CLI 复用时，内容文件建议省略 `model`、`request_id` 和 `execution.output_dir`，由主程序每次显式传目标模型、新请求 ID 和输出目录。模型按 `--model` → 文件 `model` → 默认 `gpt-image-1` 选择；覆盖后不支持的模型或任务直接失败，不丢弃或静默回退。跨 CLI 复用仍受目标任务和参数能力限制。

统一附件结构会先校验再过滤；当前文生图任务不使用附件，响应按原索引记录 `unsupported_attachment_role`，不读取媒体内容。需要参考图片参与生成时应使用实现了 `image-edit` 的技能。

使用 `--try-run` 时只读取并校验请求，不联网、不读取媒体、不创建输出目录；核对顶层 `model`、`effective_parameters.model` 和 `request_preview.model` 是否为本次目标模型。预检 JSON 会包含提示词，须按敏感内容处理。

请求文件格式及 Provider 参数详见 [协议说明](references/protocol.md)。
