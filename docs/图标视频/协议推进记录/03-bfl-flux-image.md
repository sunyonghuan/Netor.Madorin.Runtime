# BFL FLUX Image CLI 接入记录

## 范围与依据

接入 BFL FLUX.2 Pro 纯文生图，源码项目 `src/Multimedia/Madorin.AI.Bfl.Image/`，技能 ID/程序名 `skill-bfl-image`。复用多媒体 Core 的 argv/请求文件处理、路径授权、调用生命周期、取消和图片产物验证；发布采用独立 `publish.json` 与 `publish.ps1`。

官方资料（核对日期 2026-11-10）：

- [FLUX.2 Pro API Reference](https://docs.bfl.ai/api-reference/models/generate-or-edit-an-image-with-flux2-%5Bpro%5D.md)：`POST /v1/flux-2-pro`、`x-key` 认证、Flux2Inputs 与异步提交响应。
- [Get Result](https://docs.bfl.ai/api-reference/utility/get-result.md)：轮询结果和状态枚举。
- [Integration Guide](https://docs.bfl.ai/api_integration/integration_guidelines.md)：必须使用提交返回的 `polling_url`；结果在 `delivery.*.bfl.ai`，约 10 分钟失效。
- [Errors](https://docs.bfl.ai/api_integration/errors.md)：HTTP 错误和任务状态语义。
- [官方 OpenAPI](https://api.bfl.ai/openapi.json)：当前公开路由及模型请求 Schema。

## 实现状态

- 已建立独立项目、发布清单/入口、技能资源、请求 Schema、Provider 映射和协议引用。
- `generate` 提交 FLUX.2 Pro，请求使用官方字段；默认等待同进程轮询终态并下载、验证后落盘。
- 使用原始 `polling_url`；不公开 query/download，metadata `supports_async=false`。`list-models` 保留命令但返回 Provider 不支持错误，不回退静态目录；metadata 不声明动态目录。
- 初版只支持文生图，不支持 input_image 编辑/参考附件、webhook 或动态模型目录。官方 OpenAPI 无通用 models 查询路由；不返回静态模型目录。
- FLUX.2 Pro 模型选择默认为 `flux-2-pro`，由固定 API 路由决定，不作本地白名单限制。

## 验证边界

本批严格未运行 `dotnet build`、`dotnet test`、AOT、发布脚本、假 Provider 测试或真实 API 调用。只完成源码落盘和官方文档核对；不能宣称编译通过或运行可用。后续统一验证需重点覆盖编译/源生成 JSON、取消与总预算、轮询状态转换、返回 URL 主机与重定向、图片下载容器验证、文件冲突、错误映射、metadata/list-models 行为及包结构。

## 待核实

- BFL 账户/组织是否对 FLUX.2 Pro 开通，实际服务端是否接受默认 `safety_tolerance=2`。
- delivery 主机模式与真实重定向链；当前实现按官方通配域名要求校验初始结果 URL，未验证 HttpClient 自动重定向链。
- 当前尺寸像素规则取自模型 OpenAPI，边界组合及平台行为待假 Provider/官方账户确认。
- list-models 因缺少官方通用模型目录 API 返回明确的 Provider 不支持错误；统一测试阶段需确认该行为与目录命令契约一致。



