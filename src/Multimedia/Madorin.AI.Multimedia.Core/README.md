# Multimedia Core

图片、视频技能 CLI 共用的基础能力类库，随具体 CLI 发布，不登记独立技能或发布清单。

| 目录 | 职责 |
| --- | --- |
| `Protocol/` | 契约版本、关联 ID、模型 ID 结构校验、远端目录规范化、JSON 工具、错误与响应封装。 |
| `CommandLine/` | 与 Provider 无关的 argv 解析和模型查询连接/预算参数；允许选项由具体技能传入。 |
| `Execution/` | 单次调用状态、超时预算、stdin 协作取消及资源释放。 |
| `Files/` | 授权根路径校验、严格 JSON 文件读取、图片/视频完整性验证、媒体工具进程和产物落盘。 |

本库不依赖任何 Provider，不包含 OpenAI 模型清单、字段映射或认证细节。各 CLI 通过 `ProjectReference` 使用；不改动现有 Runtime 的冻结契约。

图片校验使用 MIT 许可的 SkiaSharp 4.153.1，包含 Windows、macOS 及 Linux 的原生运行依赖；发布时必须保留对应 RID 的原生库。校验检查容器完整性、实际格式并执行完整像素解码，失败不创建产物。取消会停止等待解码，后台原生解码仅释放内存，不写文件。

视频校验要求目标技能包将经许可核对的 `ffmpeg`、`ffprobe` 和许可证放入对应 RID 工具目录；Release 不从系统 PATH 回退。媒体子进程取消时先终止进程，再限时回收退出和管道读取。
