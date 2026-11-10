# Kling Video CLI

技能名为 `skill-kling-video`。实现可灵 4.0 路径接口的文生视频和单首帧图生视频，复用 `Madorin.AI.Multimedia.Core`。

默认模型 `kling-4.0`，默认地址 `https://api-beijing.klingai.com`。模型原样放入路径，不改写到旧版 `/v1/videos/*`。不支持多关键帧、Omni、主体、AK/SK JWT 或静态模型目录。视频成功状态是 `succeeded`。官方未公布结果主机白名单；下载只接受 HTTPS 并校验 MP4。真实 API、AOT 和正式发布尚未验证。
