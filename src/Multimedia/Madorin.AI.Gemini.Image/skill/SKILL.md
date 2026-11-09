---
name: skill-gemini-image
description: 通过 Google Gemini 原生图片生成 API 生成图片，支持文生图及受支持的图片宽高比。
---

# Gemini 图片生成

当前实现同步 `text-to-image`，支持 metadata、list-models、version、generate；不支持图片编辑、参考附件、异步查询或下载。

## 调用

```text
skill-gemini-image metadata --request-id <id>
skill-gemini-image list-models --request-id <id> --api-url <url> --api-key <key>
skill-gemini-image version --request-id <id>
skill-gemini-image generate --request-file <json> --output-dir <dir> --api-key <key> --model gemini-2.0-flash-exp --request-id <id>
```

也可直接传 `--prompt <text>`；支持 `--aspect-ratio`，当前每次调用生成一个图片槽位。API 根地址缺省为 `https://generativelanguage.googleapis.com/v1beta`，程序追加 `models/{model}:generateContent`。API Key 仅作为 argv 参数传递，不写入请求文件或输出。

模型按 `--model`、文件 `model`、默认 `gemini-2.0-flash-exp` 顺序选择。该默认值仅供省略模型的旧调用兼容；实际模型目录由本次 API 返回，不维护静态模型清单。

请求使用 `generationConfig.responseModalities=[TEXT, IMAGE]`，并在显式比例时映射 `generationConfig.imageConfig.aspectRatio`，响应 `candidates[].content.parts[].inlineData` 中的 JPEG/PNG/WebP 数据经公共解码验证后落盘；URL 响应不是本地已验证产物，不接受为成功结果。附件当前仅按公共契约校验并记录忽略。

使用 `--try-run` 时不联网、不读取媒体、不创建输出目录。预览会包含提示词，按敏感内容处理。

请求文件格式见 [request.schema.json](references/request.schema.json)，协议映射见 [protocol.md](references/protocol.md)。

## 动态模型目录

metadata 为离线协议能力：models=[]，tasks 描述已实现任务和参数，model_discovery 描述目录入口。list-models 使用本次 --api-url、--api-key 与 --timeout 调用 GET models，保留实际 ID；缺少远端任务信息时 tasks=null。查询失败如实报错，空目录返回 []，不回退内置清单。

--model 支持服务实际 ID、部署名或别名，不强制先查目录；try-run 保持离线，不承诺远端模型可用或支持全部参数。本地协议参数约束继续生效，模型专属能力未知时由服务确认。



