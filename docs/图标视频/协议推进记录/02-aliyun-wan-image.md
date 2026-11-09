# 阿里云百炼 Wan 2.6 文生图协议

## 批次状态

- 状态：源码初版处理中，未运行构建、测试、发布或真实 API。
- 记录日期：2026-11-09。
- 实现范围：仅同步 HTTP 文生图，目标模型默认 `wan2.6-t2i`。
- 不在本批：Wan 2.5 及以下旧版异步协议、Wan 2.6 异步恢复、图像编辑、图文混排、流式输出、多图附件、Runtime 集成。

## 官方核对

官方文档：

- [万相-文生图 V2 API 参考](https://help.aliyun.com/zh/model-studio/text-to-image-v2-api-reference)
- [万相-图像生成与编辑 2.6 API 参考](https://help.aliyun.com/zh/model-studio/wan-image-generation-api-reference)

核对日期：2026-11-09。

官方当前信息与历史调研存在差异：

- 当前文生图推荐模型为 `wan2.6-t2i`，不是历史资料中的 `wanx-v1`。
- Wan 2.6 同步请求使用 `POST /api/v1/services/aigc/multimodal-generation/generation`。
- 默认北京地域域名仍可使用 `https://dashscope.aliyuncs.com`；官方同时建议使用带 Workspace ID 的地域业务域名。`--api-url` 保留为调用方地址，不在 CLI 内猜测 Workspace ID。
- 请求使用 `model`、`input.messages`、`parameters`；提示词位于单轮 user 消息的单个 text content。
- 同步成功响应的图片位于 `output.choices[].message.content[]` 的 `type=image` 项，值为临时 PNG URL；URL 有效期约 24 小时，CLI 必须下载后验证并只列出本地产物。
- Wan 2.6 的 HTTP 异步协议使用另一端点和 `X-DashScope-Async: enable`，本批不声明公开异步能力。

## 本批请求映射

| 公共字段 | DashScope 字段 | 规则 |
| --- | --- | --- |
| `model` | 根 `model` | argv → 请求文件 → 默认 `wan2.6-t2i`；ID 非空且无控制字符即原样提交，不设静态白名单 |
| `prompt` | `input.messages[0].content[0].text` | 单轮 user 消息；保持原文，不翻译、不截断 |
| `negative_prompt` | `parameters.negative_prompt` | 原生支持；缺省不发送 |
| `size` | `parameters.size` | `宽*高` 字符串；当前只做结构校验和原样映射，模型限制交由服务确认 |
| `num_images` | `parameters.n` | 范围 1-4；本批支持直接映射 |
| `prompt_extend` | `parameters.prompt_extend` | 布尔值，原生映射 |
| `watermark` | `parameters.watermark` | 布尔值，原生映射 |
| `seed` | `parameters.seed` | 非负整数，原生映射 |
| API Key | `Authorization: Bearer <key>` | 只从 argv 读取，不写请求文件、不进入日志和预览值 |

当前不将公共 `aspect_ratio` 猜测转换为 `size`；只有显式 `size` 才进入 Provider 请求。宽高与 size 的可靠等价转换待后续协议映射统一确认。

## 响应与产物

1. HTTP 2xx 后解析 `output.choices`。
3. 使用同一调用预算下载 URL，关闭自动重定向，最多跟随 3 次且每跳要求 HTTPS；流式下载上限 50 MiB，仅接受 PNG/JPEG 响应类型。
4. 下载内容交给公共图片验证器，验证真实格式、容器和像素解码。
5. 使用 `image_001.png` 等不覆盖文件名落盘；只有完整验证并写入的文件进入响应。
6. 缺少图片项、非 HTTPS URL、下载失败、损坏图片或结果数量不符均归一为 Provider/产物错误；公共产物事务清理已写文件。

## 模型目录

`list-models` 访问调用方提供的 DashScope 模型目录地址；本批不写死 Wan 模型清单，不因目录不可用而静态回退。服务若只返回模型 ID，任务能力保持未知；本地 metadata 只声明已实现的 `text-to-image`。

## 实际源码路径

- `src/Multimedia/Madorin.AI.Aliyun.Image/`：CLI、请求合并、DashScope Provider、metadata 和技能资源。
- 当前 Provider 映射：`model`、`input.messages`、`parameters`、同步 `output.choices` 图片 URL 下载。
- `src/Multimedia/Madorin.AI.Multimedia.Core/`：复用公共生命周期、路径授权、图片验证和产物落盘；本批未修改。

## 已落盘源码摘要

- `Provider/AliyunImageRequest.cs`：`model`、单轮 `input.messages`、`parameters` 请求 DTO。
- `Provider/AliyunImageClient.cs`：同步提交、`output.choices[].message.content[].image` URL 提取、下载和公共图片产物写入。
- `Requests/ImageGenerationOptionsFactory.cs`：文件/argv 合并、size/n/negative_prompt/provider 参数和路径边界。
- `Metadata/AliyunImageMetadata.cs`、`Metadata/AliyunImageSchemas.cs`：Wan 2.6 文生图能力声明。
- `skill/`：技能说明、协议引用、Schema、调用脚本。

## 待确认协议点

- `list-models` 的 DashScope 官方模型目录入口尚未核实，当前实现沿用技能接口需要的目录抽象；统一测试前必须用官方可用入口或明确记录不适用。
- 结果 URL 的官方允许主机尚未核实；当前源码已实现 HTTPS、禁止用户信息、最多 3 次重定向、50 MiB 流式大小上限和 PNG/JPEG Content-Type 检查，但仍需统一运行验证。
- `provider_parameters.aliyun` 已按公共契约合并到有效 `parameters`；与同名通用参数重复或包含其他 Provider 命名空间时返回 `invalid_parameter`，不再静默丢弃。
- `wan2.6-t2i` 的 size 可选范围按官方文档记录为模型/地域相关，当前初版只保留常见推荐尺寸，未宣称完整模型矩阵。
- HTTP 2xx 的根对象或 `output` 对象若带有业务错误码和错误标识，也归一为 `provider_error`；错误体内容不回显。

## 未测试清单

以下项目按用户要求留到全部协议完成后统一执行：

- C# 构建、分析器构建、Native AOT 发布和跨 RID 发布。
- `metadata`、`version`、`list-models`、`generate` 的 CLI 子进程回归。
- 文件/argv 合并、覆盖顺序、重复选项、严格 JSON、路径授权和秘密脱敏。
- try-run 不联网、不读取媒体、不下载 URL、不建目录、不写文件。
- 假 Provider 的请求 JSON、Authorization、响应 URL 下载、图片完整性和文件冲突。
- HTTP 401/403、429、超时、服务错误、空 choices、过期 URL、损坏图片和部分结果。
- 取消、总预算、重定向限制和临时文件清理。
- 真实 DashScope API、模型可用性、额度、地域域名和正式技能包发布。

## 后续任务

- 完成源码项目及技能资源后，补充本记录中的实际源码路径和字段映射摘要。
- 建立 `03-bfl-flux.md` 前，先重新核对 BFL 当前官方端点、模型 ID、轮询 URL 和结果下载协议。
- 全部协议源码落盘后，按 `协议推进记录/README.md` 的统一测试门禁执行，不把本批文档核对当作测试通过。



