# Provider 开发指南

> 开发自定义 Provider 适配器，接入任意 AI 模型服务

本目录提供完整的 Provider 开发指南，帮助你使用任意编程语言实现 Provider Worker。

## 什么是 Provider

Provider 是 Madorin.AI.Runtime 与 AI 模型服务之间的适配器。它负责：

- 将 Runtime 的标准请求转换为模型服务的 API 格式
- 将模型服务的响应转换为 Runtime 的标准事件
- 处理认证、重试、流式输出等细节

## 架构概览

```
┌──────────────────────┐
│  Runtime Server      │
└───────────┬──────────┘
            │ stdin/stdout (JSON 帧)
            ↓
┌──────────────────────┐
│  Provider Worker     │  ← 你要实现的部分
│  (独立进程)           │
└───────────┬──────────┘
            │ HTTPS
            ↓
┌──────────────────────┐
│  AI 服务 API          │
│  (OpenAI/Anthropic/etc)│
└──────────────────────┘
```

### 核心特性

- **独立进程**：每个 Provider 是一个独立的可执行文件
- **语言无关**：只需能读写 stdin/stdout 和解析 JSON
- **按需启动**：Runtime 按需启动 Worker，不使用时不占用资源
- **进程复用**：每个协议每个 Runtime 最多一个 Worker 进程
- **连接复用**：同一 Worker 可服务多个不同的 BaseUrl/ApiKey

## 文档导航

| 文档 | 说明 | 适用对象 |
| --- | --- | --- |
| [语言无关实现指南](./03-语言无关实现指南.md) | 使用任意语言实现 Provider（含完整示例） | 所有开发者 |
| [C# 实现示例](./04-CSharp实现示例.md) | 使用 C# 和 Runtime 基础设施快速实现 Provider | C# 开发者 |
| [Provider 协议](../04-协议规范/04-Provider协议.md) | Runtime ↔ Provider Worker 通信协议 | 所有开发者 |

## 快速开始

### 1. 理解协议

Provider Worker 通过 **stdin/stdout** 与 Runtime 通信：

- **stdin**：接收来自 Runtime 的帧（`hello`、`invoke`、`cancel`、`shutdown`）
- **stdout**：发送帧到 Runtime（`hello.ok`、`event`、`error`）
- **stderr**：诊断日志（Runtime 自动捕获并脱敏）

帧格式：`UInt32 大端长度前缀 + UTF-8 JSON`

### 2. 选择实现方式

**方式 A：使用 C#**

如果你使用 C#，可以直接使用 Runtime 提供的基础设施：

```csharp
using Madorin.AI.Runtime.Providers.WorkerHost;

public static async Task<int> Main(string[] args)
{
    return await ProviderWorkerHost.RunAsync(
        protocol: "MyProvider",
        slug: "myprovider",
        adapterFactory: connection => new MyProviderAdapter(connection),
        stdin: Console.OpenStandardInput(),
        stdout: Console.OpenStandardOutput(),
        stderr: Console.Error
    );
}
```

详见 [C# 实现示例](./04-CSharp实现示例.md)。

**方式 B：使用其他语言**

任何能读写二进制流和解析 JSON 的语言均可实现：

- Python：`sys.stdin.buffer`、`sys.stdout.buffer`、`json`
- Go：`os.Stdin`、`os.Stdout`、`encoding/json`
- Rust：`std::io::stdin`、`std::io::stdout`、`serde_json`
- Node.js：`process.stdin`、`process.stdout`、`JSON.parse`

详见 [语言无关实现指南](./03-语言无关实现指南.md)。

### 3. 实现核心逻辑

最小实现需要：

1. **帧编解码**：读写 stdin/stdout 的长度前缀 JSON 帧
2. **握手协议**：响应 `hello` 并返回 `hello.ok`
3. **处理调用**：接收 `invoke`，调用 AI 服务，返回 `event`
4. **优雅退出**：响应 `shutdown` 并完成所有调用

### 4. 测试与发布

```bash
# 本地测试
echo '{"type":"hello","ipcVersion":1,"protocol":"MyProvider","slug":"myprovider","runtimeVersion":"1.0.0"}' | \
  ./myprovider-worker --slug myprovider

# 集成测试（与真实 Runtime）
madorin serve --instance test
madorin run --instance test --provider myprovider --input "测试"
```

## 开发流程

### 步骤 1：设计协议映射

确定如何将 Runtime 请求映射到目标 AI 服务：

| Runtime | AI 服务 |
| --- | --- |
| `RuntimeProviderRequest.Messages` | API 请求的 `messages` 字段 |
| `RuntimeProviderRequest.Tools` | API 请求的 `tools` / `functions` 字段 |
| `RuntimeProviderRequest.MaxTokens` | API 请求的 `max_tokens` 字段 |
| AI 服务的 SSE 事件 | `RuntimeProviderEvent` |

### 步骤 2：实现帧协议

实现 stdin/stdout 的帧读写：

```
┌────────────────┬────────────────────────────┐
│  Length (4B)   │  JSON Body (Length bytes)  │
│  Big Endian    │  UTF-8                     │
└────────────────┴────────────────────────────┘
```

详见 [Provider 协议](../04-协议规范/04-Provider协议.md)。

### 步骤 3：实现握手

```
Runtime                          Worker
  │                                 │
  │────hello──────────────────────>│
  │  {"type":"hello",               │
  │   "ipcVersion":1,               │
  │   "protocol":"MyProvider"}      │
  │                                 │
  │<───hello.ok────────────────────│
  │  {"type":"hello.ok",            │
  │   "ipcVersion":1,               │
  │   "slug":"myprovider"}          │
```

### 步骤 4：实现调用处理

```
Runtime                          Worker
  │                                 │
  │────invoke──────────────────────>│
  │  {"type":"invoke",              │
  │   "invocationId":"inv-123",     │
  │   "connection":{...},           │
  │   "request":{...}}              │
  │                                 │
  │<───event────────────────────────│
  │  {"type":"event",               │
  │   "invocationId":"inv-123",     │
  │   "event":{"type":"output.delta","text":"..."}} │
  │                                 │
  │<───event────────────────────────│
  │  {"type":"event",               │
  │   "invocationId":"inv-123",     │
  │   "event":{"type":"invocation.completed"}} │
```

### 步骤 5：处理并发

Worker 可能同时收到多个 `invoke`（不同 `invocationId`）：

```csharp
private readonly ConcurrentDictionary<string, Task> _activeInvocations = new();

async Task HandleInvokeAsync(InvokeFrame invoke)
{
    var task = Task.Run(async () =>
    {
        try
        {
            await ProcessInvokeAsync(invoke);
        }
        finally
        {
            _activeInvocations.TryRemove(invoke.InvocationId, out _);
        }
    });
    
    _activeInvocations[invoke.InvocationId] = task;
}
```

### 步骤 6：处理取消

```csharp
private readonly ConcurrentDictionary<string, CancellationTokenSource> _invocationCts = new();

async Task HandleCancelAsync(CancelFrame cancel)
{
    if (_invocationCts.TryRemove(cancel.InvocationId, out var cts))
    {
        cts.Cancel();
    }
}
```

### 步骤 7：优雅退出

```csharp
async Task HandleShutdownAsync()
{
    // 等待所有调用完成
    await Task.WhenAll(_activeInvocations.Values);
    
    // 退出
    Environment.Exit(0);
}
```

## 协议 Slug 注册

每个 Provider 需要注册协议 slug：

| 配置字段 `protocol` | slug | 文件名 |
| --- | --- | --- |
| `MyProvider` | `myprovider` | `madorin-provider-myprovider` |

**规则**：

- slug 必须小写、短横线分隔
- 文件名格式：`madorin-provider-<slug>`（Windows 加 `.exe`）
- 在 `ProviderProtocolSlugs` 和 `build/provider-workers.json` 中注册

## 能力声明

Provider 通过 `ProviderCapabilities` 声明支持的能力：

```csharp
[Flags]
public enum ProviderCapabilities
{
    None = 0,
    Streaming = 1 << 0,           // 流式输出
    ToolCalling = 1 << 1,         // 工具调用
    Vision = 1 << 2,              // 图像输入
    Audio = 1 << 3,               // 音频输入/输出
    StructuredOutput = 1 << 4,    // JSON Schema 输出
    Reasoning = 1 << 5,           // 推理/思考链
    PromptCaching = 1 << 6,       // 提示缓存
    Files = 1 << 7,               // 文件上传
    ComputerUse = 1 << 8,         // 计算机操作
    Embeddings = 1 << 9,          // 向量嵌入
    Usage = 1 << 10,              // Token 用量
    RemoteCancellation = 1 << 11  // 远端取消
}
```

Runtime 只会向 Provider 发送其声明支持的能力相关请求。

## 安全要求

### API Key 保护

- **禁止**将 API Key 输出到 stderr
- **禁止**将 API Key 写入日志文件
- **允许**输出脱敏的 Key 前缀（如 `sk-...a1b2`）

### 进程隔离

- Worker 不访问工作区文件系统（`.madorin/`）
- Worker 不保存 Session 状态
- Worker 不持久化敏感数据

### 网络安全

- 使用 HTTPS 连接 AI 服务
- 验证 SSL 证书
- 支持 HTTP 代理（从 `connection.proxy` 读取）

## 错误处理

### HTTP 错误映射

| HTTP 状态码 | 错误码 | 是否可重试 |
| --- | --- | --- |
| 401/403 | `AuthenticationFailed` | 否 |
| 429 | `RateLimitExceeded` | 是（指数退避） |
| 500/502/503 | `ProviderUnavailable` | 是 |
| 504 | `TimeoutExceeded` | 是 |
| 其他 | `InvokeFailed` | 否 |

### 错误事件

发送 `invocation.failed` 事件：

```json
{
  "type": "event",
  "invocationId": "inv-123",
  "event": {
    "type": "invocation.failed",
    "code": "RateLimitExceeded",
    "message": "Rate limit exceeded, retry after 5s"
  }
}
```

## 性能优化

### 连接池

使用 HTTP 连接池减少握手开销：

```csharp
private static readonly SocketsHttpHandler SharedHandler = new()
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    MaxConnectionsPerServer = 50
};
```

### 并发限制

限制同时处理的调用数：

```csharp
private readonly SemaphoreSlim _concurrencyLimit = new(10);
```

### 流式转发

立即转发 AI 服务的事件，不要缓冲：

```csharp
await foreach (var chunk in ReadSSE(response))
{
    await WriteEventAsync(invocationId, MapChunk(chunk));
}
```

## 完整示例

### OpenAI Compatible Provider（简化）

```csharp
public class OpenAICompatibleAdapter : IRuntimeProviderAdapter
{
    private readonly ProviderConnectionConfig _connection;
    private readonly HttpClient _httpClient;
    
    public OpenAICompatibleAdapter(ProviderConnectionConfig connection)
    {
        _connection = connection;
        _httpClient = CreateHttpClient(connection);
    }
    
    public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
        RuntimeProviderRequest request,
        CancellationToken ct)
    {
        var apiRequest = MapRequest(request);
        var response = await _httpClient.PostAsJsonAsync("/chat/completions", apiRequest, ct);
        
        await foreach (var chunk in ReadSSE(response.Content, ct))
        {
            if (chunk.Choices?[0]?.Delta?.Content is { } text)
            {
                yield return new OutputDeltaEvent(text);
            }
        }
        
        yield return new InvocationCompletedEvent();
    }
}
```

完整实现见 [C# 实现示例](./04-CSharp实现示例.md)。

## 测试清单

- [ ] 握手协议正常工作
- [ ] 可以处理单个 `invoke` 并返回事件
- [ ] 可以处理并发 `invoke`（不同 `invocationId`）
- [ ] 可以响应 `cancel` 并停止调用
- [ ] 可以响应 `shutdown` 并优雅退出
- [ ] stderr 不输出完整 API Key
- [ ] 帧大小超过限制时正确报错
- [ ] HTTP 错误正确映射为事件
- [ ] 与真实 Runtime 集成测试通过

## 发布清单

- [ ] 可执行文件命名：`madorin-provider-<slug>`
- [ ] 支持 `--slug` 参数
- [ ] 在 `ProviderProtocolSlugs` 注册
- [ ] 在 `build/provider-workers.json` 注册
- [ ] 发布到 `bin/` 目录（与 `madorin-cli` 同目录）
- [ ] 更新 `providers.manifest.json`（由发布脚本自动生成）
- [ ] 添加集成测试

## 调试技巧

### 本地调试

使用文件模拟 stdin/stdout：

```bash
# 准备输入
echo '{"type":"hello",...}' > input.txt
cat input.txt | xxd -r -p | ./myprovider-worker --slug myprovider > output.bin
xxd output.bin
```

### 查看 Runtime 日志

```bash
madorin serve --log-level Debug
cat .madorin/logs/runtime-*.log
```

### 查看 Worker stderr

Runtime 自动捕获 Worker 的 stderr 并写入诊断日志：

```
[Worker:myprovider] Connecting to https://api.example.com
[Worker:myprovider] Request latency: 234ms
```

## 常见问题

### Q: 如何处理多个 BaseUrl/ApiKey？

A: 不要为每个配置启动新进程。同一 Worker 通过 `invoke.connection` 接收不同的连接配置，使用连接池管理多个 `HttpClient`。

### Q: Worker 可以保存 Session 状态吗？

A: 不可以。Worker 是无状态的，所有 Session 状态由 Runtime 管理。

### Q: Worker 什么时候退出？

A: 
- 收到 `shutdown` 后优雅退出
- Runtime 关闭时被 Kill
- 发生不可恢复错误时主动退出（退出码 2）

### Q: 如何测试 Worker 不启动 Runtime？

A: 手动构造帧并通过管道传递：

```bash
# 发送 hello
printf '\x00\x00\x00\x4e{"type":"hello","ipcVersion":1,"protocol":"Test","slug":"test","runtimeVersion":"1.0.0"}' | \
  ./myworker --slug test
```

## 下一步

- **了解协议**：阅读 [Provider 协议](../04-协议规范/04-Provider协议.md)
- **完整实现**：阅读 [语言无关实现指南](./03-语言无关实现指南.md)
- **协议总览**：查看 [协议规范总览](../04-协议规范/README.md)

## 参考资源

- [Provider 协议规范](../04-协议规范/04-Provider协议.md)
- [现有 Provider 开发指南](../../开发文档/15-Provider适配器开发指南.md)
- [Provider 进程隔离方案](../../方案设计/12-Provider进程隔离AOT按需加载方案.md)
- [Runtime 源码](../../../src/)
