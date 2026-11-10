# Kling Image CLI

技能名为 `skill-kling-image`。实现可灵旧版图像生成接口的文生图提交、查询和下载，复用 `Madorin.AI.Multimedia.Core`。

默认模型 `kling-v3`，默认地址 `https://api-beijing.klingai.com`。不支持参考图、扩图、编辑、AK/SK JWT 或静态模型目录。图片成功状态是 `succeed`。官方未公布结果主机白名单；下载只接受 HTTPS 并校验图片容器。真实 API、AOT 和正式发布尚未验证。
