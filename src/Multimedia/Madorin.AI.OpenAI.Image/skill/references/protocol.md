# OpenAI Images API 接入

## 当前范围

仅实现同步文本生成图片，调用 `POST /images/generations`。已声明模型为 `gpt-image-1`、`gpt-image-1-mini`、`gpt-image-1.5`。`image-edit`、异步提交/查询/下载、附件生成、遮罩及部分成功暂不支持；统一附件结构仍可解析和过滤。

## 认证与端点

| 输入 | 含义 |
| --- | --- |
| `--api-key` | OpenAI API Key，作为 Bearer Token 发送；不得写入请求文件或输出。 |
| `--api-url` | HTTPS API 根地址，缺省 `https://api.openai.com/v1`；程序追加 `images/generations`。 |

## 请求字段

请求文件采用公共结构，`schema_version` 固定 `1.0`，`request_id` 可由 argv 提供；省略 `task` 默认 `text-to-image`，提示词必需。模型按显式 `--model` → 文件 `model` → 默认 `gpt-image-1` 选择，覆盖后再执行任务和参数校验；最终模型不在本技能清单中或任务不兼容时失败，不丢弃模型或回退。`request_id` 双来源必须相同，不能按模型覆盖规则替换。公共 Schema 只约束输入结构，模型约束在 metadata 的任务 Schema 中定义。

主程序跨 CLI 调用显式传入目标 `--model`；通用文件建议省略模型、调用 ID 和输出目录，保留任务、提示词、附件和生成参数。每次通过 `--request-id`、`--output-dir` 以及所需连接参数指定调用上下文，不修改原文件。文件模型继续作为兼容输入，默认模型仅在 argv 和文件均未提供时采用。`--try-run` 通过时，顶层 `model`、`effective_parameters.model` 和 `request_preview.model` 均为最终模型；`input_parameters.model` 仅回显生效的显式值。

`attachments` 默认空数组；本地引用先按请求文件目录和 cwd 授权根校验，然后逐项忽略并记录原索引。不支持的生成项仅记录字段路径，不回显值；未知根字段、未知执行字段及认证字段均拒绝。`execution` 接受 `output_dir`、`timeout`、`try_run`、`verbose`、`no_wait`、`output_format`；同步任务只接受 `no_wait=false`，协议输出只接受 `json`。

| CLI / 文件参数 | Provider 字段 | 说明 |
| --- | --- | --- |
| `prompt` | `prompt` | 原文透传，不翻译、不改写。 |
| `--model` / 根字段 `model` | `model` | 当前模型之一；命令行优先于文件，不同模型的参数按各自任务声明校验。 |
| `num-images` / `parameters.num_images` | `n` | 生成数量，范围 1-10。 |
| `size` / `parameters.size` | `size` | `auto`、`1024x1024`、`1536x1024`、`1024x1536`。 |
| `width`、`height` / `parameters.width`、`parameters.height` | `size` | 正整数像素，必须成对且精确组成支持的尺寸；与显式 size 不同则失败，记录 `dimensions_to_size`。 |
| `quality` / `parameters.quality` | `quality` | `auto`、`low`、`medium`、`high`。 |
| `provider-openai-output-format` / `provider_parameters.openai.output_format` | `output_format` | `png`、`jpeg`、`webp`，默认 `png`；供应商字段保留命名空间。 |
| 旧文件 `parameters.output_format` | `output_format` | 按 `image_format_to_openai` 等价转换到上述 Provider 字段，与显式目标冲突时报错。 |
| `output-format` / `execution.output_format` | 无 | 仅控制 stdout 协议，固定 `json`，不参与图片编码。 |

## 响应与产物

OpenAI 返回 `data[].b64_json`。CLI 验证容器完整、实际编码格式与完整像素解码后，以 `image_001.<format>` 序列落盘，输出相对路径、MIME、格式、字节数和实际宽高。损坏、截断、编码不符返回 `provider_error`。`x-request-id` 映射到 `provider_metadata.request_id`。输出目录限 cwd 授权根，已存在目标文件不覆盖。

## 错误映射

| Provider 情况 | 公共错误 |
| --- | --- |
| HTTP 401/403 | `authentication_failed` |
| HTTP 408/504 | `timeout` |
| HTTP 429 | `rate_limit_exceeded` |
| 网络/TLS 传输失败 | `network_error` |
| 其他非成功响应、无效响应 JSON / 图片 | `provider_error` |
| 本地参数/请求文件错误 | `invalid_parameter` |

密钥不进入响应。Provider 原始错误码可保留；原始响应体不输出。
