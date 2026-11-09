---
name: skill-bfl-image
description: 通过 Black Forest Labs FLUX.2 Pro 生成图片。
---

# BFL 图片生成

当前实现 FLUX.2 Pro 文生图。支持 `metadata`、`list-models`、`version`、`generate`。BFL 提交接口为异步协议，但本技能只在一次 `generate` 调用内部轮询并下载结果；不声明公开异步，不提供 `query` 或 `download`。

## 调用

```text
skill-bfl-image metadata --request-id <id>
skill-bfl-image list-models --request-id <id>  # 当前无通用目录端点，返回 provider_error
skill-bfl-image version --request-id <id>
skill-bfl-image generate --request-id <id> --prompt <text> --api-key <key> --output-dir <dir> [--model flux-2-pro]
```

连接地址缺省 `https://api.bfl.ai`。API Key 仅通过 `--api-key` 输入，并以 `x-key` 请求头发送；不写入请求文件或日志。生成使用官方返回的原始 `polling_url`，不按任务 ID 拼接轮询地址。成功结果图片 URL 仅接受 HTTPS `delivery.*.bfl.ai`，下载后完整验证并写入本地产物。官方临时结果 URL 约 10 分钟过期。

支持 `width` 与 `height` 成对指定、`seed`、`safety_tolerance`（0 至 5）、`output_format`（jpeg/png/webp）和 Provider 参数 `disable_pup`。本批仅支持文生图，不接受参考图片附件。try-run 不联网、不读取媒体、不创建目录或写文件。

请求结构见 [request.schema.json](references/request.schema.json)，协议映射及来源见 [protocol.md](references/protocol.md)。

