# Multimedia

图片、视频生成技能 CLI 的源码工作目录。后续相关开发默认在本目录内进行，遵守仓库根目录的 `agents.md`。

| 项目 | 约定 |
| --- | --- |
| 工作范围 | 图片生成、视频生成、相关资源文档的读取与校验，以及请求、轮询、错误和产物处理。 |
| 源码位置 | 新增多媒体项目放在本目录下，按实际任务创建，不预建空壳项目。 |
| 项目命名 | 以技能 CLI 方案为准，技能项目使用 `Madorin.AI.<Provider>.<Capability>`；能力段为 `Image`、`Video` 或 `ImageVideo`，不额外插入 `Skills` 段。 |
| 职责边界 | CLI 入口负责命令与输出；技能内部的 Provider 实现负责供应商协议；共享契约不包含供应商私有字段。 |
| 请求输入 | 生成支持统一 JSON 请求文件并保留直接参数；API 地址与认证材料只直接传参，不写入文件，不采用 AES 或连接文件。异步查询、下载使用任务 ID 等少量直接参数。 |
| 请求关联 | 主程序提供调用 `request_id`，响应顶层 `response_id` 原样相同；与 Provider 任务 ID 分开。缺失、冲突及失败响应的边界按方案第 3.0 节。 |
| 参数转换 | 公共和特殊参数优先按已声明规则做等价转换，响应记录 `converted_parameters`；无法应用的剩余项才丢弃并记录 `ignored_parameters`。非法源值、目标冲突及协议/控制错误仍失败。 |
| 测试 | 使用仓库 `tests/` 下对应项目、临时目录和假 Provider，默认不依赖外网。 |
| 目录外改动 | 现有 Runtime、Provider、解决方案和公共构建配置默认不动；明确要求集成时仅做必要的最小修改。 |
| 禁止内容 | 不存放密钥、账号配置、真实生成媒体、调试产物或本机绝对路径；不顺带开展音频或其他业务。 |

命名、命令、参数与 JSON 字段定义见[技能 CLI 设计方案](../../docs/图标视频/策划/技能CLI设计方案.md)。当前尚无可发布的多媒体项目；发布基础设施不代表生成能力已实现。

## 发布入口

统一入口为本目录的 `publish.ps1`，Windows 可用 `publish.cmd`，Linux/macOS 可用 `bash publish.sh`。脚本支持 Windows PowerShell 5.1 或跨平台 PowerShell 7；Unix 包装器依赖 `pwsh`。

| 操作 | 在本目录执行 | 含义 |
| --- | --- | --- |
| 列出项目 | `./publish.ps1 list` | 列出登记的项目名、技能名、支持的 RID 和单项发布命令；不执行发布。 |
| 发布单项 | `./publish.ps1 publish <项目名或技能名> -Rid win-x64` | 调用该项目自带的发布脚本，完成构建与技能打包。 |
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
| 执行脚本 | 默认提供 `scripts/invoke.ps1` 与 `scripts/invoke.sh`，根据宿主定位 `runtime/<RID>/` 中的 CLI，并透传 argv 和退出码。 |
| 最终位置 | 仅发布到仓库根 `skills/<skill_name>/`，不覆盖其他技能；同名非本流程管理的技能包拒绝覆盖。 |
| 多平台 | 按 RID 分目录；同版本才可合并并保留其他 RID。后续须按方案第 7 节核对发布记录中的技能版本、契约版本、源码修订及共享文件摘要；不同版本或记录缺失时拒绝覆盖，新版本暂存完整包后整体替换。此版本校验是待实现契约，不代表当前脚本已经支持。 |
| Native AOT | 以 Release、self-contained、Native AOT 发布；使用仓库 `PublishAotCross.Tools` 工具链。RID 按目标平台选择，实际交叉编译能力取决于 .NET SDK、C/C++/原生工具链和项目依赖，不受支持的目标必须在替代平台上构建。 |
| 安全替换 | 在本次独立暂存目录完成构建和结构校验后再替换目标；失败保留原包，禁止清空整个 `skills/`。同名并行发布由独占文件锁拒绝；全量发布逐项生效，失败立即停止且已成功发布的前序项目保留。 |
| 敏感信息 | 不打包密钥、账号配置、绝对路径、真实生成媒体或调试文件；生成产物仍写入调用方指定输出目录。 |
| 远程操作 | 发布仅指本地技能打包，不上传、不推送、不安装到全局技能目录。 |

## 发布脚本验证

当前仍为策划阶段，尚无真实多媒体项目，不要求此时运行发布脚本或验证其可执行性。后续项目落地后再按需要执行 `./tests/publishing.tests.ps1`：使用临时目录、模拟仓库和假构建器验证清单、平台、单项/全量调度、技能结构、同版本多 RID 合并和失败保护；不把模拟检查视为真实 Native AOT 或各操作系统实机发布通过。
