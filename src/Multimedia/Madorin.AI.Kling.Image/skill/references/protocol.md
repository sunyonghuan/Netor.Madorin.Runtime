# 可灵图片协议引用

核对日期：2026-11-10。来源：[可灵 AI 开放平台](https://klingai.com/document-api) 的图像生成与错误码页面。

- 根地址默认 `https://api-beijing.klingai.com`。认证为 `Authorization: Bearer <API Key>`。
- 创建：`POST /v1/images/generations`。字段为 `model_name`、`prompt`、`negative_prompt`、`resolution`、`n`、`aspect_ratio`、`watermark_info.enabled`。
- 查询：`GET /v1/images/generations/{task_id}`。状态为 `submitted`、`processing`、`succeed`、`failed`。
- 成功结果取 `task_result.images[].url`。`index` 从 0 起，映射到本地槽位 1 起。数量必须与请求的 `n` 一致。
- 没有通用模型目录端点，也没有取消端点。任务上下文不保存地址、密钥或提示词。
- 结果 URL 约 30 天有效。官方未公布主机白名单。下载只接受无用户信息的 HTTPS，最多跟随 3 次重定向，并校验 PNG 或 JPEG。
