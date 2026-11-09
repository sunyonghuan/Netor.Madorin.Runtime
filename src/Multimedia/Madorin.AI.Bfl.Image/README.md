# BFL Image CLI

技能名为 `skill-bfl-image`，源码位于 `src/Multimedia/Madorin.AI.Bfl.Image/`。实现 BFL FLUX.2 Pro 文生图的同进程提交、轮询、图片下载和产物验证，复用 `Madorin.AI.Multimedia.Core` 的公共请求、路径、取消、响应和产物设施。

本批不支持图片编辑、参考附件、webhook、跨进程 query/download，也未进行真实 API、AOT 发布或正式技能发布验证。已完成离线构建、metadata、version、try-run、参数错误和 list-models 参数校验验证。使用 `publish.ps1 -Rid <RID>` 进入统一多媒体发布流程。
