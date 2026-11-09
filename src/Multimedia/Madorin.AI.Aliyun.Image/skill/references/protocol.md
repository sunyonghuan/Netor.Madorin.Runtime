# 阿里云百炼 Wan 2.6 文生图协议

官方依据（2026-11-09 核对）：
- https://help.aliyun.com/zh/model-studio/text-to-image-v2-api-reference
- https://help.aliyun.com/zh/model-studio/wan-image-generation-api-reference

## 范围与端点

| 项目 | 当前实现 |
| --- | --- |
| 模型默认值 | `wan2.6-t2i`；模型 ID 不做静态白名单校验 |
| API 根地址 | `https://dashscope.aliyuncs.com/api/v1`；允许调用方传地域 Workspace URL |
| 同步端点 | `POST /services/aigc/multimodal-generation/generation` |
| 认证 | `Authorization: Bearer <API Key>` |
| 任务 | `text-to-image`，同步等待 |
| 异步 | 不声明支持；异步提交/恢复后续批次处理 |

## 请求映射

请求外层为 `model`、`input`、可选 `parameters`。`input.messages` 只包含一条 `role=user` 消息，content 只包含一个 `{ "text": prompt }` 项。

| CLI/统一参数 | DashScope 字段 | 约束 |
| --- | --- | --- |
| `--prompt` / `prompt` | `input.messages[0].content[0].text` | 必需，原文映射 |
| `--model` / `model` | 根 `model` | 默认 `wan2.6-t2i`，按 argv → 文件 → 默认选择 |
| `--size` / `parameters.size` | `parameters.size` | `宽*高`，默认 `1280*1280`；总像素与比例限制由服务最终校验 |
| `--num-images` / `parameters.num_images` | `parameters.n` | 整数 1-4，默认 1 |
| `--negative-prompt` | `parameters.negative_prompt` | 字符串，缺省不发送 |
| `--provider-aliyun-prompt-extend` | `parameters.prompt_extend` | bool，官方默认 true |
| `--provider-aliyun-watermark` | `parameters.watermark` | bool，官方默认 false |
| `--provider-aliyun-seed` | `parameters.seed` | 整数 0 至 2147483647 |

本批不映射 `aspect_ratio`、`width`、`height` 到 `size`，不声明其为可转换参数，避免非等价尺寸推断。当前代码默认参数应由 Provider DTO 和 metadata 保持一致。

## 响应、URL 下载与产物

同步响应路径为 `output.choices[].message.content[]`。只接受 `type=image` 且 `image` 为绝对 HTTPS URL 的项。CLI 关闭自动重定向，最多跟随 3 次且每一跳都要求 HTTPS；下载使用流式读取，最大 50 MiB，仅接受 `image/png` 或 `image/jpeg`，随后由公共图片验证器完整解码并按检测格式以不覆盖的 `image_001` 起始序列落盘。只有全部请求图片都下载、验证和写入后才提交成功结果；失败时公共产物事务清理已写文件。

下载目标主机未限制为官方固定清单，因为服务返回的临时签名 URL 主机尚未从官方资料中核实；当前仅强制 HTTPS、禁止 URI 用户信息并限制跳转次数。总预算和取消清理行为仍待统一运行验证。

## 错误与异步边界

非 2xx 错误从 JSON 安全字段 `code` 映射公共错误；401/403、429、408/504 分别归一为认证、限流、超时错误。成功 HTTP 响应若根对象或 `output` 对象同时包含业务 `code` 与错误标识字段，也归一为 `provider_error`。错误消息不得回显原始响应体或签名 URL。

本批不发送 `X-DashScope-Async`，不实现 Wan 2.6 异步端点或 Wan 2.5 及以下旧版异步接口，不声明 `query`、`download`、`supports_async`。

## 历史调研差异

仓库 `docs/图标视频/参数统计/06-阿里云-通义万相.md` 的 `wanx-v1`、旧 `image-synthesis` 请求结构及旧模型表不作为本批实现依据。现行官方文档区分 `wan2.6-t2i` 文生图同步接口与 `wan2.6-image` 图像编辑/图文混排多模态接口。
