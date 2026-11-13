# Aliyun Video CLI

技能名为 `skill-aliyun-video`。实现阿里云百炼万相 2.7 文生视频和单首帧图生视频，包含同进程等待、跨进程 `query`/`download` 和 MP4 产物校验。

默认文生视频模型 `wan2.7-t2v`，图生视频模型 `wan2.7-i2v-2026-04-25`。提交必须带 `X-DashScope-Async: enable`，查询使用官方 `GET /api/v1/tasks/{task_id}`。不传音频时由模型自动配音。首尾帧、视频续写、旧版 2.6 及以下协议和静态模型目录不在本批。真实 API、AOT 和正式发布尚未验证。
