# 阿里云百炼 Wan 2.6 文生图协议

官方依据（2026-11-09 核对）：
- https://help.aliyun.com/zh/model-studio/text-to-image-v2-api-reference
- https://help.aliyun.com/zh/model-studio/wan-image-generation-api-reference
- https://help.aliyun.com/zh/model-studio/base-url

## 范围与端点

| 项目 | 官方协议与当前实现 |
| --- | --- |
| 模型默认值 | `wan2.6-t2i`；模型 ID 不做静态白名单校验 |
| 默认 API 根地址 | 北京 `https://dashscope.aliyuncs.com/api/v1` 仍可用；官方建议迁移 Workspace 专属域名。北京、新加坡、美东 Key 与域名按地域匹配 |
| 同步端点 | `POST /services/aigc/multimodal-generation/generation` |
| 认证 | `Authorization: Bearer <API Key>` |
| 任务 | `text-to-image`，同步等待 |
| 异步 | 官方另有新协议端点，但本实现不声明支持、不接受恢复 |
| 模型目录 | 官方文生图 API 参考未定义通用 `/models` 目录接口；当前 `list-models` 路径不是已核实的官方端点 |

## 请求映射

## 请求映射

请求外层为 `model`、`input`、可选 `parameters`。`input.messages` 只包含一条 `role=user` 消息，content 只包含一个 `{ "text": prompt }` 项。

| CLI/统一参数 | DashScope 字段 | 官方约束与实现 |
| `--num-images` / `parameters.n` | `parameters.n` | 整数 1-4，官方默认 4；实现默认已同步为 4 |
| `--prompt` / `prompt` | `input.messages[0].content[0].text` | 必需，最多 2100 字符；实现原文传递，不模拟服务端截断 |
| `--model` / `model` | 根 `model` | 默认 `wan2.6-t2i`，按 argv → 文件 → 默认选择 |
| `--size` / `parameters.size` | `parameters.size` | 默认为 `1280*1280`；总像素 [1280², 1440²]、宽高比 [1:4, 4:1]。推荐 `1280*1280`、`1104*1472`、`1472*1104`、`960*1696`、`1696*960`；`768*2700` 也满足规则 |
| `--num-images` / `parameters.n` | `parameters.n` | 整数 1-4，官方默认 4；实现默认已同步为 4 |
| `--negative-prompt` | `parameters.negative_prompt` | 最多 500 字符；实现传递原值，不模拟服务端截断 |
| `--provider-aliyun-prompt-extend` | `parameters.prompt_extend` | bool，默认 true；V2 文生图参数，不是本技能排除的图像编辑专属字段 |
| `--provider-aliyun-watermark` | `parameters.watermark` | bool，官方默认 false |
| `--provider-aliyun-seed` | `parameters.seed` | 整数 0 至 2147483647 |

尺寸 Schema/CLI 采用推荐值枚举，不覆盖官方连续尺寸规则，因此合法尺寸可能被本地拒绝；这是当前已知协议缺口，仍需调整为总像素及比例校验。

## 响应、URL 下载与产物

同步成功响应为 `output.choices[].message.content[]`，图片 URL 格式为 PNG；官方示例主机为 `dashscope-result-bj.oss-cn-beijing.aliyuncs.com`，结果链接有效期 24 小时。实现只接受 `type=image` 且 `image` 为绝对 HTTPS URL 的项。CLI 关闭自动重定向，最多跟随 3 次且每一跳都要求 HTTPS；下载使用流式读取，最大 50 MiB，仅接受 `image/png` 或 `image/jpeg`，随后由公共图片验证器完整解码并按检测格式以不覆盖的 `image_001` 起始序列落盘。

官方文档给出北京 OSS 示例主机，但未提供完整区域主机清单；实现暂未按主机白名单限制，也未将响应 Content-Type 限为 PNG（当前允许 JPEG），这些属于待收敛项。总预算和取消清理行为仍待统一运行验证。

## 错误与异步边界

非 2xx 错误使用 JSON 字段 `code`、`message`、`request_id`；实现读取错误码并隐藏原始错误正文。HTTP 状态到公共错误码的分类属于本地归一策略，不是阿里云状态码映射的官方承诺。

本批不发送 `X-DashScope-Async`，不实现 Wan 2.6 异步端点或 Wan 2.5 及以下旧版异步接口，不声明 `query`、`download`、`supports_async`。

## 历史调研差异

仓库 `docs/图标视频/参数统计/06-阿里云-通义万相.md` 的 `wanx-v1`、旧 `image-synthesis` 请求结构及旧模型表不作为本批实现依据。现行官方文档区分 `wan2.6-t2i` 文生图同步接口与 `wan2.6-image` 图像编辑/图文混排多模态接口。
