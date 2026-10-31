# 执行步骤 04：Provider Worker 双向 Blob 管道

> 状态：输入子任务实施中，输出写回未开始。用户在真实测试后明确要求继续接通 Worker Resolver，允许先实施此断点；[02 Blob 生命周期](./02-附件工具与Blob生命周期.md)、03 授权剩余门禁保留，不视为已完成。

## 1. 目标与协议边界

生产路线通过 10 个隔离 Worker 运行 Provider。给每个 invocation 提供只能访问此请求附件的 Blob read capability，给 Provider 回包提供流式 Blob write sink。传二进制时使用有界 chunk frames，不把整个内容加入 `InvokeFrame.Request` JSON。

改造前 Worker IPC Version 1、`MaxFrameLength=16 MiB`、无 Blob 读写帧；本批实现输入读取并升级 IPC 2，CLI/Worker 必须一起构建使用，正式发布/AOT 仍待验证。

### 本批冻结的输入协议

- IPC 升级为 2，旧 CLI/Worker 握手拒绝混用；Invoke 新增随机 Generation 与独立的 SDK 附件缓冲上限。
- `blob.read`：InvocationId、Generation、TransferId、BlobReference、Offset、Count；`blob.chunk`：同一关联信息、Offset、Data、EndOfBlob、可选有界错误。Data 仅是最多 128 KiB 的分块，不写入 Invoke 或普通 prompt，JSON 编码后 chunk 帧上限 256 KiB。
- Runtime 的读取委托只存在进程内（JsonIgnore），来自已有 Blob Store。每次 Invoke 递归收集该请求中已有引用为白名单，完整 descriptor 匹配，不接收 Worker 本地路径。
- 每次调用至多一个读取处于处理状态；Worker 逐块请求、逐块等待，主 dispatch 持续读取响应。默认 SDK 完整缓冲上限 64 MiB，再受 Runtime 单 Blob 配额收紧；本批不声称 SDK 常量内存。
- 本批仅输入，不新增无实现的 output write frame。输出 sink、恢复/配额/AOT/全十家发布仍按后续门禁继续。
- `HistoryMessageCount` 原为请求 JsonIgnore 属性，本批作为 Invoke 控制字段显式传递；Worker 复用现有 ProviderInvocationBoundary 区分历史与当前输入。仅对历史 reasoning 应用原兼容投影，不放宽本轮 reasoning 校验。Cancel 增加 Generation，旧代次取消不得中断同 InvocationId 的新轮。

## 2. 帧协议与授权

- 建议帧：`read.request/read.chunk/read.complete`、`write.begin/write.chunk/write.complete/write.abort`。所有帧使用 InvocationId + TransferId 相关联；命名与 schema 在 implementation 前以 source-generated protocol snapshot 冻结。
- 每帧元数据和数据部分上限为 256 KiB 建议值，序列化 byte[] 的 Base64 膨胀也须计入最大 frame。读写每个方向限制并发 transfer、总字节数和 buffer 数量。
- Runtime 在调用开始时建立 input Blob allowlist，内容只来自已授权的当前 provider request/history/工具结果引用。Worker 不能遍历磁盘、按猜测 BlobId 读取别的 Run 或调用任意 Runtime 文件路径。
- Output sink 绑定当前 Provider + InvocationId。Worker 声明 MIME/名称/长度不能覆盖 Runtime 校验；Runtime 逐块限额、计算长度和 SHA-256，Complete 验证后才下发 BlobReference/输出事件。
- Worker 写 Output Blob 的能力和读取 Input Blob 的能力必须不同向、不同 token/TransferId；输出接口不能变成远端任意文件写入。
- Worker 配置的 API Key 不得放入 Blob frame、日志或异常。stdout 仍只走 IPC 帧，stderr 脱敏。
- 取消、超时、进程崩溃、客户端断开需终止 active transfers、abort staging、释放读句柄和租约；临时 uploadId 不可在重启后当有效附件复用。

## 3. 并发和背压

1. ProviderWorkerHost 主读循环不能在处理 Invoke 时等待它自己消费的 Blob 响应，避免 IPC deadlock；拆分 frame dispatch 与各 invocation 的 completion channel。
2. 同一 worker 同时支持多 invocation 时按 invocation 隔离 Resolver/Sink，禁止把 per-call resolver 保存到 worker 全局 adapter 字段。
3. Frame writer 单点串行化，防止事件和二进制帧 byte interleave；读端限制 outstanding read requests 和 channel capacity，slow consumer 自然背压。
4. 每次 Invoke/多轮续调有 RequestGeneration，旧一轮迟到的数据不能被下一轮/下一 Session 接受。
5. 超过 MaxInlineContentBytes 的输入和所有附件内容均通过 Blob 通道。不能为了跨 IPC 继续使用当前 16 MiB JSON frame 限额。

## 4. 十个 Worker 装配

逐项修改并验收 `OpenAI.Worker`、`Anthropic.Worker`、`OpenAICompatible.Worker`、`AzureOpenAI.Worker`、`Aliyun.Worker`、`Gemini.Worker`、`GLM.Worker`、`Ollama.Worker`、`Kimi.Worker`、`DeepSeek.Worker`。

每个 `BuildAdapter` 创建时应取得当前 invocation transport context，通过新的 Worker adapter factory/AsyncLocal-free 明确参数传入 resolver 和 sink。不要复制十套不同协议桥；如能安全共用工厂，可以共用基础设施，但每个 Worker 都要有装配测试。

## 5. 测试与发布门禁

- 小附件/多 chunk/空流/最终 chunk 长度准确、SHA/MIME 一致。
- 对同一哈希重复读、不同 Run 引用读、伪造 BlobId、无授权引用、过期引用、越界 offset、假长度/哈希分别验证。
- 慢速消费者验证有界 buffer 与背压；观察 managed memory 不随附件大小全量增长，除非某 SDK 被明确记录只能 buffer 且在独立配额内。
- Cancellation、timeout、Worker kill、IPC half-close、stale response、并发十个请求均完成清理且不串租户/Key/Session。
- JSON frame 编码后仍低于单帧上限；传大于 16 MiB 的附件不能让 Invoke frame 变大。
- CLI 与 Worker IPC 版本不兼容时拒绝握手；Windows 发布包的 Worker 清单、Source Generation 和 AOT 对齐。

## 6. 完成记录

记录 Worker IPC 版本、frame/transfer 限额、测试 payload 大小、峰值内存、取消清理和十个 Worker 装配证明。未实际接通的 Provider 不得进入后续“全 Provider 真实链路已通”状态。

2026-10-31 前置验证：重新构建的 DeepSeek、OpenAI、Anthropic、Kimi Worker 均在真实 CLI attach 工具成功后发生本地 InvalidOperationException；当前 BuildAdapter 无 invocation-scoped Resolver 装配，尚未执行本步骤，不归因于厂商拒图。Aliyun 则先被 reasoning 历史检查拦截。完整证据见[CLI 与真实 Provider 附件验证](./验证记录-20261031-CLI与真实Provider附件.md)。

### 输入实现与验证（2026-10-31）

- 已接通 `RuntimeProviderRequest.OpenBlobRead`（JsonIgnore）→ Runtime 进程内 `WorkerBlobInputLease` → `blob.read/chunk` → `WorkerInputBlobResolver` → Provider Adapter。委托只从现有 Blob Store 注入，Invoke 中没有文件字节、路径、Base64 或委托。
- 十个 Worker 的 BuildAdapter 均显式接收本次调用的 Resolver；旧单参数 RunAsync 仅作 API 兼容入口，生产十个 Worker 统一用新入口。per-call Resolver 不存全局，不用 AsyncLocal。
- 白名单递归包含本次请求的顶层/嵌套工具 Blob，descriptor（包含 scope/hash/期限）精确匹配；generation/offset/count/配额检查后才开流。每次读取 128 KiB，chunk 编码上限 256 KiB，完整 SDK 缓冲总量默认最多 64 MiB，Runtime 单 Blob 限制更小时收紧。
- 每个调用独立流和取消令牌，主 dispatch 不等待自己消费的 Blob 响应。调用结束/取消移除授权入口并释放读取流，旧代次响应/取消不进入新轮。
- `WorkerBlobInputTests` 使用真实十个 Worker 子进程和 Fake HTTP：每家 300,001 字节多 chunk 图片工具结果均在最终原生字段解码得到原字节；不从共用 Adapter 测试外推十家装配。
- 安全/边界：伪造 scope/hash、旧 generation、越界 offset/count、配额反例在开流前失败；错误实际哈希在 HTTP 前失败；空流、17 MiB（超过旧 Invoke 单帧）读取成功且 Invoke JSON 小于 3 KiB；取消阻断 pending read、释放流并能继续下一调用。十个并发调用各复用相同 InvocationId 两轮，20 次请求没有串附件。
- Debug Worker 联合测试 45 通过；最终 Release 附件/Worker/工具联合测试 153 通过，Protocol 49 通过，BlobTransfer E2E 5 通过，构建零警告/错误。TRX：`worker-blob-final-release.trx`、`worker-blob-integration.trx`。
- 真实 CLI：DeepSeek `deepseek-v4-flash`、axis-code `gpt-5.6-sol`、axis-code `claude-opus-4-6`、Kimi `kimi-k3.1-pro`、Aliyun `qwen3.8-max` 五条路线均完成一次 attach，返回图片可见；5 条 history 都保留 146 字节元数据+11,297 字节 Blob 引用、哈希一致、staging 0。Aliyun 第一轮超时，补齐历史边界传递和 Worker 历史投影后重跑成功，不隐去第一次记录。证据目录 `obj/worker-blob-cli-20261031-164403`。

### 尚未关闭的门禁

- 输出 Blob write sink/frames 未实现；本步骤仍不能标记双向完成。
- 非图片 codec、SDK 峰值内存采样、Worker kill/half-close 故障矩阵、恶意帧压力与正式 AOT 发布/manifest 仍待完成；不宣称全格式或全厂商真实输出通过。
- 本批真实 CLI 证明 PNG 输入主业务链路；02 的元数据归属/保留/Invocation 配额、03 的外部 Grant 专项验收仍保留，未扩大文件授权。
