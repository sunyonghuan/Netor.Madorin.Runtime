# Runtime 协议

> Host ↔ Runtime 通信协议详细规范

本文档描述宿主应用与 Runtime Server 之间的通信协议，供 SDK 开发者和集成方参考。

## 概述

Host 通过 **Named Pipe**（Windows）或 **Unix Domain Socket**（Linux/macOS）与 Runtime 通信。

### 传输层

- **Windows**：`\\.\pipe\madorin-<instance>-<hash>`
- **Linux/macOS**：`/tmp/madorin-<instance>-<hash>.sock`

### 消息格式

Runtime 使用 **JSON-RPC 2.0** 协议进行控制通道通信，所有消息都是 UTF-8 编码的 JSON 对象，通过换行符（`\n`）分隔。

**请求格式：**
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "run.new_session",
  "params": { ... }
}
```

**响应格式：**
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": { ... }
}
```

**错误格式：**
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "error": {
    "code": -32600,
    "message": "Invalid request",
    "data": { ... }
  }
}
```

## 连接流程

### 1. 建立控制通道连接

Host 连接到 Runtime 的控制通道 Named Pipe：

```csharp
var pipeClient = new NamedPipeClientStream(
    serverName: ".",
    pipeName: "madorin-main-abc123",
    direction: PipeDirection.InOut,
    options: PipeOptions.Asynchronous
);

await pipeClient.ConnectAsync(timeout: 10000);
```

### 2. 三次握手认证

Runtime 使用基于 HMAC 的三次握手协议进行认证。

**步骤 1：Host → Runtime（ClientHello）**

```json
{
  "hostInstanceId": "host-abc",
  "nonceC": "client-random-nonce",
  "timestampUnixMilliseconds": 1726657200000
}
```

**步骤 2：Runtime → Host（ServerHello）**

```json
{
  "runtimeInstanceId": "runtime-xyz",
  "nonceS": "server-random-nonce",
  "runtimeResponse": "hmac-sha256(secret, nonceC + nonceS + hostInstanceId)"
}
```

**步骤 3：Host → Runtime（ClientConfirmation）**

```json
{
  "hostInstanceId": "host-abc",
  "proof": "hmac-sha256(secret, nonceS + nonceC + runtimeInstanceId)"
}
```

如果认证失败，连接会被关闭。

### 3. 建立事件通道连接

认证成功后，Host 需要连接到独立的事件通道来接收事件流：

```csharp
var eventPipeClient = new NamedPipeClientStream(
    serverName: ".",
    pipeName: "madorin-main-abc123-events",  // 事件通道 pipe 名称
    direction: PipeDirection.InOut,
    options: PipeOptions.Asynchronous
);

await eventPipeClient.ConnectAsync(timeout: 10000);
```

**Host → Runtime（EventChannelHello）**

```json
{
  "hostInstanceId": "host-abc",
  "proof": "hmac-sha256(sessionKey, 'event-channel' + hostInstanceId + runtimeInstanceId)"
}
```

验证通过后，事件流开始推送。

### 4. 保活心跳

控制通道支持心跳保活（可选实现）。建议心跳间隔：30秒。

## 会话与运行管理

### 创建新会话并启动 Run

**方法：`run.new_session`**

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "run.new_session",
  "params": {
    "sessionIdempotencyKey": "session-key-789",
    "runIdempotencyKey": "run-key-abc",
    "mode": "Expert",
    "selection": {
      "providerId": "my-openai",
      "model": "gpt-4o",
      "agentId": null
    },
    "input": [
      {
        "type": "text",
        "text": "你好，请介绍一下自己"
      }
    ],
    "language": "zh-CN"
  }
}
```

**响应：**

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "sessionId": "session-123",
    "runId": "run-456"
  }
}
```

### 继续已有会话

**方法：`run.existing_session`**

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "run.existing_session",
  "params": {
    "sessionId": "session-123",
    "runIdempotencyKey": "run-key-def",
    "input": [
      {
        "type": "text",
        "text": "继续刚才的话题"
      }
    ],
    "language": "zh-CN"
  }
}
```

**响应：**

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "result": {
    "runId": "run-789"
  }
}
```

### 对话语言与环境信息

`run.new_session` 和 `run.existing_session` 都接受可选字段 `language`（BCP 47，例如 `zh-CN`、`en-US`）。

- 传入非空白值时，Runtime 用它作为本轮对话语言。
- 省略字段、JSON `null` 或空白字符串时，Runtime 使用当前操作系统的界面语言。
- 独立 CLI 没有语言设置，因此不传该字段。
- 主程序在用户已保存语言设置时传入该值；未保存时传 `null`。

Runtime 会在专家、工作和会议三种模式的系统提示词末尾追加一段环境信息，供模型知道自己跑在哪：

```text
## Environment
os: windows 10.0.26100 x64
locale: zh-CN
date: 2026-09-22 +08:00
cli: madorin 0.1.50
workspace: E:\work\demo
```

`locale` 来自上面的语言规则。`date` 是本地日期和时区偏移，不包含时分秒。Unix 上能确定交互式 shell 时多一行 `shell`；Windows 上省略。

### 取消运行

**方法：`run.cancel`**

```json
{
  "jsonrpc": "2.0",
  "id": 3,
  "method": "run.cancel",
  "params": {
    "runId": "run-456",
    "reason": "User cancelled"
  }
}
```

**响应：**

```json
{
  "jsonrpc": "2.0",
  "id": 3,
  "result": {
    "cancelled": true
  }
}
```

### 查询运行状态

**方法：`run.get`**

```json
{
  "jsonrpc": "2.0",
  "id": 4,
  "method": "run.get",
  "params": {
    "runId": "run-456"
  }
}
```

**响应：**

```json
{
  "jsonrpc": "2.0",
  "id": 4,
  "result": {
    "runId": "run-456",
    "status": "Running",
    "startedAt": "2026-09-18T12:00:00Z",
    "completedAt": null
  }
}
```

## 事件流

### 接收事件

事件通过**独立的事件通道**推送，不需要订阅。Host 连接事件通道后，事件会自动推送。

事件消息格式（非 JSON-RPC，直接推送）：

```json
{
  "runtimeInstanceId": "runtime-abc123",
  "gsn": 42,
  "runId": "run-456",
  "runSequence": 5,
  "messageType": "output.delta",
  "timestamp": "2026-09-18T12:00:05Z",
  "payload": {
    "invocationId": "inv-789",
    "delta": "你好"
  }
}
```

### 确认事件

Host 定期确认已接收的事件，使用控制通道的 JSON-RPC 方法：

**方法：`events.acknowledge`**

```json
{
  "jsonrpc": "2.0",
  "id": 5,
  "method": "events.acknowledge",
  "params": {
    "lastConfirmedGsn": 42
  }
}
```

**响应：**

```json
{
  "jsonrpc": "2.0",
  "id": 5,
  "result": {}
}
```

Runtime 收到确认后，可以安全地清理已确认的事件缓存。

## 会话管理

### 列出会话

**方法：`session.list`**

```json
{
  "jsonrpc": "2.0",
  "id": 6,
  "method": "session.list",
  "params": {
    "limit": 20,
    "since": "2026-09-10T00:00:00Z"
  }
}
```

**响应：**

```json
{
  "jsonrpc": "2.0",
  "id": 6,
  "result": {
    "sessions": [
      {
        "sessionId": "session-123",
        "title": "代码分析讨论",
        "createdAt": "2026-09-18T10:00:00Z",
        "lastActiveAt": "2026-09-18T12:00:00Z",
        "totalRuns": 5
      }
    ]
  }
}
```

### 获取会话详情

**方法：`session.get`**

```json
{
  "jsonrpc": "2.0",
  "id": 7,
  "method": "session.get",
  "params": {
    "sessionId": "session-123"
  }
}
```

**响应：**

```json
{
  "jsonrpc": "2.0",
  "id": 7,
  "result": {
    "sessionId": "session-123",
    "title": "代码分析讨论",
    "createdAt": "2026-09-18T10:00:00Z",
    "lastActiveAt": "2026-09-18T12:00:00Z",
    "totalRuns": 5,
    "metadata": {}
  }
}
```

### 恢复会话

**方法：`session.resume`**

```json
{
  "jsonrpc": "2.0",
  "id": 8,
  "method": "session.resume",
  "params": {
    "sessionId": "session-123"
  }
}
```

**响应：**

```json
{
  "jsonrpc": "2.0",
  "id": 8,
  "result": {
    "sessionId": "session-123",
    "messageCount": 10,
    "messages": [
      {
        "role": "user",
        "content": [{"type": "text", "text": "你好"}]
      },
      {
        "role": "assistant",
        "content": [{"type": "text", "text": "你好！我是AI助手"}]
      }
    ]
  }
}
```

### Meeting 插话 / 暂停 / 续跑校验

| 方法 | 作用 |
| --- | --- |
| `meeting.input.enqueue` | 入队用户插话（FIFO，`requestId` 幂等） |
| `meeting.pause` | 请求协作式暂停；下一安全点 `Meeting=Paused`，当前 Run=`Interrupted` |
| `meeting.resume` | 校验已 Paused，返回 `ReadyForContinuation`；**不**启动 Run |
| `run.existing_session` | 创建 Continuation Run（新 idempotency key） |

```json
{
  "jsonrpc": "2.0",
  "id": 81,
  "method": "meeting.pause",
  "params": {
    "sessionId": "session-123",
    "requestId": "pause-1",
    "reason": "user break"
  }
}
```

```json
{
  "jsonrpc": "2.0",
  "id": 82,
  "method": "meeting.resume",
  "params": {
    "sessionId": "session-123",
    "requestId": "resume-1"
  }
}
```

`meeting.resume` 成功示例：`status=ReadyForContinuation`，`nextRoundIndex` 为下一讨论轮。随后宿主必须调用 `run.existing_session` 真正续跑。

## Provider 配置

### 注入 Provider

**方法：`providers.upsert`**

```json
{
  "jsonrpc": "2.0",
  "id": 9,
  "method": "providers.upsert",
  "params": {
    "providers": [
      {
        "id": "my-openai",
        "protocol": "OpenAI",
        "apiKey": "sk-...",
        "baseUrl": "https://api.openai.com/v1",
        "model": "gpt-4o"
      }
    ]
  }
}
```

**响应：**

```json
{
  "jsonrpc": "2.0",
  "id": 9,
  "result": {
    "upsertedCount": 1
  }
}
```

### 更新凭据

**方法：`credentials.update`**

```json
{
  "jsonrpc": "2.0",
  "id": 10,
  "method": "credentials.update",
  "params": {
    "providerId": "my-openai",
    "apiKey": "sk-new-key-..."
  }
}
```

**响应：**

```json
{
  "jsonrpc": "2.0",
  "id": 10,
  "result": {}
}
```

## 工具目录管理

### 注册工具

**方法：`tools.catalog.replace`**

```json
{
  "jsonrpc": "2.0",
  "id": 11,
  "method": "tools.catalog.replace",
  "params": {
    "tools": [
      {
        "toolId": "host.search",
        "displayName": "search_database",
        "description": "在数据库中搜索数据",
        "inputSchema": {
          "type": "object",
          "properties": {
            "query": {"type": "string"}
          },
          "required": ["query"]
        }
      }
    ]
  }
}
```

**响应：**

```json
{
  "jsonrpc": "2.0",
  "id": 11,
  "result": {
    "toolCount": 1
  }
}
```

## 错误响应

所有请求失败时返回 JSON-RPC 2.0 错误响应：

```json
{
  "jsonrpc": "2.0",
  "id": 12,
  "error": {
    "code": -32001,
    "message": "Session 'session-123' does not exist",
    "data": {
      "errorCode": "SessionNotFound",
      "sessionId": "session-123"
    }
  }
}
```

**常见错误码**：

| errorCode | 说明 |
| --- | --- |
| `AuthenticationFailed` | 认证失败 |
| `SessionNotFound` | 会话不存在 |
| `RunNotFound` | 运行不存在 |
| `ProviderNotFound` | Provider 未配置 |
| `InvalidRequest` | 请求格式错误 |
| `InternalError` | Runtime 内部错误 |

## 请求-响应模式

所有请求都使用 JSON-RPC 2.0 格式，通过 `id` 字段关联请求和响应：

```
Host                        Runtime
  │                            │
  │──{"jsonrpc":"2.0",         │
  │    "id":1,                 │
  │    "method":"session.get"} │
  │──────────────────────────>│
  │                            │
  │<──{"jsonrpc":"2.0",        │
  │    "id":1,                 │
  │    "result":{...}}─────────│
```

`id` 由 Host 生成，用于匹配请求和响应。

## 幂等性

### Session 创建幂等

通过 `sessionIdempotencyKey` 保证：

```json
{
  "jsonrpc": "2.0",
  "id": 13,
  "method": "run.new_session",
  "params": {
    "sessionIdempotencyKey": "same-key",
    "input": "..."
  }
}
```

相同的 `sessionIdempotencyKey` 只创建一次 Session，重复请求返回已有的 `sessionId`。

### Run 创建幂等

通过 `runIdempotencyKey` 保证：

```json
{
  "jsonrpc": "2.0",
  "id": 14,
  "method": "run.new_session",
  "params": {
    "runIdempotencyKey": "same-run-key",
    "input": "..."
  }
}
```

相同的 `runIdempotencyKey` 只启动一次 Run，重复请求返回已有的 `runId`。

## 断线重连

Host 断线重连后：

1. 重新握手（三次握手）
2. 重新连接事件通道（`EventChannelHello`）
3. 查询未完成的 Run 状态（`run.query`）

事件通道会从上次确认的 GSN 后继续推送事件。

```csharp
// 保存上次确认的 GSN
SaveLastConfirmedGsn(gsn);

// 重连后恢复
var lastGsn = LoadLastConfirmedGsn();
// 事件通道握手时，Runtime 会从 lastGsn + 1 开始重放
await client.ConnectEventChannelAsync();
```

## 性能优化

### 批量确认

避免每个事件都确认，建议每处理 10-100 个事件确认一次：

```csharp
if (gsn % 10 == 0)
{
    await client.AcknowledgeEventsAsync(gsn);
}
```

### 连接复用

单个应用建议只创建一个 Runtime Client 连接，多个 Session/Run 共享。

### 异步处理

使用 `IAsyncEnumerable` 处理事件流，避免阻塞：

```csharp
await foreach (var evt in client.ReadEventsAsync())
{
    await ProcessEventAsync(evt);
}
```

## 完整示例

```csharp
// 1. 连接
var client = await RuntimeClient.ConnectAsync(new RuntimeClientOptions
{
    InstanceName = "main",
    AuthenticationToken = "secret"
});

// 2. 创建会话并启动 Run
var runId = await client.StartNewSessionRunAsync(new NewSessionRunRequest(
    SessionIdempotencyKey: Guid.NewGuid().ToString(),
    RunIdempotencyKey: Guid.NewGuid().ToString(),
    Mode: RuntimeMode.Expert,
    Selection: new NextTurnSelection { ProviderId = "my-openai", Model = "gpt-4o" },
    InitialInput: new[] { ContentBlock.CreateText("你好") }
));

// 3. 处理事件流
await foreach (var evt in client.ReadEventsAsync(0))
{
    if (evt.RunId != runId) continue;
    
    switch (evt.MessageType)
    {
        case "output.delta":
            var delta = evt.Payload.Deserialize<TextDeltaEvent>();
            Console.Write(delta.Delta);
            break;
        
        case "run.completed":
            Console.WriteLine("\n完成！");
            goto Done;
    }
}

Done:
await client.DisposeAsync();
```

## 下一步

- **理解事件**：阅读 [事件模型](./02-事件模型.md)
- **生命周期**：阅读 [Session 与 Run 生命周期](./03-Session与Run生命周期.md)
- **SDK 使用**：阅读 [SDK 集成指南](../03-SDK集成指南.md)
- **完整协议**：查看 [协议规范总览](./README.md)
