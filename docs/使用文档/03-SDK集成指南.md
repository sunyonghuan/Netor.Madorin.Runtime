# SDK 集成指南

> 通过 Client SDK 将 Madorin.AI.Runtime 集成到应用程序

本文档面向需要将 Runtime 集成到桌面应用、服务端程序或其他宿主应用的开发者。

## 概述

集成方式：**宿主应用** ↔ **Runtime Server** ↔ **Provider Workers**

- **宿主应用**：你的应用程序，通过 Client SDK 连接 Runtime
- **Runtime Server**：`madorin serve` 启动的常驻服务，管理会话和执行
- **Provider Workers**：独立进程，由 Runtime 按需启动

### 适用场景

- 桌面应用程序（WPF、Avalonia、MAUI 等）
- 服务端应用（ASP.NET Core、gRPC 服务等）
- 需要完整事件控制和生命周期管理
- 多会话并发处理

## 快速开始

### 1. 添加 NuGet 包

```xml
<ItemGroup>
  <PackageReference Include="Madorin.AI.Runtime.Client" Version="1.0.0" />
  <PackageReference Include="Madorin.AI.Runtime.Contracts" Version="1.0.0" />
</ItemGroup>
```

或使用 CLI：

```bash
dotnet add package Madorin.AI.Runtime.Client
dotnet add package Madorin.AI.Runtime.Contracts
```

### 2. 启动 Runtime Server

在你的应用启动前或同时启动 Runtime Server：

```bash
# 命令行启动
madorin serve --instance main

# 或在代码中启动（见下文）
```

### 3. 连接并使用

```csharp
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;

// 创建客户端选项
var options = new RuntimeClientOptions
{
    InstanceName = "main",
    AuthenticationToken = "your-secret-token"
};

// 连接到 Runtime
var client = await RuntimeClient.ConnectAsync(options);

// 创建新会话并启动 Run
var request = new NewSessionRunRequest(
    SessionIdempotencyKey: Guid.NewGuid().ToString(),
    RunIdempotencyKey: Guid.NewGuid().ToString(),
    Mode: RuntimeMode.Expert,
    Selection: new NextTurnSelection
    {
        ProviderId = "my-openai",
        Model = "gpt-4o"
    },
    InitialInput: new[]
    {
        ContentBlock.CreateText("你好，请介绍一下自己")
    }
);

var runId = await client.StartNewSessionRunAsync(request);

// 监听事件流
await foreach (var evt in client.ReadEventsAsync(0))
{
    if (evt.MessageType == "output.delta")
    {
        var delta = evt.Payload.Deserialize<TextDeltaEvent>();
        Console.Write(delta.Text);
    }
    else if (evt.MessageType == "run.completed")
    {
        break;
    }
}

// 清理
await client.DisposeAsync();
```

## Runtime Server 生命周期

### 方式一：独立进程（推荐）

由你的应用启动和管理 `madorin serve` 进程：

```csharp
using System.Diagnostics;

public class RuntimeServerManager : IAsyncDisposable
{
    private Process? _serverProcess;

    public async Task StartAsync(string instanceName, string dataDir)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "madorin",
            Arguments = $"serve --instance {instanceName} --data-dir {dataDir}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        _serverProcess = Process.Start(startInfo);
        
        // 等待 Runtime 就绪
        await Task.Delay(1000);
    }

    public async ValueTask DisposeAsync()
    {
        if (_serverProcess != null && !_serverProcess.HasExited)
        {
            _serverProcess.Kill();
            await _serverProcess.WaitForExitAsync();
        }
    }
}

// 使用
var manager = new RuntimeServerManager();
await manager.StartAsync("main", "./data");
```

### 方式二：嵌入式（高级）

直接在应用进程内启动 `RuntimeServer`：

```csharp
using Madorin.AI.Runtime.Server;

var options = new RuntimeServerOptions
{
    InstanceName = "embedded",
    DataDirectory = "./data",
    WorkspaceRoot = "./workspace",
    HandshakeSecret = "your-secret",
    // ... 其他选项
};

var server = await RuntimeServer.StartAsync(options);

// 应用关闭时清理
await server.DisposeAsync();
```

注意：嵌入式需要直接引用 `Madorin.AI.Runtime.Server`，会增加应用体积。

## Client SDK 核心接口

### IRuntimeClient

```csharp
public interface IRuntimeClient : IAsyncDisposable
{
    // 实例绑定信息
    RuntimeInstanceBinding InstanceBinding { get; }
    
    // 协商的能力
    RuntimeCapabilities? NegotiatedCapabilities { get; }
    
    // 客户端状态
    RuntimeClientState State { get; }
    
    // 完成信号
    Task Completion { get; }
    
    // 连接
    ValueTask ConnectAsync(CancellationToken cancellationToken = default);
    
    // 启动新会话
    ValueTask<string> StartNewSessionRunAsync(
        NewSessionRunRequest request,
        CancellationToken cancellationToken = default);
    
    // 继续已有会话
    ValueTask<string> StartExistingSessionRunAsync(
        ExistingSessionRunRequest request,
        CancellationToken cancellationToken = default);
    
    // 读取事件流
    IAsyncEnumerable<RuntimeEventEnvelope> ReadEventsAsync(
        long eventReplayCursor,
        CancellationToken cancellationToken = default);
    
    // 确认事件
    ValueTask AcknowledgeEventsAsync(
        long lastConfirmedGsn,
        CancellationToken cancellationToken = default);
    
    // 取消运行
    ValueTask<bool> CancelRunAsync(
        string runId,
        string? reason = null,
        CancellationToken cancellationToken = default);
    
    // 查询运行状态
    ValueTask<RunQueryResult> QueryRunAsync(
        string runId,
        CancellationToken cancellationToken = default);
    
    // 会话管理
    ValueTask<SessionGetResult> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
    
    ValueTask<SessionListResult> ListSessionsAsync(
        SessionListParameters parameters,
        CancellationToken cancellationToken = default);
    
    ValueTask<SessionResumeResult> ResumeSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
    
    // 工具目录管理
    ValueTask<ToolCatalogUpdateResponse> ReplaceToolCatalogAsync(
        ToolCatalogReplaceRequest request,
        CancellationToken cancellationToken = default);
    
    // 凭据更新
    ValueTask UpdateCredentialsAsync(
        CredentialsUpdateParameters parameters,
        CancellationToken cancellationToken = default);
    
    // Provider 配置
    ValueTask UpsertProvidersAsync(
        IReadOnlyList<HostProviderConfig> providers,
        CancellationToken cancellationToken = default);
}
```

## 连接选项

### RuntimeClientOptions

```csharp
var options = new RuntimeClientOptions
{
    // 实例名称（与 madorin serve --instance 对应）
    InstanceName = "main",
    
    // 认证令牌（与 Runtime 启动时的 HandshakeSecret 对应）
    AuthenticationToken = "your-secret-token",
    
    // 连接超时
    ConnectionTimeout = TimeSpan.FromSeconds(10),
    
    // 心跳间隔
    HeartbeatInterval = TimeSpan.FromSeconds(30),
    
    // 自动重连
    AutoReconnect = true,
    
    // 重连间隔
    ReconnectInterval = TimeSpan.FromSeconds(5),
    
    // 最大重连次数
    MaxReconnectAttempts = 10
};
```

## 创建会话与启动 Run

### 新会话

```csharp
var request = new NewSessionRunRequest(
    SessionIdempotencyKey: Guid.NewGuid().ToString(),
    RunIdempotencyKey: Guid.NewGuid().ToString(),
    Mode: RuntimeMode.Expert,
    Selection: new NextTurnSelection
    {
        ProviderId = "my-openai",
        Model = "gpt-4o",
        AgentId = "default"
    },
    InitialInput: new[]
    {
        ContentBlock.CreateText("分析这段代码")
    },
    ProviderConfigs: new[]
    {
        new HostProviderConfig
        {
            Id = "my-openai",
            Protocol = "OpenAI",
            ApiKey = "sk-...",
            BaseUrl = "https://api.openai.com/v1"
        }
    }
);

var runId = await client.StartNewSessionRunAsync(request);
```

### 继续已有会话

```csharp
var request = new ExistingSessionRunRequest(
    SessionId: "existing-session-id",
    RunIdempotencyKey: Guid.NewGuid().ToString(),
    Input: new[]
    {
        ContentBlock.CreateText("继续刚才的话题")
    }
);

var runId = await client.StartExistingSessionRunAsync(request);
```

## 事件处理

### 事件流模型

Runtime 通过事件流返回执行过程中的所有信息：

```csharp
await foreach (var evt in client.ReadEventsAsync(0))
{
    switch (evt.MessageType)
    {
        case "output.delta":
            // 文本增量输出
            var delta = evt.Payload.Deserialize<TextDeltaEvent>();
            OnTextDelta(delta.Text);
            break;
        
        case "tool.call.request":
            // 工具调用请求
            var toolCall = evt.Payload.Deserialize<ToolCallRequestEvent>();
            OnToolCallRequest(toolCall.ToolId, toolCall.Arguments);
            break;
        
        case "tool.call.delta":
            // 工具调用参数增量
            var toolDelta = evt.Payload.Deserialize<ToolCallDeltaEvent>();
            OnToolCallDelta(toolDelta.ArgumentDelta);
            break;
        
        case "tool.call.completed":
            // 工具调用完成
            break;
        
        case "run.completed":
            // Run 完成
            var completed = evt.Payload.Deserialize<RunCompletedEvent>();
            OnRunCompleted(completed);
            break;
        
        case "run.failed":
            // Run 失败
            var failed = evt.Payload.Deserialize<RunFailedEvent>();
            OnRunFailed(failed.Code, failed.Message);
            break;
        
        case "usage.updated":
            // Token 用量更新
            var usage = evt.Payload.Deserialize<UsageUpdatedEvent>();
            OnUsageUpdated(usage.InputTokens, usage.OutputTokens);
            break;
    }
}
```

### 事件确认

为了可靠投递，需要定期确认已处理的事件：

```csharp
long lastGsn = 0;

await foreach (var evt in client.ReadEventsAsync(0))
{
    // 处理事件
    ProcessEvent(evt);
    
    lastGsn = evt.Gsn;
    
    // 每 10 个事件确认一次
    if (lastGsn % 10 == 0)
    {
        await client.AcknowledgeEventsAsync(lastGsn);
    }
}

// 最后确认
if (lastGsn > 0)
{
    await client.AcknowledgeEventsAsync(lastGsn);
}
```

### 断线重连

Client SDK 支持自动重连：

```csharp
var options = new RuntimeClientOptions
{
    AutoReconnect = true,
    ReconnectInterval = TimeSpan.FromSeconds(5),
    MaxReconnectAttempts = 10
};

var client = await RuntimeClient.ConnectAsync(options);

// 监听状态变化
client.StateChanged += (sender, state) =>
{
    Console.WriteLine($"Client state: {state}");
    
    if (state == RuntimeClientState.Reconnecting)
    {
        ShowReconnectingUI();
    }
    else if (state == RuntimeClientState.Connected)
    {
        HideReconnectingUI();
    }
};

// 断线重连后，从上次确认的 GSN 继续读取
long lastConfirmedGsn = LoadLastConfirmedGsn();
await foreach (var evt in client.ReadEventsAsync(lastConfirmedGsn + 1))
{
    // 处理事件
}
```

## 会话管理

### 列出会话

```csharp
var parameters = new SessionListParameters
{
    Limit = 20,
    Since = DateTimeOffset.Now.AddDays(-7)
};

var result = await client.ListSessionsAsync(parameters);

foreach (var session in result.Sessions)
{
    Console.WriteLine($"{session.SessionId}: {session.Title}");
}
```

### 获取会话详情

```csharp
var result = await client.GetSessionAsync(sessionId);

Console.WriteLine($"Session: {result.Session.Title}");
Console.WriteLine($"Created: {result.Session.CreatedAt}");
Console.WriteLine($"Runs: {result.Session.TotalRuns}");
```

### 恢复会话

```csharp
var result = await client.ResumeSessionAsync(sessionId);

if (result.Success)
{
    Console.WriteLine($"Resumed session with {result.MessageCount} messages");
}
```

## 工具集成

### 注册自定义工具

```csharp
var tools = new[]
{
    new ToolDescriptor
    {
        ToolId = "host.search",
        DisplayName = "search_database",
        Description = "在数据库中搜索数据",
        InputSchema = new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string" },
                limit = new { type = "integer" }
            },
            required = new[] { "query" }
        }
    }
};

var request = new ToolCatalogReplaceRequest
{
    Tools = tools
};

await client.ReplaceToolCatalogAsync(request);
```

### 处理工具调用回调

工具调用需要宿主实现反向 RPC。详见 [协议规范：Runtime 协议](./04-协议规范/01-Runtime协议.md)。

## Provider 配置

### 动态注入 Provider

```csharp
var providers = new[]
{
    new HostProviderConfig
    {
        Id = "my-openai",
        Protocol = "OpenAI",
        ApiKey = GetApiKeyFromSecureStorage(),
        BaseUrl = "https://api.openai.com/v1"
    },
    new HostProviderConfig
    {
        Id = "my-anthropic",
        Protocol = "Anthropic",
        ApiKey = GetAnthropicKey(),
        BaseUrl = "https://api.anthropic.com"
    }
};

await client.UpsertProvidersAsync(providers);
```

### 凭据刷新

当 Provider 返回认证失败时，Runtime 会发出 `credentials.refresh.request` 事件：

```csharp
await foreach (var evt in client.ReadEventsAsync(0))
{
    if (evt.MessageType == "credentials.refresh.request")
    {
        var refreshEvent = evt.Payload.Deserialize<CredentialsRefreshRequestEvent>();
        
        // 提示用户输入新凭据
        var newApiKey = await PromptForNewApiKey(refreshEvent.ProviderId);
        
        // 更新凭据
        await client.UpdateCredentialsAsync(new CredentialsUpdateParameters
        {
            ProviderId = refreshEvent.ProviderId,
            ApiKey = newApiKey
        });
    }
}
```

## 取消运行

```csharp
// 启动长时间运行的任务
var runId = await client.StartNewSessionRunAsync(request);

// 在另一个任务中取消
await Task.Delay(5000);
await client.CancelRunAsync(runId, "用户取消");
```

## 查询运行状态

```csharp
var result = await client.QueryRunAsync(runId);

Console.WriteLine($"Status: {result.Status}");
Console.WriteLine($"Started: {result.StartedAt}");
Console.WriteLine($"Completed: {result.CompletedAt}");
```

## 完整示例

### 桌面应用集成

```csharp
public class AIService : IAsyncDisposable
{
    private readonly RuntimeServerManager _serverManager;
    private IRuntimeClient? _client;
    
    public event Action<string>? OnTextDelta;
    public event Action<string, string>? OnError;
    public event Action? OnCompleted;
    
    public AIService()
    {
        _serverManager = new RuntimeServerManager();
    }
    
    public async Task InitializeAsync()
    {
        // 启动 Runtime Server
        await _serverManager.StartAsync("app", "./data");
        
        // 连接 Client
        var options = new RuntimeClientOptions
        {
            InstanceName = "app",
            AuthenticationToken = "secret",
            AutoReconnect = true
        };
        
        _client = await RuntimeClient.ConnectAsync(options);
    }
    
    public async Task<string> SendMessageAsync(
        string sessionId,
        string message)
    {
        if (_client == null)
            throw new InvalidOperationException("Not initialized");
        
        // 创建或继续会话
        string runId;
        if (string.IsNullOrEmpty(sessionId))
        {
            var request = new NewSessionRunRequest(
                SessionIdempotencyKey: Guid.NewGuid().ToString(),
                RunIdempotencyKey: Guid.NewGuid().ToString(),
                Mode: RuntimeMode.Expert,
                Selection: new NextTurnSelection
                {
                    ProviderId = "my-openai",
                    Model = "gpt-4o"
                },
                InitialInput: new[] { ContentBlock.CreateText(message) }
            );
            runId = await _client.StartNewSessionRunAsync(request);
        }
        else
        {
            var request = new ExistingSessionRunRequest(
                SessionId: sessionId,
                RunIdempotencyKey: Guid.NewGuid().ToString(),
                Input: new[] { ContentBlock.CreateText(message) }
            );
            runId = await _client.StartExistingSessionRunAsync(request);
        }
        
        // 监听事件
        await foreach (var evt in _client.ReadEventsAsync(0))
        {
            if (evt.RunId != runId)
                continue;
            
            switch (evt.MessageType)
            {
                case "output.delta":
                    var delta = evt.Payload.Deserialize<TextDeltaEvent>();
                    OnTextDelta?.Invoke(delta.Text);
                    break;
                
                case "run.completed":
                    OnCompleted?.Invoke();
                    return runId;
                
                case "run.failed":
                    var failed = evt.Payload.Deserialize<RunFailedEvent>();
                    OnError?.Invoke(failed.Code, failed.Message);
                    throw new Exception($"Run failed: {failed.Message}");
            }
        }
        
        return runId;
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_client != null)
            await _client.DisposeAsync();
        
        await _serverManager.DisposeAsync();
    }
}

// 使用
var service = new AIService();
await service.InitializeAsync();

service.OnTextDelta += text => Console.Write(text);
service.OnCompleted += () => Console.WriteLine("\n完成！");

await service.SendMessageAsync(null, "你好");
```

## 错误处理

### 异常类型

```csharp
try
{
    await client.StartNewSessionRunAsync(request);
}
catch (RuntimeClientException ex)
{
    // Client 相关错误
    Console.WriteLine($"Client error: {ex.Message}");
}
catch (TimeoutException)
{
    // 连接或操作超时
    Console.WriteLine("Operation timed out");
}
catch (OperationCanceledException)
{
    // 用户取消
    Console.WriteLine("Operation cancelled");
}
```

### 连接失败处理

```csharp
var options = new RuntimeClientOptions
{
    InstanceName = "main",
    ConnectionTimeout = TimeSpan.FromSeconds(10),
    AutoReconnect = true
};

try
{
    var client = await RuntimeClient.ConnectAsync(options);
}
catch (RuntimeClientException ex) when (ex.Message.Contains("not found"))
{
    // Runtime 实例不存在
    Console.WriteLine("Runtime instance not found. Starting...");
    await StartRuntimeServerAsync();
    var client = await RuntimeClient.ConnectAsync(options);
}
```

## 性能优化

### 批量事件处理

```csharp
var batch = new List<RuntimeEventEnvelope>();
const int batchSize = 10;

await foreach (var evt in client.ReadEventsAsync(0))
{
    batch.Add(evt);
    
    if (batch.Count >= batchSize)
    {
        ProcessBatch(batch);
        await client.AcknowledgeEventsAsync(batch[^1].Gsn);
        batch.Clear();
    }
}
```

### 连接池

对于多租户场景，可以维护连接池：

```csharp
public class RuntimeClientPool
{
    private readonly ConcurrentDictionary<string, IRuntimeClient> _clients = new();
    
    public async Task<IRuntimeClient> GetOrCreateAsync(string instanceName)
    {
        return await _clients.GetOrAdd(instanceName, async name =>
        {
            var options = new RuntimeClientOptions { InstanceName = name };
            return await RuntimeClient.ConnectAsync(options);
        });
    }
}
```

## 下一步

- **了解协议细节**：阅读 [协议规范](./04-协议规范/README.md)
- **完整 API 参考**：查看 [IRuntimeClient 接口](./06-API参考/01-IRuntimeClient接口.md)
- **事件类型**：查看 [事件类型参考](./06-API参考/02-事件类型参考.md)
