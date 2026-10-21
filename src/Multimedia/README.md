# Multimedia

图片、视频生成技能 CLI 的源码工作目录。后续相关开发默认在本目录内进行。专项实施和最终验收以[技能 CLI 设计方案](../../docs/图标视频/策划/技能CLI设计方案.md)为准；根 agents.md 仅为临时工作约束，不作为最终验收或协议核对标准。

| 项目 | 约定 |
| --- | --- |
| 工作范围 | 图片生成、视频生成、相关资源文档的读取与校验，以及请求、轮询、错误和产物处理。 |
| 源码位置 | 新增多媒体项目放在本目录下，按实际任务创建，不预建空壳项目。 |
| 项目命名 | 以技能 CLI 方案为准，技能项目使用 `Madorin.AI.<Provider>.<Capability>`；能力段为 `Image`、`Video` 或 `ImageVideo`，不额外插入 `Skills` 段。 |
| 职责边界 | CLI 入口负责命令与输出；技能内部的 Provider 实现负责供应商协议；共享契约不包含供应商私有字段。 |
| 请求输入 | 生成支持统一 JSON 文件并保留直接参数；API 基础地址与认证只 argv，不采用 AES 或连接文件。查询/下载按声明可需 --task-context-file，保存任务恢复状态而非生成内容。 |
| 模型选择 | 主程序跨 CLI 生成显式传 `--model`；内容文件建议省略模型、调用 ID 和输出目录。模型按 argv → 文件 → 声明默认 → 唯一候选选择，最终模型不支持则失败，不丢弃或静默回退。 |
| 请求关联 | response_id 原样等于有效 request_id；远端 task_id 与 provider_metadata.request_id 单次追踪分开。成功/失败中的任务与完整上下文按方案第 3 节保留。 |
| 异步与控制 | 公开异步须能跨进程恢复；BFL 原 polling_url 仅在 task_context/受限任务文件往返。stdin 取消帧、命令识别起点总预算和最多 5 秒清理按方案第 2.10、2.11、5 节。 |
| 参数转换 | 公共和特殊参数优先按已声明规则做等价转换，响应记录 `converted_parameters`；无法应用的剩余项才丢弃并记录 `ignored_parameters`。非法源值、目标冲突及协议/控制错误仍失败。 |
| 测试 | 使用仓库 `tests/` 下对应项目、临时目录和假 Provider，默认不依赖外网。 |
| 目录外改动 | 现有 Runtime、Provider、解决方案和公共构建配置默认不动；明确要求集成时仅做必要的最小修改。 |
| 禁止内容 | 不存放密钥、账号配置、真实生成媒体、调试产物或本机绝对路径；不顺带开展音频或其他业务。 |

命名、命令、参数与 JSON 字段定义见[技能 CLI 设计方案](../../docs/图标视频/策划/技能CLI设计方案.md)。当前已有 `Madorin.AI.OpenAI.Image` 文生图初版及 `Madorin.AI.Multimedia.Core` 公共类库；构建通过不代表真实 API 或跨平台发布已验证。

## 项目依赖

| 项目 | 职责 | 依赖 |
| --- | --- | --- |
| `Madorin.AI.Multimedia.Core` | 通用命令解析、协议辅助、单次调用生命周期、文件安全和产物处理。 | .NET 基础库及 MIT 许可的 SkiaSharp 图片解码；不包含厂商模型、端点或私有参数。 |
| `Madorin.AI.OpenAI.Image` | OpenAI 图片 CLI、模型声明、请求映射和 REST 调用。 | 通过项目引用使用公共类库；自带技能资源和发布脚本。 |

后续技能复用公共库，不复制基础设施。公共类库随对应 CLI 编译与打包，不创建独立 `publish.json`，不纳入单独发布清单。

## 发布入口

统一入口为本目录的 `publish.ps1`，Windows 可用 `publish.cmd`，Linux/macOS 可用 `bash publish.sh`。脚本支持 Windows PowerShell 5.1 或跨平台 PowerShell 7；Unix 包装器依赖 `pwsh`。

| 操作 | 在本目录执行 | 含义 |
| --- | --- | --- |
| 列出项目 | `./publish.ps1 list` | 列出登记的项目名、技能名、支持的 RID 和单项发布命令；不执行发布。 |
| 发布单项 | `./publish.ps1 publish <项目名或技能名> -Rid win-x64` | 调用该项目自带的发布脚本，完成构建与技能打包。 |
| 多 RID 暂存 | `./publish.ps1 publish <技能名> -Rid win-x64,linux-x64` | 一次构建所选 RID 后整体替换；Bash/cmd 包装器也接受逗号分隔值。任何一个构建失败均保留旧包。 |
| 发布全部 | `./publish.ps1 all -Rid win-x64` | 发布全部已登记项目；任一项目不支持所选 RID 时，在开始构建前失败。 |
| 查看平台 | `./publish.ps1 platforms` | 列出可选 RID 及当前宿主；未指定 `-Rid` 时使用当前宿主 RID。 |
| 查看帮助 | `./publish.ps1 help` | 显示命令和参数。 |

Windows 示例：`publish.cmd publish skill-example-image -Rid win-x64`。Linux 示例：`bash publish.sh all -Rid linux-x64`。每个项目还必须可单独执行 `pwsh ./<项目目录>/publish.ps1 -Rid <RID>`。

## 项目登记

只发现本目录直接子目录中的 `publish.json`，不扫描模板或 `bin/obj`。共享类库不单独发布技能包；每个可独立发布的 CLI 项目必须自带 `publish.json`、`publish.ps1` 和 `skill/`。从 `templates/project/` 创建项目时，先将 `publish.json.example` 重命名为 `publish.json`，将 `SKILL.md.template` 重命名为 `SKILL.md`，替换示例技能名和占位说明，并检查实际 `scripts/` 文件；模板不是实际项目，不计入 `list`。

| `publish.json` 字段 | 类型 | 含义 |
| --- | --- | --- |
| `schema_version` | 整数，固定 `1` | 发布清单版本，未知字段或版本拒绝处理。 |
| `skill_name` | 字符串 | `skill-<provider>-<capability>`；必须与 `SKILL.md` 的 YAML `name`、发布文件夹名一致。 |
| `project` | 相对路径 | 本项目目录内的 `.csproj`，不得越界或使用绝对路径。 |
| `executable` | 字符串 | CLI 无扩展名文件名；必须与技能名及项目根级、无条件 `PropertyGroup` 的 `AssemblyName` 一致，避免 CLI 文件名误匹配或全局 MSBuild 属性影响依赖项目。 |
| `platforms` | 字符串数组 | 本项目支持的 RID；从 `win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64` 中选择。 |

CLI 项目还须在根级无条件 `PropertyGroup` 中声明 `Version`（`x.y.z`）和 `SkillContractVersion`（`x.y`），分别与 CLI 技能版本、公共契约版本一致。发布要求相关源码、技能源文件和公共构建输入已提交；脚本不会自动提交。未提交的工作目录可构建和测试，不能生成可合并的正式技能包。

## 发布目录契约

```text
src/Multimedia/<项目名>/
  publish.json
  publish.ps1
  <项目名>.csproj
  skill/
    SKILL.md
    references/
    scripts/

skills/<skill_name>/
  SKILL.md
  references/
  scripts/
    invoke.ps1
    invoke.sh
  runtime/
    <RID>/
      <executable>[.exe]
      <其他必需运行文件>
```

| 发布规则 | 要求 |
| --- | --- |
| 技能包 | 必须同时包含 `SKILL.md`、至少一份 `references/` 引用文档、至少一个 `scripts/` 脚本和 CLI；不接受仅发布二进制。 |
| CLI 文件 | `dotnet publish` 的运行文件全部放在 `runtime/<RID>/`；不能散落在技能根目录或脚本目录。 |
| 执行脚本 | 默认提供 invoke.ps1、invoke.sh，定位 runtime/RID 中 CLI，透传 argv、stdin/stdout/stderr 和退出码，保持取消通道；Windows 参数与退出码、Bash 语法已有定向验证，其他平台待实机验证。 |
| 最终位置 | 仅发布到仓库根 `skills/<skill_name>/`，不覆盖其他技能；同名非本流程管理的技能包拒绝覆盖。 |
| 多平台 | 按 RID 分目录；发布记录包含技能版本、契约版本、完整 Git 修订、共享文件 SHA-256 和实际 RID。同版本全部一致才保留其他 RID，记录缺失或不符则拒绝；升级版本仅收齐本次选择的 RID，不继承旧二进制。 |
| Native AOT | 以 Release、self-contained、Native AOT 发布；使用仓库 `PublishAotCross.Tools` 工具链。RID 按目标平台选择，实际交叉编译能力取决于 .NET SDK、C/C++/原生工具链和项目依赖，不受支持的目标必须在替代平台上构建。 |
| 安全替换 | 在本次独立暂存目录完成构建和结构校验后再替换目标；失败保留原包，禁止清空整个 `skills/`。同名并行发布由独占文件锁拒绝；全量发布逐项生效，失败立即停止且已成功发布的前序项目保留。 |
| 敏感信息 | 不打包密钥、账号配置、绝对路径、真实生成媒体或调试文件；生成产物仍写入调用方指定输出目录。 |
| 远程操作 | 发布仅指本地技能打包，不上传、不推送、不安装到全局技能目录。 |

## 发布脚本验证

发布脚本验证与代码构建分别记录。`./tests/publishing.tests.ps1` 使用临时目录、假 Git 状态和假构建器验证清单、平台、单项/全量调度、技能结构、版本与摘要、同版本多 RID 合并、升级及失败保护；通过结果见本轮修复执行记录。它不代表真实 Native AOT 或 Linux/macOS 实机发布通过。
