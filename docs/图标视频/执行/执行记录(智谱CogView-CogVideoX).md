# 智谱 CogView / CogVideoX 执行记录

日期：2026-11-10。依据 [执行计划](../策划/执行计划(智谱CogView-CogVideoX).md) 和 [协议记录](../协议推进记录/04-zhipu-cogview-cogvideox.md)。

## 已完成

- P0：核对现行智谱文档，确认参考文档中的同步视频响应、`cogview-3-plus`、`n` 和 `resolution` 档位失效。
- 决定拆成 `skill-zhipu-image` 与 `skill-zhipu-video`，因为图片同步且 CogView 没有公开异步恢复，视频可用官方任务 ID 查询。
- 纳入文生图、文生视频和单图生视频。首尾帧官方存在，本批明确拒绝。
- 落盘两个技能项目、metadata、请求映射、try-run、Provider 调用和技能源文件。
- `list-models` 返回 Provider 不支持错误，不写静态模型清单。

## 未执行

按协议推进记录的统一测试约定，本阶段没有运行构建、测试、AOT、发布脚本或真实 API。因此不能宣称编译通过或运行可用。

结果 URL 主机、重定向、账户权限、视频容器和取消终态仍未验证。
