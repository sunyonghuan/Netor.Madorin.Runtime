# 智谱 CogVideoX-3 视频协议

核对日期：2026-11-10。

- https://docs.bigmodel.cn/cn/guide/models/video-generation/cogvideox-3.md
- https://docs.bigmodel.cn/api-reference/模型-api/视频生成异步.md
- https://docs.bigmodel.cn/api-reference/模型-api/查询异步结果.md
- https://docs.bigmodel.cn/cn/api/api-code.md

## 映射

| 项目 | 实现 |
| --- | --- |
| 提交 | `POST /paas/v4/videos/generations` |
| 查询 | `GET /paas/v4/async-result/{id}`，这是官方查询路径，不是猜测的轮询 URL |
| 认证 | Bearer，只来自 argv |
| 默认模型 | `cogvideox-3` |
| 时长/帧率 | 5 或 10 秒，30 或 60 帧；缺省分别发送 5 和 30 |
| 尺寸 | 仅 CogVideoX-3 枚举；缺省不发送 |
| 单图 | HTTPS URL 原样提交；本地文件正式执行时编码为 data URI，不超过 5 MiB |
| 状态 | `PROCESSING`、`SUCCESS`、`FAIL` |
| 产物 | 只下载 `video_result` 中唯一的 HTTPS URL，并用 ffprobe/ffmpeg 验证 MP4 |
| 异步 | `supports_async=true`；上下文不含地址、密钥和提示词 |
| 取消 | 官方无取消端点，`remote_cancellation=unsupported` |

参考文档中的同步 `data[].url`、`resolution=720p` 和 `cogvideox-text-to-video` 已失效。首尾帧官方支持，但本批拒绝 `first_frame`/`last_frame`。

官方未公布结果主机白名单。下载不把 URL 列为产物，只接受无用户信息的 HTTPS，并限制手动重定向。
