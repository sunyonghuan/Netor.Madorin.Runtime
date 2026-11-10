# Zhipu Image CLI

技能名为 `skill-zhipu-image`。实现智谱 CogView 同步文生图，复用 `Madorin.AI.Multimedia.Core` 的请求、路径、取消、响应和图片产物设施。

默认模型 `cogview-4`，默认地址 `https://open.bigmodel.cn/api`。不支持图片编辑、参考附件、GLM-Image 异步接口、跨进程 query/download 或静态模型目录。官方未公布结果主机白名单；下载只接受 HTTPS 并校验图片容器。真实 API、AOT 和正式发布尚未验证。
