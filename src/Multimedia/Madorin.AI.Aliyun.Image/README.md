# Aliyun Image CLI

技能名为 `skill-aliyun-image`，当前实现阿里云百炼 Wan 2.6 同步文生图。统一契约以设计方案为准，具体字段和未测试边界见 `skill/` 与 `docs/图标视频/协议推进记录/02-aliyun-wan-image.md`。

Provider 使用 `POST /api/v1/services/aigc/multimodal-generation/generation`、Bearer API Key、`input.messages` 请求和 `output.choices[].message.content[].image` 临时 PNG URL 响应。CLI 下载并完整验证图片后才报告产物。

本批不支持旧版 Wan 异步协议、公开 query/download、图像编辑、图文混排、参考附件或 Runtime 集成。视频由独立的 `skill-aliyun-video` 接入，不在本项目实现。按统一安排，本批源码完成后再与其他协议一起构建、测试和发布验证。
