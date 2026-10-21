# OpenAI Image CLI

技能名为 `skill-openai-image`，当前支持同步文生图。统一契约以设计方案为准，使用说明和已实现范围见 `skill/`。

| 目录 / 文件 | 职责 |
| --- | --- |
| `Program.cs` | 组合依赖、标准输入输出和进程入口。 |
| `Cli/` | 命令分派、错误出口、生成编排与试运行响应。 |
| `Requests/` | OpenAI 图片请求校验、文件与 argv 合并、有效参数及输入回显。 |
| `Provider/` | OpenAI REST 请求映射、认证、调用和错误规范化；JSON 使用源生成上下文。 |
| `Metadata/` | OpenAI 模型、能力和参数定义。 |
| `skill/` | 技能本身、引用文件及调用脚本。 |

通过 `ProjectReference` 依赖 `../Madorin.AI.Multimedia.Core/`；公共库不反向依赖本项目。取消、总超时、路径授权、JSON 读取、公共响应和产物生命周期在公共库中实现。调用级状态由 `SkillInvocation` 持有，禁止重新引入静态可变调用状态或将业务逻辑集中到入口。

`publish.ps1 -Rid <RID>` 沿用多媒体统一发布流程；公共库随 CLI 发布，不生成单独技能包。此拆分不修改现有 Runtime、根解决方案或公共构建配置。
