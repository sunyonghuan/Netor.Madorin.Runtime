# 跨 CLI 模型选择约定 : 100%

依据：[技能 CLI 设计方案](../策划/技能CLI设计方案.md)。本批落实用户确认的模型外部选择方案，不扩大已有 CLI 能力或修改宿主 Runtime。

## Step 1 核对现有规则与实现 : 100%

- [√] 确认现有 `--model` 可覆盖文件值，覆盖后再校验任务和模型；最终模型不受支持时不能丢弃或回退。
- [√] 确认请求文件可省略 `model`、`request_id` 和输出目录，分别由本次调用参数提供。

## Step 2 同步设计与调用说明 : 100%

- [√] 明确主程序跨 CLI 调用显式传 `--model`，保留文件值、声明默认和唯一候选的兼容选择顺序。
- [√] 同步调用参数表、请求示例、try-run、流程图、执行任务、场景推演和 OpenAI 技能说明。

## Step 3 定向验证与收口 : 100%

- [√] 离线验证模型优先级、同文件多次调用、最终模型回显和不支持模型/任务失败。
- [√] 检查文档表格、链接、示例和同一事实的副本，记录实际结果与未验证边界。

## 变更与验证记录

| 项目 | 记录 |
| --- | --- |
| 实际修改文件 | 见下方清单；业务源码、Schema、发布脚本及根 agents.md 均未变更。 |
| 实现调整 | 现有实现已满足 argv 覆盖、默认兜底、失败与模型回显约定；不修改业务代码，新增模型选择回归测试。 |
| 验证证据 | `dotnet test tests/Madorin.AI.Multimedia.Tests/Madorin.AI.Multimedia.Tests.csproj --no-restore --filter FullyQualifiedName~ModelSelectionTests --nologo -v:minimal`：9 通过、0 失败、0 跳过；命令同时构建公共库、OpenAI CLI 和测试项目。SDK 10.0.401，MSTest / VSTest。 |
| 文档检查 | 11 份 Markdown 的表格列数、39 个本地链接、6 个 JSON 示例及代码围栏通过检查；场景 S01–S42 共 42 个且不重复。同事实副本已核对，异步提交示例同步补入 argv 模型和调用 ID。 |
| 未验证边界 | 当前只有 OpenAI 同步文生图实现；不宣称已验证其他 CLI、真实 API、Native AOT 或跨平台发布。 |

### 实际变更清单

- `docs/图标视频/策划/技能CLI设计方案.md`
- `docs/图标视频/执行/README.md`
- `docs/图标视频/执行/02-请求输入与参数处理.md`
- `docs/图标视频/执行/03-命令输出与试运行.md`
- `docs/图标视频/执行/07-验收与交付.md`
- `docs/图标视频/执行/可实施性验证/README.md`
- `docs/图标视频/执行/可实施性验证/01-场景推演与预期结果.md`
- `docs/图标视频/执行/执行计划(跨CLI模型选择).md`
- `src/Multimedia/README.md`
- `src/Multimedia/Madorin.AI.OpenAI.Image/skill/SKILL.md`
- `src/Multimedia/Madorin.AI.OpenAI.Image/skill/references/protocol.md`
- `tests/Madorin.AI.Multimedia.Tests/ModelSelectionTests.cs`
