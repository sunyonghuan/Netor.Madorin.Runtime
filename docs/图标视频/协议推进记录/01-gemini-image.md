# Google Gemini 原生图片协议

## 当前状态

前序批次已完成源码初版和一次代码逻辑推演。本轮不重跑测试，统一测试阶段必须重新覆盖。

## 已有实现

- 源码：`src/Multimedia/Madorin.AI.Gemini.Image/`
- 原生请求：`models/{model}:generateContent`
- 原生响应：`candidates[].content.parts[].inlineData`
- 模型目录：`models` 列表归一化
- 离线推演：version、metadata、try-run、模型目录假服务、成功图片产物曾验证

## 统一测试待办

- 完整分析器构建；前序曾受公共库 `MediaToolProcess.cs` 的 CA2016 阻断。
- 严格 Schema、metadata、argv/文件合并和 `num_images` 边界。
- Gemini 请求体、aspect ratio 映射、模型 ID 前缀归一化。
- inlineData 损坏图片、MIME、下载/落盘、取消和文件冲突。
- 真实 API、AOT、跨平台发布和正式技能包结构。
