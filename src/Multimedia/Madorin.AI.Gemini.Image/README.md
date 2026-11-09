# Gemini Image CLI

技能名为 `skill-gemini-image`，当前实现 Google Gemini 原生图片生成 同步文生图。统一契约以设计方案为准，具体能力和字段见 `skill/`。

| 目录 / 文件 | 职责 |
| --- | --- |
| `Program.cs` | 组合依赖、标准输入输出和进程入口。 |
| `Cli/` | 命令分派、错误出口、生成编排和 try-run 响应。 |
| `Requests/` | 请求文件与 argv 合并、模型选择、校验、转换和输入回显。 |
| `Provider/` | Google Gemini REST 请求映射、x-goog-api-key 认证、inlineData 图片响应解析和错误归一。 |
| `Metadata/` | 模型、能力、参数和 JSON Schema 声明。 |
| `skill/` | `SKILL.md`、协议引用、请求 Schema 和脚本资源。 |

项目通过 `ProjectReference` 依赖 `Madorin.AI.Multimedia.Core`；不修改现有 Runtime 或根解决方案。取消、总预算、路径授权、JSON 读取、公共响应和产物生命周期复用公共库。

使用 `publish.ps1 -Rid <RID>` 进入统一多媒体发布流程；公共库随 CLI 发布，不生成独立技能包。

## 模型目录与协议能力

模型目录通过带连接认证参数的 list-models 从目标 API 获取；metadata 离线描述协议 tasks，不提供静态模型白名单。生成仅检查模型 ID 的非空和控制字符，原样传递；目录缺项或查询不可用不阻止提交。try-run 不联网校验模型可用性。具体字段以设计方案第 2.2.1、3.1、3.2 节为准。


