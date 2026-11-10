# 智谱 CogView 图片协议

核对日期：2026-11-10。

- https://docs.bigmodel.cn/cn/guide/models/image-generation/cogview-4.md
- https://docs.bigmodel.cn/api-reference/模型-api/图像生成.md
- https://docs.bigmodel.cn/cn/api/api-code.md
- https://docs.bigmodel.cn/cn/guide/develop/http/introduction.md

## 映射

| 项目 | 实现 |
| --- | --- |
| 端点 | `POST /paas/v4/images/generations` |
| 认证 | `Authorization: Bearer`，只来自 argv |
| 默认模型 | `cogview-4`，其他模型 ID 原样提交 |
| 尺寸 | 缺省 `1024x1024`；边长 512–2048、16 倍数、像素积不超过 2^21 |
| 质量 | `standard` 或 `hd`，缺省 `standard` |
| 响应 | `data[].url`；当前契约只接受一张图 |
| 下载 | HTTPS、无用户信息、最多 3 次手动重定向；官方未给出主机白名单 |
| 异步 | `supports_async=false`。异步图片端点只支持 GLM-Image，本批不实现 |
| 模型目录 | 无官方通用端点，`list-models` 返回 `provider_error` |

参考文档中的 `n`、`response_format`、`user` 和 `cogview-3-plus` 与现行 OpenAPI 不符，不发送。
