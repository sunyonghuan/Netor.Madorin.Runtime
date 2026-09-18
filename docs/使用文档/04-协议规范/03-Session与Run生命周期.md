# Session 与 Run 生命周期

> Session 和 Run 的状态管理与生命周期详解

本文档描述 Session（会话）和 Run（运行）的完整生命周期、状态转换和管理机制。

## 核心概念

### Session（会话）

Session 是一个完整的对话上下文，包含多轮对话历史。

**特性**：
- 唯一标识：`sessionId`（UUID）
- 包含多个 Run（按时间顺序）
- 持久化存储（SQLite + JSONL）
- 支持恢复和导出
- 独立的元数据（title、tags 等）

**用途**：
- 多轮对话的上下文保持
- 历史消息的管理
- 对话的组织和检索

### Run（运行）

Run 是 Session 内的一次执行，从用户输入开始到完成/失败/取消结束。

**特性**：
- 唯一标识：`runId`（UUID）
- 属于一个 Session
- 产生一系列事件
- 有明确的开始和结束
- 可以被取消

**用途**：
- 单次任务的执行单元
- 事件流的边界
- 资源分配和清理的单位

## Session 生命周期

### 状态转换

```
    [创建]
      ↓
   ┌────────┐
   │ Active │ ←──────┐
   └────┬───┘        │
        │            │
        │ 长时间未使用  │ 恢复/继续
        ↓            │
   ┌────────┐        │
   │ Idle   │ ───────┘
   └────┬───┘
        │
        │ 归档
        ↓
   ┌────────┐
   │Archived│
   └────────┘
```

### 状态说明

| 状态 | 说明 |
| --- | --- |
| `Active` | 活跃状态，有进行中的 Run 或最近被访问 |
| `Idle` | 空闲状态，长时间未使用但未归档 |
| `Archived` | 已归档，不再活跃但保留历史 |

### 创建 Session

#### 隐式创建

通过 `startNewSessionRun` 自动创建：

```csharp
var request = new NewSessionRunRequest(
    SessionIdempotencyKey: Guid.NewGuid().ToString(),
    RunIdempotencyKey: Guid.NewGuid().ToString(),
    Mode: RuntimeMode.Expert,
    Selection: new NextTurnSelection { ProviderId = "my-openai", Model = "gpt-4o" },
    InitialInput: new[] { ContentBlock.CreateText("你好") }
);

var runId = await client.StartNewSessionRunAsync(request);
// Session 和 Run 同时创建
```

#### 显式创建

通过 `createSession` 单独创建：

```csharp
var sessionId = await client.CreateSessionAsync(new CreateSessionRequest
{
    Title = "代码分析讨论",
    Metadata = new { project = "MyProject", tags = new[] { "coding", "review" } }
});
```

### 恢复 Session

从持久化存储中恢复：

```csharp
var result = await client.ResumeSessionAsync(sessionId);

Console.WriteLine($"Restored {result.MessageCount} messages");
foreach (var msg in result.Messages)
{
    Console.WriteLine($"{msg.Role}: {msg.Content}");
}
```

### 继续 Session

在已有 Session 上启动新 Run：

```csharp
var runId = await client.StartExistingSessionRunAsync(new ExistingSessionRunRequest(
    SessionId: sessionId,
    RunIdempotencyKey: Guid.NewGuid().ToString(),
    Input: new[] { ContentBlock.CreateText("继续刚才的话题") }
));
```

### 查询 Session

```csharp
var result = await client.GetSessionAsync(sessionId);

Console.WriteLine($"Session: {result.Session.Title}");
Console.WriteLine($"Created: {result.Session.CreatedAt}");
Console.WriteLine($"Total Runs: {result.Session.TotalRuns}");
```

### 列出 Sessions

```csharp
var result = await client.ListSessionsAsync(new SessionListParameters
{
    Limit = 20,
    Since = DateTimeOffset.Now.AddDays(-7),
    OrderBy = "LastActiveAt",
    OrderDescending = true
});

foreach (var session in result.Sessions)
{
    Console.WriteLine($"{session.SessionId}: {session.Title} ({session.TotalRuns} runs)");
}
```

### 归档 Session

```csharp
await client.ArchiveSessionAsync(sessionId);
```

## Run 生命周期

### 状态转换

```
    [创建]
      ↓
   ┌────────┐
   │Pending │
   └────┬───┘
        │ Runtime 接受
        ↓
   ┌────────┐
   │Running │
   └────┬───┘
        │
        ├─────────────┬──────────────┐
        │             │              │
        │ 正常完成    │ 失败         │ 取消
        ↓             ↓              ↓
   ┌─────────┐  ┌────────┐    ┌──────────┐
   │Completed│  │ Failed │    │Cancelled │
   └─────────┘  └────────┘    └──────────┘
```

### 状态说明

| 状态 | 说明 | 事件 |
| --- | --- | --- |
| `Pending` | 待执行，已提交但未开始 | - |
| `Running` | 执行中，正在调用 Provider | `run.accepted` |
| `Completed` | 成功完成 | `run.completed` |
| `Failed` | 执行失败 | `run.failed` |
| `Cancelled` | 被取消 | `run.cancelled` |

### 创建 Run

**新 Session + Run**：

```csharp
var runId = await client.StartNewSessionRunAsync(new NewSessionRunRequest(
    SessionIdempotencyKey: "session-key-1",
    RunIdempotencyKey: "run-key-1",
    Mode: RuntimeMode.Expert,
    Selection: new NextTurnSelection { ProviderId = "my-openai", Model = "gpt-4o" },
    InitialInput: new[] { ContentBlock.CreateText("分析这段代码") }
));
```

**已有 Session + Run**：

```csharp
var runId = await client.StartExistingSessionRunAsync(new ExistingSessionRunRequest(
    SessionId: existingSessionId,
    RunIdempotencyKey: "run-key-2",
    Input: new[] { ContentBlock.CreateText("继续分析") }
));
```

### Run 执行

Run 启动后，Runtime 会：

1. 验证输入和配置
2. 加载 Session 上下文
3. 启动 Provider Worker（如未启动）
4. 发起模型调用
5. 流式返回事件

**事件序列**：

```
run.accepted              # Run 已接受
  ↓
invocation.started        # Provider 调用开始
  ↓
output.delta              # 文本增量输出
output.delta
output.delta
  ↓
usage.updated             # Token 用量更新
  ↓
invocation.completed      # Provider 调用完成
  ↓
run.completed             # Run 完成
```

### 取消 Run

用户或系统可以取消正在进行的 Run：

```csharp
var cancelled = await client.CancelRunAsync(runId, reason: "用户取消");
```

**取消流程**：

1. Host 发送 `cancelRun` 请求
2. Runtime 向 Provider Worker 发送 `cancel` 帧
3. Worker 停止 HTTP 请求
4. Worker 返回 `invocation.failed`（code = "Cancelled"）
5. Runtime 发出 `run.cancelled` 事件

### 查询 Run 状态

```csharp
var result = await client.QueryRunAsync(runId);

Console.WriteLine($"Status: {result.Status}");
Console.WriteLine($"Started: {result.StartedAt}");
if (result.CompletedAt.HasValue)
{
    Console.WriteLine($"Completed: {result.CompletedAt}");
    Console.WriteLine($"Duration: {result.CompletedAt - result.StartedAt}");
}
```

## 上下文管理

### 上下文窗口

每个模型都有上下文窗口限制（如 GPT-4o 的 128K tokens）。

Runtime 自动管理上下文：

1. 计算当前消息的总 token 数
2. 如果超过窗口，应用上下文投影策略
3. 发出 `context.projection.adjusted` 事件

### 上下文投影策略

| 策略 | 说明 |
| --- | --- |
| `KeepRecent` | 保留最近的消息，丢弃最早的 |
| `KeepImportant` | 保留系统消息和重要消息 |
| `Summarize` | 对早期消息进行摘要 |

**投影事件**：

```json
{
  "messageType": "context.projection.adjusted",
  "payload": {
    "sessionId": "session-123",
    "originalMessageCount": 50,
    "projectedMessageCount": 30,
    "strategy": "KeepRecent",
    "droppedMessageIds": ["msg-1", "msg-2", "..."]
  }
}
```

### 上下文恢复

恢复 Session 时，Runtime 加载历史消息：

```csharp
var result = await client.ResumeSessionAsync(sessionId);

// 历史消息
foreach (var msg in result.Messages)
{
    Console.WriteLine($"{msg.Role}: {msg.Content}");
}
```

## 幂等性保证

### Session 幂等性

通过 `SessionIdempotencyKey` 保证：

```csharp
// 第一次调用
var runId1 = await client.StartNewSessionRunAsync(new NewSessionRunRequest(
    SessionIdempotencyKey: "same-key",
    RunIdempotencyKey: "run-key-1",
    ...
));

// 第二次调用（相同 SessionIdempotencyKey）
var runId2 = await client.StartNewSessionRunAsync(new NewSessionRunRequest(
    SessionIdempotencyKey: "same-key",  // 相同的 key
    RunIdempotencyKey: "run-key-2",
    ...
));

// runId1 和 runId2 属于同一个 Session
```

### Run 幂等性

通过 `RunIdempotencyKey` 保证：

```csharp
// 第一次调用
var runId1 = await client.StartExistingSessionRunAsync(new ExistingSessionRunRequest(
    SessionId: sessionId,
    RunIdempotencyKey: "same-run-key",
    ...
));

// 第二次调用（相同 RunIdempotencyKey）
var runId2 = await client.StartExistingSessionRunAsync(new ExistingSessionRunRequest(
    SessionId: sessionId,
    RunIdempotencyKey: "same-run-key",  // 相同的 key
    ...
));

// runId1 == runId2，不会重复启动
```

## 并发控制

### Session 级并发

一个 Session 可以同时有多个 Run（不推荐）：

```csharp
var run1 = client.StartExistingSessionRunAsync(request1);
var run2 = client.StartExistingSessionRunAsync(request2);

await Task.WhenAll(run1, run2);
```

**注意**：并发 Run 会导致上下文混乱，建议等待前一个 Run 完成。

### Runtime 级并发

一个 Runtime 实例可以同时处理多个 Session 的多个 Run。

## 数据持久化

### Session 存储

**SQLite 表**：

```sql
CREATE TABLE Sessions (
    SessionId TEXT PRIMARY KEY,
    Title TEXT,
    CreatedAt TEXT NOT NULL,
    LastActiveAt TEXT NOT NULL,
    Status TEXT NOT NULL,
    Metadata TEXT
);
```

### Run 存储

**SQLite 表**：

```sql
CREATE TABLE Runs (
    RunId TEXT PRIMARY KEY,
    SessionId TEXT NOT NULL,
    Status TEXT NOT NULL,
    StartedAt TEXT NOT NULL,
    CompletedAt TEXT,
    Mode TEXT NOT NULL,
    FOREIGN KEY (SessionId) REFERENCES Sessions(SessionId)
);
```

### 消息存储

**JSONL 文件**：

```
.madorin/sessions/<session-id>/messages.jsonl
```

每行一条消息：

```json
{"role":"user","content":[{"type":"text","text":"你好"}]}
{"role":"assistant","content":[{"type":"text","text":"你好！我是AI助手"}]}
```

## 错误处理

### Session 不存在

```csharp
try
{
    await client.GetSessionAsync("non-existent");
}
catch (RuntimeClientException ex) when (ex.Code == "SessionNotFound")
{
    Console.WriteLine("Session not found");
}
```

### Run 不存在

```csharp
try
{
    await client.QueryRunAsync("non-existent");
}
catch (RuntimeClientException ex) when (ex.Code == "RunNotFound")
{
    Console.WriteLine("Run not found");
}
```

### Run 已完成

尝试取消已完成的 Run：

```csharp
var cancelled = await client.CancelRunAsync(runId);
if (!cancelled)
{
    Console.WriteLine("Run already completed or failed");
}
```

## 生命周期回调

使用事件监听 Run 生命周期：

```csharp
await foreach (var evt in client.ReadEventsAsync(0))
{
    switch (evt.MessageType)
    {
        case "run.accepted":
            OnRunStarted(evt);
            break;
        
        case "run.completed":
            OnRunCompleted(evt);
            break;
        
        case "run.failed":
            OnRunFailed(evt);
            break;
        
        case "run.cancelled":
            OnRunCancelled(evt);
            break;
    }
}
```

## 完整示例

### 多轮对话

```csharp
// 创建 Session 和第一个 Run
var sessionKey = Guid.NewGuid().ToString();
var runId1 = await client.StartNewSessionRunAsync(new NewSessionRunRequest(
    SessionIdempotencyKey: sessionKey,
    RunIdempotencyKey: Guid.NewGuid().ToString(),
    Mode: RuntimeMode.Expert,
    Selection: new NextTurnSelection { ProviderId = "my-openai", Model = "gpt-4o" },
    InitialInput: new[] { ContentBlock.CreateText("什么是递归？") }
));

// 等待完成
await WaitForRunCompletionAsync(runId1);

// 获取 Session ID
var sessions = await client.ListSessionsAsync(new SessionListParameters { Limit = 1 });
var sessionId = sessions.Sessions[0].SessionId;

// 继续对话
var runId2 = await client.StartExistingSessionRunAsync(new ExistingSessionRunRequest(
    SessionId: sessionId,
    RunIdempotencyKey: Guid.NewGuid().ToString(),
    Input: new[] { ContentBlock.CreateText("给我一个递归的例子") }
));

await WaitForRunCompletionAsync(runId2);

// 第三轮
var runId3 = await client.StartExistingSessionRunAsync(new ExistingSessionRunRequest(
    SessionId: sessionId,
    RunIdempotencyKey: Guid.NewGuid().ToString(),
    Input: new[] { ContentBlock.CreateText("如何优化递归？") }
));

await WaitForRunCompletionAsync(runId3);
```

### 恢复已有对话

```csharp
// 列出最近的 Sessions
var sessions = await client.ListSessionsAsync(new SessionListParameters
{
    Limit = 10,
    OrderBy = "LastActiveAt",
    OrderDescending = true
});

Console.WriteLine("Recent sessions:");
foreach (var s in sessions.Sessions)
{
    Console.WriteLine($"{s.SessionId}: {s.Title} ({s.TotalRuns} runs)");
}

// 选择一个 Session
var selectedSessionId = sessions.Sessions[0].SessionId;

// 恢复上下文
var resumed = await client.ResumeSessionAsync(selectedSessionId);
Console.WriteLine($"Restored {resumed.MessageCount} messages");

// 继续对话
var runId = await client.StartExistingSessionRunAsync(new ExistingSessionRunRequest(
    SessionId: selectedSessionId,
    RunIdempotencyKey: Guid.NewGuid().ToString(),
    Input: new[] { ContentBlock.CreateText("继续刚才的讨论") }
));
```

## 最佳实践

### 1. Session 命名

给 Session 设置有意义的 title：

```csharp
var sessionId = await client.CreateSessionAsync(new CreateSessionRequest
{
    Title = $"代码审查 - {fileName}",
    Metadata = new { file = fileName, timestamp = DateTimeOffset.Now }
});
```

### 2. 幂等性 Key 管理

使用 GUID 生成唯一的幂等性 Key：

```csharp
var sessionKey = Guid.NewGuid().ToString();
var runKey = Guid.NewGuid().ToString();
```

### 3. Run 等待

不要启动新 Run 直到前一个完成：

```csharp
await WaitForRunCompletionAsync(currentRunId);
var nextRunId = await client.StartExistingSessionRunAsync(...);
```

### 4. 上下文管理

监听 `context.projection.adjusted` 事件，了解上下文变化：

```csharp
if (evt.MessageType == "context.projection.adjusted")
{
    var projection = evt.Payload.Deserialize<ContextProjectionAdjustedEvent>();
    Console.WriteLine($"Context adjusted: {projection.DroppedMessageIds.Length} messages dropped");
}
```

### 5. 错误恢复

保存 Session ID，出错后可以恢复：

```csharp
try
{
    var runId = await client.StartExistingSessionRunAsync(...);
    await WaitForRunCompletionAsync(runId);
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine($"Session ID: {sessionId} (can be resumed later)");
}
```

## 下一步

- **理解协议**：阅读 [Runtime 协议](./01-Runtime协议.md)
- **理解事件**：阅读 [事件模型](./02-事件模型.md)
- **SDK 使用**：阅读 [SDK 集成指南](../03-SDK集成指南.md)
- **协议总览**：查看 [协议规范总览](./README.md)
