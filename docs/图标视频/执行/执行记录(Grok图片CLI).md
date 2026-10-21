# Grok 图片 CLI 实施与交付记录

依据：[技能 CLI 设计方案](../策划/技能CLI设计方案.md)。本批实现同步文生图初版，复用现有公共库，不代表完整协议或全部平台验收完成。

## 范围与实现

| 工作项 | 实际交付 | 验证或边界 |
| --- | --- | --- |
| 项目与架构 | 新增 `src/Multimedia/Madorin.AI.Grok.Image/`，按 Cli、Requests、Metadata、Provider、skill 拆分 | 项目引用 Multimedia.Core，不改宿主 Runtime 或根解决方案 |
| 命令与模型 | `metadata`、`list-models`、`version`、`generate`；模型 `grok-imagine-image-2.0` | 仅同步 `text-to-image`，不声明公开异步或部分成功 |
| 输入 | JSON 请求文件与 argv 合并，显式模型覆盖，关联 ID、总预算和取消复用公共库 | 未支持参数、其他 Provider 参数与附件按契约校验后过滤；附件不参与生成 |
| Provider 映射 | `POST /v1/images/generations`，Bearer 认证，prompt、model、aspect_ratio、n | n 为 1-10，固定 `response_format=b64_json`；不启用 deferred |
| 产物 | Base64 解码、PNG/JPEG/WebP 格式探测与公共完整解码校验、不覆盖落盘 | URL-only、202/异步任务及损坏结果返回受控失败，不冒充已落盘成功 |
| 试运行 | 共用正式请求映射器，预览模型与有效参数一致，凭证脱敏 | 不联网、不读媒体、不写输出目录；中文回显和 Unicode 检查已验证 |
| HTTP 回环 | OpenAI 与 Grok 允许本机回环 HTTP；其他地址要求 HTTPS | 同时拒绝用户信息、查询参数和片段；不关闭 TLS 证书校验 |
| 技能源与发布入口 | SKILL.md、references、scripts、publish.json、publish.ps1 | 发布源结构通过校验；未生成根 skills 发布包 |

## 验证证据

| 实际命令或检查 | 结果 |
| --- | --- |
| `dotnet build src/Multimedia/Madorin.AI.Grok.Image/Madorin.AI.Grok.Image.csproj --nologo` | 通过，0 警告、0 错误 |
| `dotnet test tests/Madorin.AI.Multimedia.Tests/Madorin.AI.Multimedia.Tests.csproj --nologo` | 68/68 通过，含 Grok 7 项及 OpenAI 端点边界回归 |
| Grok CLI 中文提示词 try-run | validated、退出码 0；API Key 脱敏、无网络及输出写入副作用 |
| `Test-MultimediaSkillSource` 检查 Grok 技能源 | 通过，仅证明源结构有效 |
| 已授权本机兼容端点真实调用 | 请求已到达 images/generations，端点返回 HTTP 404；CLI 返回 provider_error、退出码 125，无图片落盘 |

真实调用失败原因由直接 HTTP 状态核实为 404；输出目录可由产物流程创建，不能将此前目录创建命令报错视为 Provider 404 的原因。真实 xAI 官方服务、成功生成、Native AOT 发布和目标 RID 实机运行均未验证。

## 后续步骤与交付状态

| 顺序 | 目标与输入 | 操作与验收 | 状态 |
| --- | --- | --- | --- |
| 1 | 已实现源码、技能资源与上述离线证据 | 中文提交并推送当前功能分支，报告完整提交链接 | 本批交付执行，实际结果以 Git 记录为准 |
| 2 | 当前工作树 TFS 项目与 PAT | 使用 md-tfs-cli 读取工作项类型、既有条目与 PR | PAT 文件规范化后仍 HTTP 401，认证待解决 |
| 3 | 提交链接、本记录、实际独立任务 | 查询去重后按 preview/confirm 补录工作项、步骤和提交关联，写入后回读 | 尚未写入；不得把离线材料当作已补录 |
| 4 | 源分支与目标 master | 核对已有 PR，提醒审查并合并本批提交 | PR 查询受 TFS 认证阻断，尚未合并 |
| 5 | 有效图片生成端点及明确调用条件 | 真实成功生成与失败边界验证，再按授权执行技能发布 | 待执行，不自动发布 |

本批不提交密钥、TFS 本地配置、临时评审目录或生成媒体。工作项按真实独立交付拆分；未验证能力及未发布状态必须保留。
