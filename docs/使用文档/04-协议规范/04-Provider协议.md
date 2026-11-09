# Provider 协议

> Runtime ↔ Provider Worker 通信协议详细规范

本文档描述 Runtime 与 Provider Worker 进程之间的 IPC 协议，供 Provider 开发者实现语言无关的适配器。

## 概述

Provider Worker 是独立的可执行文件，由 Runtime 按需启动，通过 stdin/stdout 进行通信。

### 设计原则

- **无状态**：Worker 不保存 Session 状态，所有状态由 Runtime 管理
- **语言无关**：任何能读写标准输入输出并解析 JSON 的语言均可实现
- **进程复用**：每个协议 slug 每个 Runtime 最多一个 Worker 进程
- **连接复用**：同一 Worker 可同时服务多个 ProviderId（BaseUrl/ApiKey 按调用传入）

### 进程模型

```
Runtime Process
  ├── Worker: madorin-provider-openai
  │     stdin  ← Runtime 写入 invoke/cancel/shutdown
  │     stdout → Runtime 读取 event/error
  │     stderr → Runtime 诊断日志（自动脱敏）
  │
  ├── Worker: madorin-provider-anthropic
  │     ...
  └── Worker: madorin-provider-deepseek
        ...
```

**关键约束**：

- 每个 Runtime 实例对每个协议 slug 最多启动一个 Worker
- 同一 slug 的多个连接（不同 BaseUrl/ApiKey）复用同一 Worker
- Worker 崩溃后按需重启（不会影响其他 Worker）

## 传输层

### 帧结构

每个帧由 **长度前缀** + **JSON 正文** 组成：

```
┌────────────────┬────────────────────────────┐
│  Length (4B)   │  JSON Body (Length bytes)  │
│  Big Endian    │  UTF-8                     │
└────────────────┴────────────────────────────┘
```

- **Length**：UInt32 大端字节序，表示 JSON Body 的字节长度
- **JSON Body**：UTF-8 编码的 JSON 对象

### 帧大小限制

```csharp
const int MaxFrameLength = 16 * 1024 * 1024; // 16 MB
```

- `Length <= MaxFrameLength`：合法帧
- `Length > MaxFrameLength`：非法，Worker 必须写入 `error` 帧后退出（退出码 2）

### 示例

**帧编码**（十六进制 + 文本）：

```
00 00 00 1E  {"type":"ping","id":"123"}
```

- `00 00 00 1E` = 30（十进制），表示 JSON 正文 30 字节
- `{"type":"ping","id":"123"}` = 30 字节 UTF-8 文本

### 编解码参考

**C# 示例**：

```csharp
// 写帧
public static async Task WriteAsync(Stream stream, ProviderWorkerFrame frame, CancellationToken ct)
{
    var json = JsonSerializer.Serialize(frame, JsonContext.Default.ProviderWorkerFrame);
    var bodyBytes = Encoding.UTF8.GetBytes(json);
    
    if (bodyBytes.Length > MaxFrameLength)
        throw new ProviderWorkerFrameTooLargeException();
    
    var lengthBytes = BitConverter.GetBytes((uint)bodyBytes.Length);
    if (BitConverter.IsLittleEndian)
        Array.Reverse(lengthBytes); // 转大端
    
    await stream.WriteAsync(lengthBytes, ct);
    await stream.WriteAsync(bodyBytes, ct);
    await stream.FlushAsync(ct);
}

// 读帧
public static async Task<ProviderWorkerFrame?> ReadAsync(Stream stream, CancellationToken ct)
{
    var lengthBytes = new byte[4];
    int read = await stream.ReadAsync(lengthBytes, ct);
    if (read == 0) return null; // EOF
    if (read < 4) throw new Exception("Incomplete length prefix");
    
    if (BitConverter.IsLittleEndian)
        Array.Reverse(lengthBytes);
    var length = BitConverter.ToUInt32(lengthBytes);
    
    if (length > MaxFrameLength)
        throw new ProviderWorkerFrameTooLargeException();
    
    var bodyBytes = new byte[length];
    int totalRead = 0;
    while (totalRead < length)
    {
        read = await stream.ReadAsync(bodyBytes.AsMemory(totalRead), ct);
        if (read == 0) throw new Exception("Unexpected EOF");
        totalRead += read;
    }
    
    var json = Encoding.UTF8.GetString(bodyBytes);
    return JsonSerializer.Deserialize<ProviderWorkerFrame>(json, JsonContext.Default.ProviderWorkerFrame);
}
```

其他语言类似实现。

## 帧类型

所有帧都有 `type` 字段用于判别：

```typescript
type ProviderWorkerFrame =
  | HelloFrame
  | HelloOkFrame
  | HelloRejectedFrame
  | InvokeFrame
  | EventFrame
  | CancelFrame
  | PingFrame
  | PongFrame
  | ShutdownFrame
  | ErrorFrame;
```

### HelloFrame (Runtime → Worker)

握手请求，Worker 启动后 Runtime 发送的第一个帧。

```json
{
  "type": "hello",
  "ipcVersion": 1,
  "protocol": "OpenAI",
  "slug": "openai",
  "runtimeVersion": "1.0.0"
}
```

**字段**：

- `ipcVersion`：IPC 协议版本（当前为 1）
- `protocol`：协议名称（如 "OpenAI"、"Anthropic"）
- `slug`：协议 slug（如 "openai"、"anthropic"）
- `runtimeVersion`：Runtime 版本字符串

### HelloOkFrame (Worker → Runtime)

握手成功响应。

```json
{
  "type": "hello.ok",
  "ipcVersion": 1,
  "slug": "openai",
  "protocol": "OpenAI"
}
```

**字段**：

- `ipcVersion`：Worker 支持的 IPC 版本
- `slug`：确认的 slug
- `protocol`：确认的协议名称

### HelloRejectedFrame (Worker → Runtime)

握手拒绝（版本不兼容或其他错误）。

```json
{
  "type": "hello.rejected",
  "code": "VersionMismatch",
  "message": "IPC version 1 not supported"
}
```

**字段**：

- `code`：错误码
- `message`：错误描述

### InvokeFrame (Runtime → Worker)

发起模型调用。

```json
{
  "type": "invoke",
  "invocationId": "inv-123",
  "connection": {
    "providerId": "my-openai",
    "protocol": "OpenAI",
    "apiKey": "sk-...",
    "baseUrl": "https://api.openai.com/v1",
    "model": "gpt-4o",
    "additionalConfig": {}
  },
  "request": {
    "invocationId": "inv-123",
    "model": "gpt-4o",
    "messages": [
      {
        "role": "user",
        "content": [{"type": "text", "text": "你好"}]
      }
    ],
    "tools": [],
    "maxTokens": 4096,
    "temperature": 1.0,
    "topP": null,
    "stopSequences": null,
    "streamingCallback": null
  }
}
```

**字段**：

- `invocationId`：调用唯一标识，用于路由 `event` 和 `cancel`
- `connection`：连接配置（ProviderId、ApiKey、BaseUrl 等）
- `request`：标准的 `RuntimeProviderRequest` 对象

**重要**：

- 同一 Worker 可能同时处理多个 `invoke`（不同 `invocationId`）
- Worker 必须按 `invocationId` 正确路由 `event` 和 `error`
- `connection.apiKey` 包含明文 API Key，Worker 禁止将其输出到 stderr

### EventFrame (Worker → Runtime)

返回流式事件。

```json
{
  "type": "event",
  "invocationId": "inv-123",
  "event": {
    "type": "output.delta",
    "text": "你好"
  }
}
```

**字段**：

- `invocationId`：关联的调用 ID
- `event`：标准的 `RuntimeProviderEvent` 对象

**常见事件类型**：

- `output.delta`：文本增量
- `tool.call.request`：工具调用请求
- `tool.call.delta`：工具调用参数增量
- `tool.call.completed`：工具调用完成
- `usage.updated`：Token 用量更新
- `invocation.completed`：调用完成
- `invocation.failed`：调用失败

详见 [事件类型参考](../../06-API参考/02-事件类型参考.md)。

### CancelFrame (Runtime → Worker)

取消正在进行的调用。

```json
{
  "type": "cancel",
  "invocationId": "inv-123"
}
```

**字段**：

- `invocationId`：要取消的调用 ID

**行为**：

- Worker 应尽快停止该调用的 HTTP 请求
- 发送 `invocation.failed` 事件（code = "Cancelled"）
- 不影响其他并发调用

### PingFrame (Runtime → Worker)

心跳探测。

```json
{
  "type": "ping",
  "id": "ping-456"
}
```

**字段**：

- `id`：Ping ID

### PongFrame (Worker → Runtime)

心跳响应。

```json
{
  "type": "pong",
  "id": "ping-456"
}
```

**字段**：

- `id`：对应的 Ping ID

### ShutdownFrame (Runtime → Worker)

优雅关闭请求。

```json
{
  "type": "shutdown"
}
```

**行为**：

- Worker 应完成正在进行的调用
- 发送完所有 `event`
- 退出（退出码 0）

### ErrorFrame (双向)

错误通知。

```json
{
  "type": "error",
  "invocationId": "inv-123",
  "code": "InvokeFailed",
  "message": "HTTP 500: Internal Server Error"
}
```

**字段**：

- `invocationId`：关联的调用 ID（可选，全局错误时为 null）
- `code`：错误码
- `message`：错误描述

## 生命周期

### 启动

Runtime 通过 `Process.Start` 启动 Worker：

```csharp
var startInfo = new ProcessStartInfo
{
    FileName = "madorin-provider-openai",
    Arguments = "--slug openai",
    UseShellExecute = false,
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    CreateNoWindow = true,
    WorkingDirectory = "/path/to/bin" // Worker exe 所在目录
};

var process = Process.Start(startInfo);
```

**命令行参数**：

- `--slug <slug>`：协议 slug（必需）

**禁止传递**：

- API Key（通过 `invoke.connection` 传递）
- Session ID / Run ID（Worker 无需关心）
- Workspace 路径（Worker 不访问文件系统）

**环境变量**：

- `DOTNET_EnableDiagnostics=0`（可选，禁用 .NET 诊断）
- 不传递 API Key

### 握手

```
Runtime                          Worker
  │                                 │
  │────hello──────────────────────>│
  │                                 │
  │<───hello.ok────────────────────│
  │                                 │
```

握手失败时：

```
Runtime                          Worker
  │                                 │
  │────hello──────────────────────>│
  │                                 │
  │<───hello.rejected──────────────│
  │                                 │
  │  (Runtime kills Worker)         │
```

### 正常调用

```
Runtime                          Worker
  │                                 │
  │────invoke(inv-1)──────────────>│
  │                                 │
  │<───event(inv-1, delta)─────────│
  │<───event(inv-1, delta)─────────│
  │<───event(inv-1, completed)─────│
  │                                 │
```

### 并发调用

```
Runtime                          Worker
  │                                 │
  │────invoke(inv-1)──────────────>│
  │────invoke(inv-2)──────────────>│
  │                                 │
  │<───event(inv-1, delta)─────────│
  │<───event(inv-2, delta)─────────│
  │<───event(inv-1, delta)─────────│
  │<───event(inv-2, completed)─────│
  │<───event(inv-1, completed)─────│
  │                                 │
```

Worker 必须按 `invocationId` 正确路由事件。

### 取消

```
Runtime                          Worker
  │                                 │
  │────invoke(inv-1)──────────────>│
  │                                 │
  │<───event(inv-1, delta)─────────│
  │                                 │
  │────cancel(inv-1)──────────────>│
  │                                 │
  │<───event(inv-1, failed)────────│
  │     code: "Cancelled"           │
  │                                 │
```

### 优雅关闭

```
Runtime                          Worker
  │                                 │
  │────shutdown────────────────────>│
  │                                 │
  │  (Worker 完成进行中的调用)      │
  │                                 │
  │<───event(inv-N, completed)─────│
  │                                 │
  │  (Worker 退出，退出码 0)        │
```

### 异常退出

Worker 崩溃或超过帧大小限制时：

```
Runtime                          Worker
  │                                 │
  │────invoke(inv-1)──────────────>│
  │                                 │
  │<───error(inv-1, crash)─────────│
  │                                 │
  │  (Worker 退出，退出码 2)        │
```

Runtime 行为：

- 将所有进行中的 `invoke` 标记为失败（code = "ProviderWorkerExited"）
- Kill Worker 进程
- 下次需要时重新启动

## 连接复用

### ProviderId vs Slug

- **Slug**：协议标识（如 "openai"），决定使用哪个 Worker 进程
- **ProviderId**：连接标识（如 "my-openai-1"、"my-openai-2"），区分不同 BaseUrl/ApiKey

同一 Runtime 的同一 slug 只有一个 Worker，但可能有多个 ProviderId。

### 示例

配置：

```json
{
  "providers": [
    {"id": "openai-us", "protocol": "OpenAI", "baseUrl": "https://api.openai.com/v1", "apiKey": "sk-us-..."},
    {"id": "openai-eu", "protocol": "OpenAI", "baseUrl": "https://api.openai.eu/v1", "apiKey": "sk-eu-..."}
  ]
}
```

进程：

- Runtime 只启动一个 `madorin-provider-openai` Worker
- 两个 `invoke` 携带不同的 `connection.providerId`、`connection.baseUrl`、`connection.apiKey`
- Worker 内部使用 `IHttpClientFactory` 按连接配置创建 `HttpClient`

**禁止**：

- 为每个 ProviderId 启动独立 Worker
- 在 Worker 内维护单例 `HttpClient`（会混淆不同 BaseUrl/ApiKey）

## 错误处理

### Worker 错误码

| 错误码 | 说明 | 退出码 |
| --- | --- | --- |
| `InvokeFailed` | 调用失败（HTTP 错误、超时等） | 继续运行 |
| `WorkerCrashed` | Worker 崩溃 | 2 |
| `FrameTooLarge` | 帧超过大小限制 | 2 |

### HTTP 错误映射

Worker 应将 HTTP 错误映射为 `invocation.failed` 事件：

| HTTP 状态码 | 错误码 | 是否可重试 |
| --- | --- | --- |
| 401/403 | `AuthenticationFailed` | 否（需要更新凭据） |
| 429 | `RateLimitExceeded` | 是（指数退避） |
| 500/502/503 | `ProviderUnavailable` | 是 |
| 504 | `TimeoutExceeded` | 是 |
| 其他 | `InvokeFailed` | 否 |

### stderr 日志

Worker 的 stderr 由 Runtime 捕获并写入诊断日志。

**安全要求**：

- **禁止输出完整 API Key**
- 可以输出前缀（如 "sk-...a1b2"）
- 可以输出脱敏后的日志

示例（正确）：

```
[INFO] Connecting to https://api.openai.com/v1
[INFO] Using API Key: sk-...a1b2
[ERROR] HTTP 401: Invalid API Key
```

示例（错误）：

```
[ERROR] API Key: sk-abc123def456...  ← 禁止！
```

## 实现指南

### 最小实现

一个最小的 Provider Worker 需要：

1. 读写 stdin/stdout 的帧编解码
2. 握手协议（hello/hello.ok）
3. 处理 `invoke` 并返回 `event`
4. 处理 `shutdown` 并优雅退出

### 推荐实现

完整实现应支持：

1. 并发调用（多个 `invocationId`）
2. 取消（响应 `cancel`）
3. 心跳（响应 `ping`）
4. 错误处理（发送 `error` 并继续运行）
5. 连接复用（按 `connection` 创建 `HttpClient`）

### C# 参考实现

Runtime 提供 `ProviderWorkerHost` 基础设施：

```csharp
using Madorin.AI.Runtime.Providers.WorkerHost;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var slug = ParseSlug(args); // 解析 --slug 参数
        
        return await ProviderWorkerHost.RunAsync(
            protocol: "OpenAI",
            slug: slug,
            adapterFactory: connection => new OpenAIProviderAdapter(connection),
            stdin: Console.OpenStandardInput(),
            stdout: Console.OpenStandardOutput(),
            stderr: Console.Error
        );
    }
}
```

详见 [C# 实现示例](../../05-Provider开发指南/04-CSharp实现示例.md)。

### 其他语言实现

任何语言只要能：

1. 读写二进制 stdin/stdout
2. 解析和生成 JSON
3. 发起 HTTPS 请求

就可以实现 Provider Worker。

详见 [语言无关实现指南](../../05-Provider开发指南/03-语言无关实现指南.md)。

## 性能考虑

### 并发限制

Worker 应自行限制并发调用数，避免过载：

```csharp
private readonly SemaphoreSlim _concurrencyLimit = new(10); // 最多 10 并发

await _concurrencyLimit.WaitAsync();
try
{
    await ProcessInvokeAsync(invoke);
}
finally
{
    _concurrencyLimit.Release();
}
```

### 连接池

使用 `IHttpClientFactory` 或等价机制管理 HTTP 连接：

```csharp
private readonly ConcurrentDictionary<string, HttpClient> _clients = new();

private HttpClient GetClient(ProviderConnectionConfig connection)
{
    return _clients.GetOrAdd(connection.ProviderId, _ =>
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(connection.BaseUrl),
            Timeout = TimeSpan.FromSeconds(120)
        };
        
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {connection.ApiKey}");
        
        return client;
    });
}
```

### 流式输出

立即转发 AI 服务的流式事件，不要缓冲：

```csharp
await foreach (var chunk in ReadServerSentEventsAsync(response.Body))
{
    var evt = MapToRuntimeEvent(chunk);
    await WriteEventAsync(invocationId, evt);
}
```

## 测试

### 单元测试

模拟 Runtime 的 stdin/stdout：

```csharp
var stdin = new MemoryStream();
var stdout = new MemoryStream();

// 写入 hello
await WriteFrameAsync(stdin, new HelloFrame(...));
stdin.Position = 0;

// 运行 Worker
var task = ProviderWorkerHost.RunAsync(..., stdin, stdout, ...);

// 读取 hello.ok
stdout.Position = 0;
var response = await ReadFrameAsync(stdout);
Assert.IsType<HelloOkFrame>(response);
```

### 集成测试

使用真实 Runtime：

```bash
# 启动 Runtime（工作区必填，握手密钥通过 stdin 提供）
echo "change-this-secret" | madorin serve --workspace /path/to/project --instance test

# 独立 CLI 直接发起调用（会在该工作区启动 Provider Worker）
madorin run --workspace /path/to/project --input "测试"

# 检查日志
cat .madorin/logs/runtime-*.log
```

Provider Worker 由 `madorin run` 在该工作区按需启动。独立 `run` 命令直接连接 Provider Worker；它不通过 `madorin serve` 的实例名路由请求。

## 下一步

- **多语言实现**：阅读 [语言无关实现指南](../../05-Provider开发指南/03-语言无关实现指南.md)
- **开发流程**：阅读 [Provider 开发指南](../../05-Provider开发指南/README.md)
- **架构理解**：阅读 [协议规范总览](../README.md)
- **完整规格**：查看 [Provider 进程隔离方案](../../../方案设计/12-Provider进程隔离AOT按需加载方案.md)
