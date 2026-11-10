---
name: skill-zhipu-video
description: 通过智谱 CogVideoX-3 生成视频。
---

# 智谱视频生成

当前实现 CogVideoX-3 文生视频和单图生视频。支持 `metadata`、`list-models`、`version`、`generate`、`query`、`download`。

## 调用

```text
skill-zhipu-video generate --request-id <id> --prompt <text> --api-key <key> --output-dir <dir>
skill-zhipu-video generate --request-id <id> --task image-to-video --reference-image <path> --api-key <key> --output-dir <dir>
skill-zhipu-video query --request-id <id> --task-id <task> --task-context-file <file> --api-key <key>
skill-zhipu-video download --request-id <id> --task-id <task> --task-context-file <file> --api-key <key> --output-dir <dir>
```

连接地址缺省 `https://open.bigmodel.cn/api`。API Key 只通过 `--api-key` 传递。默认时长 5 秒、帧率 30。`--no-wait` 只提交并返回任务上下文，不下载。

查询使用官方 `GET /paas/v4/async-result/{id}`。任务上下文不包含 API 地址、密钥或提示词，因此后续命令必须再次提供连接参数。本批不支持首尾帧。try-run 不读本地图片、不联网、不写文件。

`list-models` 因没有官方通用目录端点而返回 `provider_error`。

字段依据见 [protocol.md](references/protocol.md)。
