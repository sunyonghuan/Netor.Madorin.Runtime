# 执行步骤 04：Provider Worker 双向 Blob 管道

> 状态：待执行 · 0%。前置：[01 P0](./01-P0-工具结果附件映射与上下文保护.md) 和 [02 Blob 生命周期](./02-附件工具与Blob生命周期.md)。

## 1. 目标与协议边界

生产路线通过 10 个隔离 Worker 运行 Provider。给每个 invocation 提供只能访问此请求附件的 Blob read capability，给 Provider 回包提供流式 Blob write sink。传二进制时使用有界 chunk frames，不把整个内容加入 `InvokeFrame.Request` JSON。

Worker 当前 IPC Version 1、`MaxFrameLength=16 MiB`、无 Blob 读写帧；在此步骤设计并冻结内部 Worker protocol 新版及 CLI/Worker 一起发布策略。

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
