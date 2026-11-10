# Zhipu Video CLI

技能名为 `skill-zhipu-video`。实现智谱 CogVideoX-3 文生视频和单图生视频，包含同进程等待、跨进程 `query`/`download` 和 MP4 产物校验。

默认模型 `cogvideox-3`。查询使用官方 `GET /paas/v4/async-result/{id}`，任务上下文不保存 API 地址或密钥。首尾帧、Vidu 和静态模型目录不在本批。真实 API、AOT 和正式发布尚未验证。
