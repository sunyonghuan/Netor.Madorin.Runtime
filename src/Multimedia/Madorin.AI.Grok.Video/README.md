# Grok Video CLI

技能名为 `skill-grok-video`，当前实现 xAI Grok Imagine 文生视频。统一契约以设计方案为准，字段见 `skill/`。

| 目录 / 文件 | 职责 |
| --- | --- |
| `Program.cs` | 组合依赖、标准输入输出和进程入口。 |
| `Cli/` | 命令分派、提交后等待、查询、下载和 try-run。 |
| `Requests/` | 请求文件与 argv 合并、模型选择、校验和输入回显。 |
| `Provider/` | xAI 视频提交、状态查询、MP4 下载和错误归一。 |
| `Metadata/` | 任务、参数、异步上下文和 JSON Schema 声明。 |
| `skill/` | `SKILL.md`、协议引用、请求 Schema 和脚本资源。 |

项目通过 `ProjectReference` 依赖 `Madorin.AI.Multimedia.Core`。不修改宿主 Runtime 或根解决方案。取消、总预算、路径授权、JSON 读取和公共响应复用公共库。视频产物由公共 `VideoArtifactStore` 落盘，并用 ffprobe、ffmpeg 校验。

使用 `publish.ps1 -Rid <RID>` 进入统一多媒体发布流程。

## 模型目录与协议能力

模型目录通过带连接认证参数的 list-models 从目标 API 获取。metadata 离线描述 `text-to-video`，不提供静态模型白名单。生成只检查模型 ID 的非空和控制字符，原样传递。默认模型是 `grok-imagine-video-1.5`。默认时长 8 秒，比例 `16:9`，分辨率 `480p`。

视频接口是异步的。`supports_async=true`，`task_context_required=true`。普通 generate 在同一进程内轮询并下载；`--no-wait` 返回任务 ID 与完整上下文。宿主保存响应的 `task_context` 对象，query/download 通过 `--task-context-file` 恢复原模型和槽位；远端省略模型时仍返回完整上下文，模型不一致时报 Provider 错误。xAI 取消端点未接入；本机取消和超时只停止本地操作，响应标记 `remote_cancellation=unsupported`，不声称撤销远端生成。本批不实现图生视频和参考图。

调用总预算默认 300 秒；JSON 响应上限 4 MiB。MP4 文件名按可用序号递增，产物槽位 index 始终为 1。工具链和产物验证属于正式执行阶段，try-run 不读媒体、不联网、不写文件，但仍需 api-key 占位值。
