# BFL FLUX 协议映射

核对日期：2026-11-10。官方依据：

- https://docs.bfl.ai/api-reference/models/generate-or-edit-an-image-with-flux2-%5Bpro%5D.md
- https://docs.bfl.ai/api-reference/utility/get-result.md
- https://docs.bfl.ai/api_integration/integration_guidelines.md
- https://docs.bfl.ai/api_integration/errors.md
- https://api.bfl.ai/openapi.json

## 初版范围

| 项目 | 实现 |
| --- | --- |
| 服务端点 | `POST https://api.bfl.ai/v1/flux-2-pro` |
| 认证 | `x-key: <API key>`；凭证只来自 argv |
| 任务 | `text-to-image`；默认模型标识 `flux-2-pro` 用于本地选择，BFL 路由由端点决定 |
| 请求 | `prompt` 必填；支持 `disable_pup`、`seed`、成对 `width`/`height`、`safety_tolerance` 0 至 5、`output_format` jpeg/png/webp |
| 提交响应 | 必须包含 `id` 和 `polling_url`；轮询使用原始 URL |
| 轮询 | 同进程内轮询 Pending/Reasoning/Generating，Ready 进入下载；其他终态失败 |
| 产物 | 只接受 `delivery.*.bfl.ai` HTTPS URL；下载后验证图片签名与 MIME，再写入输出目录 |
| 异步声明 | `supports_async=false`；不提供 query/download，避免在缺少受限上下文时伪造恢复能力 |
| 附件 | 本批拒绝，未实现编辑/参考图/多图输入 |
| 模型目录 | metadata 声明不支持动态目录；官方未提供通用 models 端点，`list-models` 返回明确 provider_error，不提供静态回退 |

## 官方约束

FLUX.2 Pro 的 OpenAPI 请求字段包括 prompt、disable_pup、input_image 至 input_image_8、seed、width、height、safety_tolerance、output_format 和 webhook 字段。本批仅实现不含输入图片和 webhook 的文生图子集。官方说明 `polling_url` 必须原样使用；delivery 图片 URL 为临时地址，约 10 分钟过期。

官方 HTTP 错误包括 400、402、403、422、429、500、503；代码将认证、余额、限流和超时映射为公共错误，其他响应归一为 provider_error，不回显原始响应体。

## 未验证项

已完成离线构建、metadata/version/try-run、参数错误和 list-models 参数校验验证；未进行测试项目、AOT 发布、正式发布、假 Provider 端到端子进程验证或真实 API 调用。尚未验证 BFL 实际账户对 FLUX.2 Pro 的访问、具体 delivery 主机集合、重定向行为、图片 MIME 与容器一致性、轮询预算/取消边界以及服务端对 width/height 和安全容忍度的实际限制。



