# Diff Re-review

## 复评范围

仅复评首轮发现对应的文档改动，没有重新审阅未改动章节。

## 已验证修复

- F-001：已修正为 `## 4` 下的 `### 4.1`～`### 4.9`，验收标准为 `####`；测试矩阵子项为 `#### 4.8.x`。
- F-002：已改为 PowerShell 可复制命令，并说明 CLI 全局 `--json` 位置需要以实际帮助为准。
- F-003：已明确桌面宿主优先走已连接 Runtime Client，CLI 仅保留独立 Runtime 场景；补充 Provider/凭据桥接和相关 Client/Server 文件。
- F-004：已明确 `Runtime.Contracts` 的 `SessionCompactionRequest`/`SessionCompactionResultDto` 跨进程传输，并要求 Gateway、Service、Chat/QuickChat 透传结果。
- F-005：已选择分模式缓存语义：Expert 可安全复用；Meeting/Work 不直接复用通用 Session compact cache，必须返回明确原因。
- F-006：已增加 MVP 参数边界，暂不把 `SegmentSize`/`RawTailSize` 转成 Token，`MaxDisplaySegments` 明确为 UI-only。
- F-007：已补充 Runtime/Workstation 的具体构建、测试项目和过滤器，并要求用 `--list-tests` 校正实际过滤器。
- F-008：已增加“现有实现/本轮新增/暂不改动”边界表。
- 复评新增问题：Server 层 `SessionCompactionResult` 不应直接跨 Client/协议边界；已改为 Contracts DTO 方案。

## 复评结果

- 未发现首轮问题的残留结构性缺陷。
- 目标文档仍为执行方案，进度保持 `0%`，没有把计划项误标为完成。
- Python 不可用，因此未运行 `detect_conventions.py`、`check_refs.py` 和自动回归脚本；已完成人工标题、路径和结构检查。
