# 智谱 CogView / CogVideoX 协议记录

## 批次状态

- 状态：P0 官方核对完成，图片与视频技能源码已按本记录落盘；未运行构建、测试、发布或真实 API。
- 记录日期：2026-11-10。
- 技能：`skill-zhipu-image`、`skill-zhipu-video`。因图片同步、视频可跨进程查询，不合并为单个 `ImageVideo` 项目。
- 不在本批：GLM-Image 异步图片、Vidu、首尾帧、图片编辑、静态模型目录、Runtime 集成、正式发布。

## 官方核对

核对日期：2026-11-10。页面与 OpenAPI 片段取自现行文档，不以参数统计稿代替。

- [CogView-4](https://docs.bigmodel.cn/cn/guide/models/image-generation/cogview-4.md)
- [GLM-Image](https://docs.bigmodel.cn/cn/guide/models/image-generation/glm-image.md)：仅用于区分现行旗舰，不纳入本批。
- [CogVideoX-3](https://docs.bigmodel.cn/cn/guide/models/video-generation/cogvideox-3.md)
- [图像生成](https://docs.bigmodel.cn/api-reference/模型-api/图像生成.md)
- [图像生成(异步)](https://docs.bigmodel.cn/api-reference/模型-api/图像生成异步.md)：OpenAPI 写明仅 `glm-image`。
- [视频生成(异步)](https://docs.bigmodel.cn/api-reference/模型-api/视频生成异步.md)
- [查询异步结果](https://docs.bigmodel.cn/api-reference/模型-api/查询异步结果.md)
- [错误码](https://docs.bigmodel.cn/cn/api/api-code.md)
- [HTTP API 调用](https://docs.bigmodel.cn/cn/guide/develop/http/introduction.md)
- [模型概览](https://docs.bigmodel.cn/cn/guide/start/model-overview.md)

认证为 `Authorization: Bearer <API Key>`。通用根地址文档写为 `https://open.bigmodel.cn/api/paas/v4/`。OpenAPI server 为 `https://open.bigmodel.cn/api/`，路径以 `/paas/v4/` 开头。CLI 默认 `--api-url` 为 `https://open.bigmodel.cn/api`；若调用方已传入以 `/paas/v4` 结尾的地址，不再重复追加该段。地址与密钥只来自 argv。

文档索引和 OpenAPI 均未给出通用模型目录端点。`list-models` 在校验 request-id、HTTPS 地址和 api-key 后返回 `provider_error`，不回退静态清单。模型 ID 仍原样提交，不做本地白名单。

## 与参考文档的冲突

[03-智谱-CogView-CogVideoX.md](../参数统计/03-智谱-CogView-CogVideoX.md) 以下内容失效，不得进入实现：

| 参考文档 | 现行官方 | 处理 |
| --- | --- | --- |
| 图片模型 `cogview-3-plus` | OpenAPI 枚举为 `glm-image`、`cogview-4-250304`、`cogview-4`、`cogview-3-flash` | 不发送旧 ID；默认模型用 `cogview-4` |
| 图片 `n`、`response_format`、`user` | `CreateImageRequest` 无这些字段 | 不映射 |
| 图片尺寸仅 `1024x1024`、`768x1024`、`1024x768` | CogView 推荐尺寸另有 `768x1344` 等；自定义为 512–2048、16 倍数、像素积不超过 2^21 | 按现行 CogView 规则校验 |
| 视频模型 `cogvideox`、`cogvideox-text-to-video`、`cogvideox-image-to-video` | 现行旗舰为 `cogvideox-3`；另有 `cogvideox-2`、`cogvideox-flash` 与 Vidu 模型 | 本批默认并按 `cogvideox-3` 字段实现 |
| 视频 `resolution=480p/720p/1080p` | CogVideoX-3 使用 `size` 像素枚举，没有 `resolution` | 不猜测映射 |
| 视频同步返回 `data[].url` | 提交返回任务 `id`，查询为 `GET /paas/v4/async-result/{id}` | 按异步实现 |
| 字符串错误码 `invalid_request_error` 等 | 业务码为数字字符串，如 `1000`、`1211`、`1301`、`1302` | 按现行错误码映射 |

## 能力结论

| 能力 | 结论 | 依据 |
| --- | --- | --- |
| 文生图 `text-to-image` | 纳入 | 同步 `POST /paas/v4/images/generations`，响应 `data[].url` |
| 文生视频 `text-to-video` | 纳入 | `cogvideox-3`，`prompt` 与 `image_url` 不能同时为空 |
| 单图生视频 `image-to-video` | 纳入 | `image_url` 支持 HTTPS URL 或 Base64；png/jpeg/jpg，不超过 5MB |
| 首尾帧 | 官方确认，本批不纳入 | `image_url` 可为两张图；需要额外槽位语义，留待后续批次 |
| 公开异步 | 仅视频为 true | 查询端点由任务 ID 和官方路径组成，可跨进程恢复；图片异步端点不属于 CogView |
| 批量与部分成功 | 不纳入 | 图片响应说明当前只含一张图；两边均 `supports_partial=false` |
| 远端取消 | 不支持 | 官方未提供取消端点 |

## 图片字段

| 公共或 CLI 字段 | 官方字段 | 规则 |
| --- | --- | --- |
| `model` | `model` | argv、文件、默认 `cogview-4`；非空且无控制字符即原样提交 |
| `prompt` | `prompt` | 必填，不截断 |
| `size` 或成对 `width`/`height` | `size` | 缺省 `1024x1024`；`*`、`×` 规范为 `x`；边长 512–2048、16 倍数、像素积不超过 2097152；冲突失败 |
| `quality` | `quality` | `hd` 或 `standard`，缺省发送 `standard`。这是 CogView 默认，不是 GLM-Image 的 `hd` |
| `provider_parameters.zhipu.watermark_enabled` | `watermark_enabled` | 仅显式布尔值才发送 |
| `provider_parameters.zhipu.user_id` | `user_id` | 6 至 128 字符，无控制字符 |

成功响应使用 `data[].url`。官方写明临时链接约 30 天，但未公布主机白名单。下载不把 URL 当产物：只接受无用户信息的 HTTPS，关闭自动重定向，最多手动跟随 3 跳，校验图片容器后落盘。

## 视频字段

| 公共或 CLI 字段 | 官方字段 | 规则 |
| --- | --- | --- |
| `model` | `model` | 默认 `cogvideox-3`，原样提交 |
| `prompt` | `prompt` | 文生视频必填；图生视频可缺省。按码点计数，超过 512 失败，不截断 |
| 单个 `reference` 或 `source` 附件 | `image_url` | 远程 HTTPS 原样提交；本地路径只在正式执行读取并编码为 data URI。try-run 不读媒体 |
| `duration` | `duration` | `5` 或 `10`，缺省发送 `5` |
| `fps` | `fps` | `30` 或 `60`，缺省发送 `30` |
| `size` 或成对宽高 | `size` | 仅 CogVideoX-3 枚举：`1280x720`、`720x1280`、`1024x1024`、`1920x1080`、`1080x1920`、`2048x1080`、`3840x2160`。缺省不发送 |
| `provider_parameters.zhipu.quality` | `quality` | `speed` 或 `quality`。不复用图片公共 `quality` 的 `hd/standard` |
| `provider_parameters.zhipu.with_audio` | `with_audio` | 显式布尔值才发送 |
| `provider_parameters.zhipu.watermark_enabled` | `watermark_enabled` | 显式布尔值才发送 |
| `provider_parameters.zhipu.user_id` | `user_id` | 6 至 128 字符 |
| 本次 `request_id` | `request_id` | 原样传给官方客户端任务号；响应里的官方 `request_id` 只进入 `provider_metadata.request_id` |

提交响应只取 `id`、`request_id`、`task_status`。`PROCESSING` 继续查询，`FAIL` 失败，`SUCCESS` 仍以查询结果中的 `video_result[].url` 为准。查询路径是文档规定的 `GET /paas/v4/async-result/{id}`，不是 BFL 那种必须原样保存的 `polling_url`。任务上下文保存技能、Provider、任务 ID、任务、模型和输出槽位，不保存 API 地址、密钥或提示词。

官方查询响应没有独立错误对象。`FAIL` 归一为 `provider_error`。封面 URL 不下载、不列为产物。

## 错误映射

| 官方信号 | 公共错误 |
| --- | --- |
| HTTP 401/403，业务码 `1000`、`1001`、`1003`、`1005` | `authentication_failed` |
| 业务码 `1113` | `insufficient_balance` |
| 业务码 `1301` | `content_policy_violation` |
| HTTP 429，业务码 `1302`、`1305`、`1308` 至 `1321` | `rate_limit_exceeded` |
| HTTP 408/504 | `timeout` |
| 其他，包括 `1211` 模型不存在 | `provider_error`，保留安全的官方 code |

不回显原始响应体、密钥、签名 URL 或 data URI。

## 源码路径

- `src/Multimedia/Madorin.AI.Zhipu.Image/`
- `src/Multimedia/Madorin.AI.Zhipu.Video/`
- 复用 `Madorin.AI.Multimedia.Core`，本批不修改公共库。

## 验证边界

按协议推进规则，本阶段不运行 `dotnet build`、`dotnet test`、AOT、发布脚本或真实 API。未验证账户权限、结果 URL 实际主机、重定向、图片 MIME、视频容器、轮询时延和取消终态。
