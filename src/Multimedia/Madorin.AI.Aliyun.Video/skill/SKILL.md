---
name: skill-aliyun-video
description: 通过阿里云百炼万相 2.7 生成带自动配音的视频。
---

# 阿里云万相视频

当前实现万相 2.7 文生视频和单首帧图生视频。支持 `metadata`、`list-models`、`version`、`generate`、`query`、`download`。

## 调用

```text
skill-aliyun-video generate --request-id <id> --prompt <text> --api-key <key> --output-dir <dir>
skill-aliyun-video generate --request-id <id> --task image-to-video --first-frame-url <https-url> --api-key <key> --output-dir <dir>
skill-aliyun-video query --request-id <id> --task-id <task> --task-context-file <file> --api-key <key>
skill-aliyun-video download --request-id <id> --task-id <task> --task-context-file <file> --api-key <key> --output-dir <dir>
```

连接地址缺省 `https://dashscope.aliyuncs.com/api/v1`。API Key、地域和地址必须匹配，只通过 argv 传递。不传音频时模型自动配音。`--no-wait` 只提交并返回任务上下文，不下载。

查询使用官方 `GET /api/v1/tasks/{task_id}`。任务上下文不包含 API 地址、密钥、提示词或结果 URL。本批不支持首尾帧、视频续写和旧版异步协议。try-run 不读本地图片、不联网、不写文件。

`list-models` 因官方视频 API 参考没有通用目录端点而返回 `provider_error`。

字段依据见 [protocol.md](references/protocol.md)。
