# Madorin.AI.Runtime

基于 .NET 10 的独立 AI CLI 与运行时仓库，提供命令行调用、宿主集成、Provider 接入及会话与数据管理。仓库同时包含独立的图片生成技能 CLI；后续图片、视频协议开发统一在 `src/Multimedia/` 中进行。

| 组成 | 定位 | 入口 |
| --- | --- | --- |
| Runtime CLI | 交互与单次任务、常驻服务、配置、Agent、会话、技能和数据运维 | `src/Madorin.AI.Runtime.Cli/`，可执行文件名 `madorin-cli` |
| Client SDK 与 Runtime | 宿主通信、任务编排、Provider 调用、持久化与工具执行 | `src/Madorin.AI.Runtime.Client/` 及其他 Runtime 项目 |
| 多媒体技能 CLI | 按统一协议独立执行图片、视频生成任务；当前已实现 OpenAI、Grok 同步文生图 | [Multimedia 开发入口](src/Multimedia/README.md) |

## 文档入口

| 目标 | 文档 |
| --- | --- |
| 查阅 Runtime 文档导航 | [文档总览](docs/README.md) |
| 安装、配置与首次使用 | [安装与初始化](docs/开发文档/02-安装与初始化.md)、[命令行与交互使用](docs/开发文档/03-命令行与交互使用.md) |
| 查询 Runtime 命令与参数 | [CLI 命令矩阵](docs/命令规范/09-CLI命令矩阵.md)及实际程序的 `--help` |
| 接入宿主程序 | [宿主集成指南](docs/开发文档/04-宿主集成指南.md) |
| 查询图片、视频统一协议 | [技能 CLI 设计方案](docs/图标视频/策划/技能CLI设计方案.md) |
| 查看多媒体实施任务与实际证据 | [执行总览](docs/图标视频/执行/README.md)、[Grok 实施记录](docs/图标视频/执行/执行记录(Grok图片CLI).md) |
| 开发新协议与技能发布 | [Multimedia README](src/Multimedia/README.md)、[公共类库 README](src/Multimedia/Madorin.AI.Multimedia.Core/README.md) |
| 查看当前工作树开发规则 | [agents.md](agents.md) |

多媒体专项的协议定义和最终验收以设计方案为准。`agents.md` 是临时开发约束，不作为最终验收标准；执行记录用于说明某一批次实际完成和验证的范围。Runtime 的文档优先级见文档总览。

## 仓库结构

| 路径 | 内容 |
| --- | --- |
| `src/Madorin.AI.Runtime.Cli/` | 主 CLI 入口、命令与配置。 |
| `src/Madorin.AI.Runtime.Client/`、`src/Madorin.AI.Runtime.Contracts/` | 宿主 SDK 与公共通信契约。 |
| `src/Madorin.AI.Runtime.*/` | 核心能力、实体、服务、编排、工具、持久化与通信；数据由 SQLite、消息 JSONL 与 Blob 等组件管理。 |
| `src/Providers/` | Runtime 的 Provider 实现与独立 Worker。 |
| `src/Multimedia/` | 多媒体公共库、独立技能项目、发布脚本及项目模板。 |
| `tests/` | 单元、协议、宿主集成及端到端等测试；多媒体测试位于 `Madorin.AI.Multimedia.Tests/`。 |
| `examples/`、`tests/samples/` | 调用示例与参考宿主。 |
| `tools/` | 辅助开发工具。 |
| `docs/` | 设计、开发、使用、实施与验证文档。 |
| `build/`、`publish/` | 主 CLI 发布脚本与 AOT 裁剪配置；`publish/` 不是多媒体技能交付目录。 |
| `skills/` | 技能目录；现有 `madorin-cli/` 为主 CLI 技能资源，多媒体发布包按技能名生成子目录。 |
| `Madorin.AI.Runtime.slnx` | Runtime 解决方案；当前未收录多媒体项目，多媒体项目单独构建。 |

## 开发环境

| 项目 | 要求 |
| --- | --- |
| .NET SDK | `global.json` 指定 `10.0.301`，允许按 `latestFeature` 前滚，不使用预览版。 |
| 语言与框架 | C# 14、`net10.0`、可空引用类型、警告视为错误。 |
| 依赖管理 | 使用仓库 `NuGet.config` 和 `Directory.Packages.props`。 |
| 发布脚本 | 多媒体支持 Windows PowerShell 5.1 或 PowerShell 7；Linux/macOS 包装器需要 `pwsh`。 |
| Native AOT | 需要目标平台对应的原生工具链和依赖；配置了目标 RID 不代表已通过交叉编译或平台运行验证。 |

以下命令均从仓库根目录执行。源码开发可直接构建目标项目，不必先发布技能包。

## Runtime CLI 快速开始

```powershell
dotnet build src/Madorin.AI.Runtime.Cli/Madorin.AI.Runtime.Cli.csproj
dotnet run --project src/Madorin.AI.Runtime.Cli --no-build -- --help
dotnet run --project src/Madorin.AI.Runtime.Cli --no-build -- version --json
```

需要配置真实 Provider 时，运行初始化向导，再校验配置：

```powershell
dotnet run --project src/Madorin.AI.Runtime.Cli --no-build -- config init
dotnet run --project src/Madorin.AI.Runtime.Cli --no-build -- config validate
```

完整使用流程见安装与初始化文档。文档中的 `madorin` 是命令示例名；发布的二进制名为 `madorin-cli`，可直接使用该名称或按安装说明建立别名。主 CLI 的凭证配置方式与下文多媒体技能的 argv 契约分别定义。

## 图片技能快速开始

### 当前能力

| 技能 | 项目 | 模型与已实现任务 |
| --- | --- | --- |
| `skill-openai-image` | [Madorin.AI.OpenAI.Image](src/Multimedia/Madorin.AI.OpenAI.Image/README.md) | 默认 `gpt-image-1`；同步 `text-to-image`，具体模型与参数以 `list-models`、`metadata` 为准。 |
| `skill-grok-image` | [Madorin.AI.Grok.Image](src/Multimedia/Madorin.AI.Grok.Image/README.md) | `grok-imagine-image-2.0`；同步 `text-to-image`，支持图片数量与宽高比。 |

两个技能均提供 `metadata`、`list-models`、`version`、`generate`。当前未实现参考图片参与生成、图像编辑、视频生成或公开异步 `query` / `download`。请求中的附件即使结构合法，也不会参与当前文生图任务。

### 构建与能力查询

```powershell
dotnet build src/Multimedia/Madorin.AI.OpenAI.Image/Madorin.AI.OpenAI.Image.csproj
dotnet build src/Multimedia/Madorin.AI.Grok.Image/Madorin.AI.Grok.Image.csproj
dotnet run --project src/Multimedia/Madorin.AI.Grok.Image --no-build -- metadata --request-id metadata-001
dotnet run --project src/Multimedia/Madorin.AI.Grok.Image --no-build -- list-models --request-id models-001
```

### 请求文件与试运行

在当前工作目录准备 UTF-8 编码的 `request.json`。以下内容可供两个图片技能复用，模型、请求 ID、输出目录和连接参数在每次调用时指定：

```json
{
  "schema_version": "1.0",
  "task": "text-to-image",
  "prompt": "白色背景上的一个红色圆形。",
  "parameters": {
    "num_images": 1
  }
}
```

先用 Grok 技能离线检查参数和中文回显；此处的 Key 是预检占位值：

```powershell
dotnet run --project src/Multimedia/Madorin.AI.Grok.Image --no-build -- generate --request-file ./request.json --output-dir ./artifacts/images --api-url https://api.x.ai/v1 --api-key TRY_RUN_ONLY --model grok-imagine-image-2.0 --request-id preview-001 --try-run
```

`--try-run` 不联网、不读附件媒体、不创建输出目录，不能证明远端认证、模型权限、额度或生成服务正常。它会回显提示词，应按请求内容的敏感程度保存预览结果。

### 正式调用

调用方准备真实凭证后去掉 `--try-run`。以下 PowerShell 示例假设调用环境已提供 `GROK_API_KEY`；CLI 接收的是展开后的 argv 值，不自行读取这个环境变量：

```powershell
dotnet run --project src/Multimedia/Madorin.AI.Grok.Image --no-build -- generate --request-file ./request.json --output-dir ./artifacts/images --api-url https://api.x.ai/v1 --api-key $env:GROK_API_KEY --model grok-imagine-image-2.0 --request-id generate-001
```

| 参数 / 规则 | 说明 |
| --- | --- |
| `--request-file` | 生成请求 JSON 文件路径，承载任务、长提示词、附件结构及生成参数；也可直接使用 `--prompt` 等受支持参数。 |
| `--model` | 跨 CLI 调用时显式指定目标模型；覆盖文件中的模型，不支持的最终模型直接失败。 |
| `--output-dir` | 本地媒体输出目录；只将完整校验且成功落盘的文件列为产物。 |
| `--api-url` | Provider 基础地址；两个技能各有默认地址，程序追加 `images/generations`。HTTPS 为常规要求，本机回环兼容服务允许 HTTP。 |
| `--api-key` | 仅通过 argv 传递，不写入请求文件；调用方不要记录包含凭证的完整命令。 |
| `--request-id` | 调用方分配的关联 ID，所有命令均需提供；顶层 `response_id` 原样相同，不代表远端任务 ID。 |
| 输出 | CLI 的 stdout 仅一个 UTF-8 终态 JSON；正式执行的诊断使用 stderr，调用方同时检查退出码与响应内容。 |
| 路径 | 启动时 cwd 为授权根；请求文件和 argv 路径相对 cwd，文件内路径相对请求文件目录，均不得越过授权边界。 |

同一请求文件切换到 OpenAI 技能时，更换项目入口、`--model`、API 地址与凭证，并为本次调用分配新的请求 ID；参数是否可用仍由目标技能的能力声明决定。详细字段见各技能的 [OpenAI 协议说明](src/Multimedia/Madorin.AI.OpenAI.Image/skill/references/protocol.md)与 [Grok 协议说明](src/Multimedia/Madorin.AI.Grok.Image/skill/references/protocol.md)。

## 发布

### 多媒体技能发布

统一入口为 `src/Multimedia/publish.ps1`。下列命令从仓库根目录运行，`list` 和 `platforms` 仅查询；`publish` 和 `all` 会执行构建与本地技能打包：

```powershell
pwsh ./src/Multimedia/publish.ps1 list
pwsh ./src/Multimedia/publish.ps1 platforms
pwsh ./src/Multimedia/publish.ps1 publish skill-grok-image -Rid win-x64
pwsh ./src/Multimedia/publish.ps1 all -Rid win-x64
```

Windows 还可使用 `src/Multimedia/publish.cmd`，Linux/macOS 可使用 `bash ./src/Multimedia/publish.sh`。每个技能项目均自带 `publish.json`、`publish.ps1` 与 `skill/`；公共库随 CLI 打包，不单独发布技能。

| 发布项 | 约定 |
| --- | --- |
| 最终目录 | 仓库根 `skills/<skill_name>/`，文件夹名与技能名一致。 |
| 完整内容 | `SKILL.md`、`references/`、`scripts/`，CLI 及运行依赖放在 `runtime/<RID>/`。 |
| RID 选择 | 清单可声明 `win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64`；实际可选项以 `list` 和项目清单为准。 |
| 发布前置 | 相关源码、技能源资源及公共构建输入已提交；脚本不会自动提交。 |
| 替换与合并 | 先暂存校验再替换单个技能，失败保留旧包；多 RID 合并按版本、源码修订和摘要检查，不覆盖无关技能。 |
| 操作范围 | 本地打包；远程推送、全局安装和真实 API 验证分别执行。 |

当前 OpenAI、Grok 已有技能源资源和发布入口，尚未生成这两个技能的根目录正式发布包。完整发布流程见 [Multimedia README](src/Multimedia/README.md)。

### Runtime CLI 发布

主 CLI 使用独立的 `build/madorin-cli.publish.ps1`，发布输出位于 `releases/Madorin-Cli/<RID>/`。可先查看平台和参数：

```powershell
pwsh ./build/madorin-cli.publish.ps1 list
```

主 CLI 发布流程与多媒体技能发布入口分别维护；完整安装包应保留 CLI、所需 Provider Worker 与运行依赖。

## 验证状态与开发流程

| 项目 | 当前记录 |
| --- | --- |
| 多媒体构建与离线测试 | Grok 批次记录为构建 0 警告、0 错误，多媒体测试 68/68 通过；属于既有批次证据。 |
| 中文试运行与技能源结构 | Grok 批次已验证中文 try-run、凭证脱敏和技能源结构。 |
| 真实图片生成 | 已授权的本机兼容端点调用返回 HTTP 404，无图片产物；真实成功生成仍待验证。 |
| Native AOT 与目标平台 | 多媒体真实 AOT 发布、目标 RID 实机运行仍待验证。 |

后续修改多媒体行为时，可按影响范围构建对应项目，并运行定向测试：

```powershell
dotnet test tests/Madorin.AI.Multimedia.Tests/Madorin.AI.Multimedia.Tests.csproj
```

仅调整文档时检查结构、链接和契约一致性；行为变更再执行对应构建与测试，真实生成和正式发布按任务授权执行。

新协议开发先核对设计方案、当前执行记录、目标项目与公共库 README，再按职责复用公共组件。源码新增在 `src/Multimedia/`；现有 Runtime、`src/Providers/`、根解决方案和公共构建配置的集成变更需明确范围。

交付应区分已实现、离线通过、真实 API 通过和实际发布。提交与推送按授权执行，提交后保留合并提醒，并使用 `md-tfs-cli` 维护对应工作项、有序工作步骤及提交、PR 链接；具体流程见 [agents.md](agents.md)。密钥、真实请求、生成媒体和调试产物不进入源码提交或技能包。
