---
name: skill-aliyun-image
description: 通过阿里云百炼 Wan 2.6 文生图 API 生成 PNG 图片。
---

# 阿里云百炼文生图

当前实现同步 `text-to-image`，默认模型为 `wan2.6-t2i`，支持 metadata、list-models、version 和 generate。不支持旧版 Wan 异步任务、Wan 2.6 异步恢复、图像编辑、图文混排和附件。

## 调用

```text
skill-aliyun-image metadata --request-id <id>
skill-aliyun-image list-models --request-id <id> --api-url <base-url> --api-key <key>
skill-aliyun-image version --request-id <id>
skill-aliyun-image generate --request-id <id> --api-key <key> --prompt <text> --output-dir <dir>
```

API 根地址默认 `https://dashscope.aliyuncs.com/api/v1`，同步生成调用 `services/aigc/multimodal-generation/generation`。调用方可传地域业务空间专属 URL；API Key 只通过 `--api-key` 提供，不写入请求文件、预览或日志。

支持 `--size`、`--num-images`、`--negative-prompt`，以及 `--provider-aliyun-prompt-extend`、`--provider-aliyun-watermark`、`--provider-aliyun-seed`。`num_images` 范围 1 至 4；size 为 `宽*高`。未声明等价转换的比例/宽高参数不会猜测转换。

正式生成会下载服务返回的临时 HTTPS PNG URL，完整验证并落盘后才返回产物。URL 约 24 小时失效。try-run 不联网、不下载图片、不创建目录或文件。

本批只支持 Wan 2.6 同步文生图；Wan 2.5 及以下模型仅支持异步协议，不能通过当前技能调用。

请求文件格式见 [request.schema.json](references/request.schema.json)，协议字段见 [protocol.md](references/protocol.md)。
