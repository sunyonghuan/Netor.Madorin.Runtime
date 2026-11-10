# 快手可灵 / 可图协议记录

## 批次状态

- 状态：P0 至 P6 源码和技能源文件已按本记录落盘；未运行构建、测试、发布或真实 API。
- 记录日期：2026-11-10。
- 技能：`skill-kling-image`、`skill-kling-video`。图片仍使用旧版任务接口，视频使用 Kling 4.0 路径接口，不合并为单个项目。
- 不在本批：可图独立供应商、Omni、多关键帧、主体、音色、口型、视频延长、动作控制、特效、试穿、扩图、编辑、回调、AK/SK JWT、静态模型目录、Runtime 集成、正式发布。

## 官方核对

核对日期：2026-11-10。证据取自 [可灵 AI 开放平台](https://klingai.com/document-api) 现行文档包，不以参数统计稿代替。

- 接口鉴权：新域名 `https://api-beijing.klingai.com`，新加坡 `https://api-singapore.klingai.com`。API Key 组装为 `Authorization: Bearer <API Key>`。JWT 仅适用于模型放在参数中的旧版接口。
- 错误码：HTTP 与业务码对照见下文。
- 图像生成：`POST /v1/images/generations`，`GET /v1/images/generations/{id}`。
- Kling 4.0 文生视频：`POST /text-to-video/kling-4.0`，查询 `GET /tasks?task_ids=`。
- Kling 4.0 图生视频：`POST /image-to-video/kling-4.0`。首帧、中间帧、尾帧官方存在；本批只实现单张首帧。
- 官方 skill 说明更新日期为 2026-10-28，只用于核对模型名线索，不替代本仓库技能契约。

CLI 默认 `--api-url` 为 `https://api-beijing.klingai.com`。地址和密钥只来自 argv。

文档索引没有通用模型目录端点。`list-models` 在校验 request-id、HTTPS 地址和 api-key 后返回 `provider_error`，不回退静态清单。模型 ID 原样提交。

## 与参考文档的冲突

[10-快手-可灵可图.md](../参数统计/10-快手-可灵可图.md) 以下内容失效，不得进入实现：

| 参考文档 | 现行官方 | 处理 |
| --- | --- | --- |
| 视频模型 `kling-3.0-omni`、`kling-pro` | 视频路径模型包括 `kling-4.0`、`kling-4.0-flash` 等；图片默认 `kling-v3` | 视频默认 `kling-4.0`，图片默认 `kling-v3`，不发送旧别名 |
| `mode=text-to-video` 和 `GET /api/v1/videos/{job_id}` | 模型在路径中；查询为 `GET /tasks` | 不拼接旧路径 |
| `quality=standard/pro/4K`、`native_audio`、`multi_shot`、`camera_control` | Kling 4.0 使用 `settings.resolution`、`settings.duration`、`settings.aspect_ratio` | 不猜测映射 |
| 图片 `width` / `height` / `num_images` / `seed` | 图片使用 `aspect_ratio`、`resolution`、`n`；文档未列 seed | 宽高不能转换成比例；不发送 seed |
| 视频时长只有 5/10 | Kling 4.0 时长为 3 至 30 秒整数，省略时由模型或默认 10 秒决定 | 只在显式传入时发送 |
| 同步图片 URL 响应 | 两边都先返回任务，再查询结果 URL | 按异步实现 |
| 错误码 `invalid_parameter`、`unauthorized` | 业务码为数字，如 `1000`、`1101`、`1200`、`1301` | 按现行错误码映射 |

## 能力结论

| 能力 | 结论 | 依据 |
| --- | --- | --- |
| 文生图 `text-to-image` | 纳入 | `POST /v1/images/generations`，成功后 `task_result.images[].url` |
| 文生视频 `text-to-video` | 纳入 | `POST /text-to-video/{model}`，默认模型 `kling-4.0` |
| 单图生视频 `image-to-video` | 纳入 | `contents` 中一个 `first_frame`，`url` 支持 URL 或 Base64 |
| 多关键帧、Omni、主体 | 官方确认，本批不纳入 | 需要额外槽位和另一套端点 |
| 公开异步 | 图片和视频均为 true | 系统任务 ID 可跨进程查询 |
| 批量与部分成功 | 不纳入 | `supports_partial=false`。图片 `n` 全部成功才算成功 |
| 远端取消 | 不支持 | 官方未提供取消端点 |
| 模型目录 | 不支持 | 无通用目录端点 |

## 图片字段

| 公共或 CLI 字段 | 官方字段 | 规则 |
| --- | --- | --- |
| `model` | `model_name` | argv、文件、默认 `kling-v3`；非空且无控制字符即原样提交。官方示例枚举含 `kling-v2-1`、`kling-v3`，但不做本地白名单 |
| `prompt` | `prompt` | 必填，按码点不超过 2500，不截断 |
| `provider_parameters.kling.negative_prompt` | `negative_prompt` | 可选，不超过 2500 码点。本批文生图不发送参考图，因此不触发图生图禁用负向词规则 |
| `provider_parameters.kling.resolution` | `resolution` | `1k` 或 `2k`，缺省发送 `1k` |
| `provider_parameters.kling.count` | `n` | 1 至 9，缺省发送 `1`。不用公共批量字段猜测 |
| `provider_parameters.kling.aspect_ratio` | `aspect_ratio` | `16:9`、`9:16`、`1:1`、`4:3`、`3:4`、`3:2`、`2:3`、`21:9`，缺省发送 `16:9` |
| `provider_parameters.kling.watermark_enabled` | `watermark_info.enabled` | 仅显式布尔值才发送 |

提交响应取 `data.task_id`、`data.task_status`、顶层 `request_id`。状态 `submitted`、`processing` 继续查询，`failed` 失败，`succeed` 后读取 `task_result.images`。注意图片成功状态是 `succeed`，不是视频新接口的 `succeeded`。

查询路径是文档规定的 `GET /v1/images/generations/{task_id}`。`task_id` 与 `external_task_id` 二选一；本批只保存和恢复系统任务 ID，不实现自定义任务 ID。

## 视频字段

| 公共或 CLI 字段 | 官方字段 | 规则 |
| --- | --- | --- |
| `model` | 路径中的模型段 | 默认 `kling-4.0`。原样放入 `/text-to-video/{model}` 或 `/image-to-video/{model}`，不改写到旧版 `/v1/videos/*` |
| `prompt` | 文生视频 `prompt`；图生视频 `contents[].text` | 文生视频必填。图生视频可缺省。不截断，官方 4.0 字段表未给出硬性长度上限 |
| 单个 `reference` 或 `source` 附件 | `contents[]` 的 `first_frame.url` | 远程 HTTPS 原样提交。本地路径只在正式执行读取，编码为不带 `data:` 前缀的 Base64。try-run 不读媒体 |
| `duration` | `settings.duration` | 仅显式传入时发送。Kling 4.0 文档范围为 3 至 30 秒整数。缺省不发送，避免覆盖提示词中的时长 |
| `resolution` | `settings.resolution` | `720p` 或 `1080p`，缺省发送 `1080p`。这是 Kling 4.0 默认，不是 Flash 的 `720p` |
| `provider_parameters.kling.aspect_ratio` | `settings.aspect_ratio` | 仅文生视频。`21:9`、`16:9`、`9:16`、`1:1`，缺省发送 `16:9`。图生视频传入则失败 |
| `provider_parameters.kling.watermark_enabled` | `options.watermark_info.enabled` | 仅显式布尔值才发送 |

提交响应取 `data.id`、`data.status`、顶层 `request_id`。查询响应的 `data` 是数组，按任务 ID 匹配。`submitted`、`processing` 继续查询，`failed` 失败，`succeeded` 后从 `outputs` 中取 `type=video` 的 `url`。封面或水印 URL 不下载、不列为产物。

图生视频官方还支持中间帧和尾帧，最多 10 张。本批拒绝第二张及非首帧角色。图片格式为 jpg、jpeg、png，不超过 30MB，边长不小于 300px，宽高比介于 2:5 和 5:2。这些限制在正式读取时校验；try-run 只校验附件结构。

## 任务上下文

两边都要求 `--task-context-file`。上下文保存技能、Provider、任务 ID、任务、模型和预期槽位，不保存 API 地址、API Key、提示词或通用令牌。查询路径由官方规则和任务 ID 重建，不是必须原样保存的 `polling_url`。

每次 query、download 使用新的调用 `request_id`。成功查询返回完整最新上下文。失败保留已知任务 ID 和已验证上下文。

## 错误映射

| 官方信号 | 公共错误 |
| --- | --- |
| HTTP 401，业务码 `1000` 至 `1004` | `authentication_failed` |
| 业务码 `1101`、`1102` | `insufficient_balance` |
| 业务码 `1301` | `content_policy_violation` |
| HTTP 429，业务码 `1100`、`1302`、`1303` | `rate_limit_exceeded` |
| HTTP 408/504，业务码 `5002` | `timeout` |
| 其他，包括 `1200` 至 `1203`、`1300`、`1304`、`5000`、`5001` | `provider_error`，保留安全的官方 code |

不回显原始响应体、密钥、签名 URL 或 Base64 图片。

## 下载

结果 URL 约 30 天有效，示例主机包括可灵 CDN 与 `h1.inkwai.com`，但官方没有完整主机白名单。下载只接受无用户信息、无片段的 HTTPS，关闭自动重定向，最多手动跟随 3 跳。图片校验 PNG/JPEG，视频校验 MP4 容器。URL 本身不是产物。

## 源码路径

- `src/Multimedia/Madorin.AI.Kling.Image/`
- `src/Multimedia/Madorin.AI.Kling.Video/`
- 复用 `Madorin.AI.Multimedia.Core`，本批不修改公共库。

## 验证边界

按协议推进规则，本阶段不运行 `dotnet build`、`dotnet test`、AOT、发布脚本或真实 API。未验证账户权限、结果 URL 实际主机、重定向、图片 MIME、视频容器、Base64 是否被视频接口接受、轮询时延和取消终态。
