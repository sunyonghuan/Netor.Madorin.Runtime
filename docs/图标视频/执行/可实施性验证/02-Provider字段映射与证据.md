# Provider 字段映射与证据

[返回验证总览](./README.md)。本报告只验证协议形状能否由统一请求表达，并给出 Provider 接入前的证明条件。官方页面查询于 `2026-10-21`；未调用端点、未使用凭证。

## 1. 证据规则

| 来源 | 用途 | 限制 |
| --- | --- | --- |
| [BFL FLUX.2 Image Editing](https://docs.bfl.ai/flux_2/flux2_image_editing) | 多参考图片、提示词位置引用和图像编辑 API。 | 以页面当前描述为准；具体 API schema 仍需实现时锁定版本。 |
| [BFL API Integration](https://docs.bfl.ai/api_integration/integration_guidelines) | 异步请求返回 `polling_url` 及强制轮询使用规则。 | 不据此推测所有 BFL 路由使用相同恢复协议。 |
| [BFL FLUX.2 [pro] API Reference](https://docs.bfl.ai/api-reference/models/generate-or-edit-an-image-with-flux2-%5Bpro%5D) 与 [Get Result](https://docs.bfl.ai/api-reference/utility/get-result) | 输入尺寸、最多八个命名输入图字段、任务 ID 和轮询地址。 | API schema 当前含 preview 路由；正式 model ID/任务可用性仍须按部署锁定核实。 |
| [xAI Multi-Image Editing](https://docs.x.ai/developers/model-capabilities/images/multi-image-editing) 与 [Image API Reference](https://docs.x.ai/developers/rest-api-reference/inference/images) | 最多五张编辑图、按请求顺序的位置 token、`images[]` 输入形状。 | 图像来源可以是 URL、base64 data URI 或 Files API `file_id`；公共 CLI 的安全路径仍需 Provider 实现承接。 |
| [xAI Video API Reference](https://docs.x.ai/developers/rest-api-reference/inference/videos) | 视频 keyframes 和 reference images 的输入结构、查询请求 ID。 | “reference images”表示内容/风格参考；`keyframes` 的 `timestamp_s` 是实际时间轴。不能混用普通附件顺序。 |
| [Microsoft Learn: Native AOT cross-compilation](https://learn.microsoft.com/dotnet/core/deploying/native-aot/cross-compile) | 发布的宿主/目标 OS 与架构限制。 | 工具选择和各目标的真实构建通过情况仍待实现验证。 |

外部资料只证明公开文档当前描述，不证明账号开通、模型端实际可用或结果质量。仓库内“参数统计”目录的资料可能包含旧信息，不作现行 API 契约。

## 2. 可比接口映射表

对照任务选择：**以多张参考图和指令编辑/生成图片**。统一任务使用 `image-edit`；必须在 metadata 声明附件角色、数量及每个型号的限制。

| 统一输入 | BFL FLUX.2 [pro] 文档接口形状 | xAI 多图编辑接口形状 | 映射判断 / 限制 |
| --- | --- | --- | --- |
| `task=image-edit` | 同一路由支持提示词和可选输入图 | `POST /v1/images/edits`，可选单 `image` 或多图 `images` | 两端都能表达指令编辑；各自支持图像来源格式须分开处理。 |
| `prompt` | JSON `prompt` 字符串 | JSON `prompt`；多图时以 `<IMAGE_0>`、`<IMAGE_1>` 等位置指代 | 统一 prompt 可容纳原文，但位置指代语法是 Provider 级知识，不能把附件 `id` 自动替换进文案。 |
| 有序 `attachments[]` / `reference` | 命名输入图字段 `input_image` … `input_image_8`，不是通用数组形状 | `images[]`，图像内容/风格参考，最多五张 | CLI 可按附件顺序映射到目标字段；过滤前先检验每个附件的结构、路径、ID 唯一性及目标数量。超限不是随意丢弃附件。 |
| `attachment.id` | API 输入字段未定义与此同义的用户 ID | 模型按位置引用图像，不按统一 ID 查找 | ID 只供统一协议关联；Provider 对映射顺序应可诊断，不应暗改提示词。 |
| `source.path` | 官方 schema 把命名输入写作图片路径 | 接口接受文件 API `file_id`、公有 URL 或 base64 data URI；不等于本机路径 | 本地 path 由 CLI 读取后用官方支持的传输方式提交；不得把本机绝对路径当远端地址。 |
| `source.uri` | 输入图的远端可接受形式需要按所选端点/schema 验证；不能据预览 URL 示例假设任意域名都可用 | 支持公有 URL 或可下载的图像内容地址，并允许 Files API file_id | URI scheme、内容类型、可达性及签名时效分 Provider 验证。try-run 只验静态 URI 格式。 |
| `num_images` | 输出数量与具体 model schema/路由限制关联 | 多图编辑字段 `n` 定义输出编辑数 | 仅当明确映射且目标接受范围有效时原生映射；不得以过滤输入图来凑数量。 |
| `width` / `height` / `size` | `width`、`height` 分别为输入字段 | 文档以 `aspect_ratio` 为主，图像输出另有尺寸/分辨率定义 | 只有源/目标 schema 规则证明尺寸语义一致才可转换；宽高不能仅按名字映射到比例。 |
| 异步任务 | 初始响应必含 `id` 和 `polling_url`；BFL 要求使用返回的 URL 轮询 | deferred edit 返回 `request_id`，GET `/v1/images/{request_id}` 查询 | 两者外形并不等价；BFL query 恢复依赖调用上下文，xAI 此接口查询路径可由 ID 形成。 |
| 完成产物 | BFL polling 得到结果，配送 URL 有过期规则 | xAI 结果可返回产物 URL 或上传目标，按 API 响应分流 | `GenerateResponse` 必须在下载、完整验证和落盘后返回产物；异步 task 查询结果不虚报本地文件。 |

该对照显示，统一外层可以承载两者的输入；但并不意味着一份原始请求会在不同模型下得到相同效果。模型能力、字段范围和无损转换要在 metadata 与 `parameter_conversions` 中具体声明。

## 3. 视频多图片场景

| 区分项 | xAI 文档例子 | 统一字段含义 | 可实施边界 |
| --- | --- | --- | --- |
| `reference_images[]` | 图生视频 API 允许多张风格/内容参考图片；提示词可用对应位置语法引用。 | `role=reference` 的有序图像上下文。 | 按顺序传入 reference list；调用方及技能文档负责提示词的 Provider 位置语法。 |
| `image` / 首帧 | 视频 API 的 `image` 是实际起始图像。 | `role=source` 或 `first_frame`。 | 属于起始画面任务语义，不得因同为 URL 而与 `reference` 混淆。 |
| `keyframes[]` | 每项含嵌套 `image` 和必需 `timestamp_s`；时间戳须严格大于 0、小于 duration；特定模型最多四项。 | 需表达有序图片和具体视频时间位置。 | 当前附件对象只有 `id`、`role`、`source`，不能表达时间戳及多个中间关键帧；不得把数组顺序当时间戳。此功能需使用 Provider 专用 Schema，并明确如何关联媒体引用，见 F07。 |
| `last_frame` | `image` 和 `last_frame` 分别固定第一帧与最后一帧。 | `first_frame` 和 `last_frame` 两个不同角色。 | 只有两端帧语义确实相同时映射，不能将任何参考图替代末帧。 |
| 提示词 | 通过 `<IMAGE_0>`、`<IMAGE_1>` 等按输入顺序引用图片；BFL 编辑例使用 image 1、image 2 | prompt 原样保留；附件 ID 不发生文本替换 | 原样保持能保证字节/Unicode 层不改写，但不能自行保证语义引用绑定。跨 CLI 的位置 token 不是通用语法。 |

附件“多张”是集合顺序；关键帧是带时间戳的时间轴。把两者放在同一个 `attachments[]` 且只靠 role 区分时，位置与角色语义仍可分开，但官方 API 特有的时间戳对象必须有清晰的 Provider 专用参数定义，不能把未知字段丢弃后继续声称成功实现关键帧。

## 4. 异步任务恢复表

| Provider / 接口 | 提交后保留的数据 | 后续查询所需输入 | 当前设计可推导的接入门槛 |
| --- | --- | --- | --- |
| BFL 多集群端点 | `id` 及必需 `polling_url` | 官方明确要求原 `polling_url`，另需 API Key | `task_id` 字符串单独不足以恢复地址。Provider 必须把原始 URL 放入受限 `TaskContext.provider_context` 并通过 `--task-context-file` 传递；否则该能力可在同一次 generate 内部轮询，但不声明可公开 query/download。 |
| xAI 图像 deferred edit | `request_id` | `GET /v1/images/{request_id}` 和认证 | 路径可由请求 ID 构建；若任务 schema/输出下载还需原始模型或槽位数，必须从 Provider 可恢复信息或安全的任务上下文获取，不能用新默认模型猜。 |
| xAI 视频 generation | `request_id` | `GET /v1/videos/{request_id}` 和认证 | 同名 `request_id` 是远端待办 ID，不是主程序的 `request_id`/顶层 `response_id`；视频查询当前文档会返回实际模型，因此该项可用于恢复模型。若任务还需原请求的数量等未由查询响应完整保留的信息，仍需从 Provider 上下文或其他远端事实恢复。 |
| 其他厂商 | 按当前官方 API 决定 | 可能要求端点 URL、task ID、区域、模型等组合 | P4/P5 接入前要核对是否可新进程恢复。`supports_async=true` 只在公开提交/查询/下载都可闭环时成立。 |

配置基础 URL / API Key 仍由主程序直接传参；BFL 的带授权 `polling_url` 只可经 `TaskContext` 受限往返，不能进入日志、通用诊断回显或发布材料。该 URL 必须校验 HTTPS、允许 Provider 主机和路径，不能不加验证地转发到任意请求目标。

## 5. Native AOT 发布可行性

Microsoft Learn 当前说明：Native AOT 依赖目标 OS 原生链接器和 SDK；不支持普通的跨 OS 编译，可以在支持的工具链下跨 x64/Arm64 架构。因而，Windows、Linux、macOS 均列在同一个 RID 清单中并不意味着任意一台机器都能一键构建所有 RID。若要求真实 Native AOT，应在对应 OS 的主机、CI runner 或受支持的隔离环境构建，再汇集到完整 skill 包；不得因某构建宿主缺工具链就默默降级到 JIT/self-contained，并仍标记 AOT 发布。

官方依据：[Cross-compilation for Native AOT](https://learn.microsoft.com/dotnet/core/deploying/native-aot/cross-compile)，其中说明“Native AOT does not support cross-OS compilation”；同文说明需要目标平台链接器和原生库。当前工作树已有的 `PublishAotCross.Tools` 可能解决部分特定工具链路径，不能覆盖官方支持边界的确认责任；须针对实际 SDK、原生依赖和工具版本做后续验证。
