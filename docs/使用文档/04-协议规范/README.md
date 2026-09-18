# 协议规范总览

> Madorin.AI.Runtime 通信协议架构概述

本目录描述 Runtime 各层之间的通信协议，帮助第三方理解和实现兼容的组件。

## 协议架构

```
┌─────────────────────┐
│   宿主应用 (Host)    │  你的应用程序
└──────────┬──────────┘
           │
           │ ① Host ↔ Runtime 协议
           │    Named Pipe
           │    JSON 消息
           ↓
┌─────────────────────┐
│  Runtime Server     │  会话管理、执行编排
└──────────┬──────────┘
           │
           │ ② Runtime ↔ Provider 协议
           │    stdin/stdout
           │    长度前缀 JSON 帧
           ↓
┌─────────────────────┐
│  Provider Worker    │  模型适配器进程
└──────────┬──────────┘
           │
           │ ③ Provider ↔ AI 服务
           │    HTTPS
           │    厂商专有协议
           ↓
┌─────────────────────┐
│   AI 服务商 API      │  OpenAI/Anthropic/etc.
└─────────────────────┘
```

## 协议文档


| 文档                                            | 说明                        | 适用对象         |
| --------------------------------------------- | ------------------------- | ------------ |
| [Runtime 协议](./01-Runtime协议.md)               | Host ↔ Runtime 通信         | 宿主应用开发者      |
| [事件模型](./02-事件模型.md)                          | 事件流、消息格式                  | 宿主应用开发者      |
| [Session 与 Run 生命周期](./03-Session与Run生命周期.md) | 会话和运行管理                   | 宿主应用开发者      |
| [Provider 协议](./04-Provider协议.md)             | Runtime ↔ Provider Worker | Provider 开发者 |


## 协议层次

### 层 1：Host ↔ Runtime

**传输**：Named Pipe（Windows）/ Unix Domain Socket（Linux/macOS）  
**编码**：UTF-8 JSON 消息  
**模式**：请求-响应 + 事件流

**职责**：

- 认证与握手
- 会话创建与管理
- Run 启动与控制
- 事件流投递
- 工具目录管理
- 凭据更新

**关键特性**：

- 心跳保活
- 断线重连
- 事件重放
- 宿主租约

详见 [Runtime 协议](./01-Runtime协议.md)。

### 层 2：Runtime ↔ Provider Worker

**传输**：stdin/stdout（父子进程）  
**编码**：大端 UInt32 长度前缀 + UTF-8 JSON 帧  
**模式**：双向异步消息

**职责**：

- Worker 进程生命周期
- 模型调用请求
- 流式事件返回
- 调用取消

**关键特性**：

- 无状态 Worker（不保存会话）
- 按协议 slug 复用（每 Runtime 每协议最多一个 Worker）
- 多连接复用（BaseUrl/ApiKey 按调用传入）
- 语言无关实现

详见 [Provider 协议](./04-Provider协议.md)。

### 层 3：Provider ↔ AI 服务

**传输**：HTTPS  
**编码**：厂商专有格式（REST JSON、SSE 等）  
**模式**：厂商定义

这一层由各 Provider Worker 实现，不属于 Runtime 协议范畴。

## 核心概念

### Workspace

工作区，一个独立的数据隔离单元，通常对应一个项目目录。

- 每个 Workspace 有独立的 `.madorin/` 数据目录
- 每个 Workspace 由一个 Runtime 实例管理
- 通过 `WorkspaceWriteLock` 保证独占访问

### Runtime Instance

Runtime 实例，管理一个 Workspace 的所有会话和执行。

- 由 `madorin serve --instance <name>` 启动
- 通过 Named Pipe 暴露服务：`\\.\pipe\madorin-<instance>-<hash>`（Windows）
- 维护全局状态：活动 Session、Run、Provider Workers

### Session

会话，包含完整的对话历史和上下文。

- 唯一标识：`sessionId`（UUID）
- 包含多个 Run（按时间顺序）
- 持久化在 SQLite + JSONL
- 支持恢复和导出

### Run

单次运行，一个 Session 内的一轮执行。

- 唯一标识：`runId`（UUID）
- 从用户输入开始，到完成/失败/取消结束
- 产生一系列事件（Event）
- 可以被取消

### Selection

选择配置，指定 Provider、Model 和 Agent。

```csharp
public record NextTurnSelection
{
    public string? ProviderId { get; init; }
    public string? Model { get; init; }
    public string? AgentId { get; init; }
}
```

### Mode

运行模式，决定执行策略。

- **Expert**：深度分析和复杂决策
- **Meeting**：会议纪要、协作和信息整理
- **Work**：日常任务执行和流程编排

### Event

事件，Runtime 执行过程中产生的通知。

所有事件包装在 `RuntimeEventEnvelope`：

```csharp
public record RuntimeEventEnvelope(
    string RuntimeInstanceId,    // Runtime 实例 ID
    long Gsn,                     // 全局序列号
    string RunId,                 // 关联的 Run ID
    long RunSequence,             // Run 内序列号
    string MessageType,           // 事件类型
    DateTimeOffset Timestamp,     // 时间戳
    JsonElement Payload           // 事件负载
);
```

详见 [事件模型](./02-事件模型.md)。

## 认证与安全

### Host ↔ Runtime 认证

使用基于时间戳和密钥的握手协议：

1. Runtime 启动时生成 `HandshakeSecret`
2. Client 连接时发送 `hello` 消息，包含 `Timestamp` 和 `ProofHash`
3. Runtime 验证时间戳（允许时钟偏差）和哈希
4. 验证通过返回 `hello.ok`，失败返回 `hello.rejected`

### Runtime ↔ Provider 隔离

- Provider Worker 运行在独立进程
- Worker 不访问工作区文件系统（`.madorin/`）
- API Key 通过 IPC 帧传输，不经过命令行或环境变量
- stderr 日志自动脱敏，禁止输出完整 API Key

## 数据流

### 典型 Run 的数据流

```
Host                Runtime                Provider Worker         AI Service
  │                    │                         │                      │
  │──StartNewSession──>│                         │                      │
  │                    │                         │                      │
  │                    │───hello────────────-───>│                      │
  │                    │<──hello.ok──────────-───│                      │
  │                    │                         │                      │
  │                    │───invoke───────────────>│                      │
  │                    │                         │───HTTPS POST────────>│
  │                    │                         │<──SSE chunk──────────│
  │<──event(delta)─────│<──event────────-────────│                      │
  │<──event(delta)─────│<──event─────────-───────│<──SSE chunk──────────│
  │                    │                         │                      │
  │                    │<──event(completed)──-───│<──SSE close──────────│
  │<──event(completed)─│                         │                      │
  │                    │                         │                      │
```

### 工具调用流

```
Host                Runtime                Provider Worker         AI Service
  │                    │                         │                      │
  │                    │───invoke───────────────>│                      │
  │                    │                         │───HTTPS POST────────>│
  │                    │                         │<──tool_use───────────│
  │<──tool.call.request│<──event────────────-────│                      │
  │                    │                         │                      │
  │──tool.response────>│                         │                      │
  │                    │───invoke───────────────>│                      │
  │                    │                         │───HTTPS POST────────>│
  │                    │                         │<──text───────────────│
  │<──output.delta─────│<──event─────────-───────│                      │
  │                    │                         │                      │
```

## 幂等性

### Session 创建幂等

通过 `SessionIdempotencyKey` 保证：

- 相同 `SessionIdempotencyKey` 只创建一次 Session
- 重复请求返回已有 Session ID

### Run 创建幂等

通过 `RunIdempotencyKey` 保证：

- 相同 `RunIdempotencyKey` 只启动一次 Run
- 重复请求返回已有 Run ID

## 事件可靠性

### 事件投递

- 所有事件分配全局序列号（GSN）和 Run 内序列号
- Client 通过 `eventReplayCursor` 指定起始 GSN
- Runtime 保证事件有序投递

### 事件确认

- Client 定期调用 `AcknowledgeEventsAsync(lastConfirmedGsn)`
- Runtime 记录 Host 的确认游标
- 断线重连后从 `lastConfirmedGsn + 1` 重放

详见 [事件模型](./02-事件模型.md)。

## 协议版本

当前协议版本：**1**

### 版本协商

- Runtime 在 `hello.ok` 中返回协议版本
- Client 检查版本兼容性
- 不兼容时拒绝连接

### 版本演进

协议升级策略：

- **补丁**：新增可选字段，向后兼容
- **次要**：新增消息类型或事件类型，旧 Client 忽略
- **主要**：破坏性变更，需要升级 Client

## 错误处理

### Runtime 协议错误


| 错误码                    | 说明             |
| ---------------------- | -------------- |
| `AuthenticationFailed` | 认证失败           |
| `InstanceNotFound`     | Runtime 实例不存在  |
| `SessionNotFound`      | 会话不存在          |
| `RunNotFound`          | 运行不存在          |
| `ProviderNotFound`     | Provider 未配置   |
| `ProtocolUnsupported`  | Provider 协议不支持 |
| `InvalidRequest`       | 请求格式错误         |
| `InternalError`        | Runtime 内部错误   |


### Provider 协议错误


| 错误码                    | 说明          |
| ---------------------- | ----------- |
| `WorkerCrashed`        | Worker 进程崩溃 |
| `FrameTooLarge`        | 帧超过大小限制     |
| `InvokeFailed`         | 调用失败        |
| `ProviderWorkerExited` | Worker 异常退出 |
| `TimeoutExceeded`      | 超时          |


## 性能考虑

### Named Pipe 性能

- 单个 Runtime 实例支持多个并发 Host 连接
- 每个连接独立的事件流
- 建议单个应用使用一个连接

### Provider Worker 复用

- 每个协议 slug 最多一个 Worker 进程
- 同一 Worker 处理多个并发 `invoke`（按 `InvocationId` 路由）
- Worker 崩溃后按需重启

### 事件流优化

- 批量确认事件（避免每个事件一次 ACK）
- 使用 `IAsyncEnumerable` 流式处理
- 避免在事件处理中阻塞

## 扩展性

### 自定义消息类型

协议支持扩展：

- 新增消息类型使用命名空间前缀（如 `custom.my_event`）
- 未知消息类型应被忽略而不是报错

### 自定义 Provider

- 实现 Provider 协议即可接入任意模型服务
- 支持任意编程语言（只需能读写 stdin/stdout 和解析 JSON）

详见 [Provider 开发指南](../05-Provider开发指南/README.md)。

## 调试工具

### 协议抓包

查看 Runtime 日志：

```bash
madorin serve --log-level Debug
```

查看事件流：

```bash
madorin run --input "测试" --format jsonl
```

### 诊断命令

```bash
# 检查连接
madorin ctl status --instance main

# 列出活动 Session
madorin ctl sessions --instance main

# 列出活动 Run
madorin ctl runs --instance main
```

## 相关文档

- [Runtime 协议详细规范](./01-Runtime协议.md)
- [事件模型详细规范](./02-事件模型.md)
- [Session 与 Run 生命周期](./03-Session与Run生命周期.md)
- [Provider 协议详细规范](./04-Provider协议.md)
- [SDK 集成指南](../03-SDK集成指南.md)
- [Provider 开发指南](../05-Provider开发指南/README.md)

## 下一步

- **Host 开发者**：阅读 [Runtime 协议](./01-Runtime协议.md)
- **Provider 开发者**：阅读 [Provider 协议](./04-Provider协议.md)
- **理解事件**：阅读 [事件模型](./02-事件模型.md)
- **理解生命周期**：阅读 [Session 与 Run 生命周期](./03-Session与Run生命周期.md)

