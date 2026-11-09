# C# Provider 实现示例

> 使用 C# 和 Runtime 提供的基础设施快速开发 Provider Worker

本文档演示如何使用 C# 实现一个完整的 Provider Worker，利用 Runtime 提供的 `ProviderWorkerHost` 基础设施。

## 前置条件

- .NET 9.0 或更高版本
- NuGet 包引用：
  - `Madorin.AI.Runtime.Providers.Abstractions`
  - `Madorin.AI.Runtime.Providers.WorkerHost`

## 最小实现

### 1. Program.cs 入口

```csharp
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.WorkerHost;

const string Protocol = "MyProvider";
const string Slug = "myprovider";

// 解析 --slug 参数
var slug = Slug;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--slug")
    {
        slug = args[i + 1];
        break;
    }
}

// 验证 slug
if (!string.Equals(slug, Slug, StringComparison.Ordinal))
{
    Console.Error.WriteLine($"slug '{slug}' is not '{Slug}'.");
    return 1;
}

// 绑定标准流
var streams = ProviderWorkerStreams.Bind();

// 启动 Worker Host
return await ProviderWorkerHost.RunAsync(
    Protocol,
    Slug,
    BuildAdapter,
    streams.Stdin,
    streams.Stdout,
    streams.Stderr);

// Adapter 工厂
static IRuntimeProviderAdapter BuildAdapter(ProviderConnectionConfig config)
{
    ArgumentNullException.ThrowIfNull(config);
    return new MyProviderAdapter(
        config.ProviderId,
        config.ApiKey,
        config.BaseUrl ?? "https://api.example.com/v1",
        config.Models);
}
```

**关键点**：

- `ProviderWorkerHost.RunAsync` 处理所有 IPC 帧协议细节
- `BuildAdapter` 根据每个 `invoke` 的连接配置创建 Adapter 实例
- `ProviderWorkerStreams.Bind()` 绑定 stdin/stdout（支持 Windows Console 重定向修复）

### 2. Adapter 实现

```csharp
using System.Runtime.CompilerServices;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Contracts;

public sealed class MyProviderAdapter : IRuntimeProviderAdapter, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _baseUrl;

    public MyProviderAdapter(
        string providerId,
        string apiKey,
        string baseUrl,
        IReadOnlyList<string>? models)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        ProviderId = providerId;
        _apiKey = apiKey;
        _baseUrl = baseUrl.TrimEnd('/');
        Models = models?.Select(id => new ProviderModel(id, id)).ToList() 
            ?? [];

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromMinutes(10)
        };
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
    }

    public string ProviderId { get; }

    public ProviderCapabilities Capabilities { get; } = new(
        Streaming: true,
        ToolCalling: true,
        Vision: false,
        StructuredOutput: false,
        Reasoning: false,
        Files: false,
        Usage: true,
        RemoteCancellation: false);

    public IReadOnlyList<ProviderModel> Models { get; }

    public Task ValidateCapabilitiesAsync(
        ContentBlock[] input, 
        CancellationToken ct = default)
    {
        return ProviderCapabilityValidator.ValidateAsync(Capabilities, input, ct);
    }

    public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
        RuntimeProviderRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 能力验证
        RuntimeError? failure = null;
        try
        {
            await ProviderCapabilityValidator.ValidateAsync(
                Capabilities,
                request,
                Models,
                ct).ConfigureAwait(false);
        }
        catch (RuntimeProviderException ex)
        {
            failure = ex.Error;
        }

        if (failure is not null)
        {
            yield return new InvocationFailedProviderEvent(request.InvocationId, failure);
            yield break;
        }

        // 构造 API 请求
        var apiRequest = BuildApiRequest(request);

        // 调用 AI 服务
        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient.PostAsJsonAsync(
                "/chat/completions",
                apiRequest,
                ct).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            failure = ProviderErrorFactory.Create(ProviderId, ex, isProtocolError: false);
            yield return new InvocationFailedProviderEvent(request.InvocationId, failure);
            yield break;
        }

        // 流式读取响应
        await using var stream = await response.Content.ReadAsStreamAsync(ct)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        string? finishReason = null;
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: "))
            {
                continue;
            }

            var json = line["data: ".Length..];
            if (json == "[DONE]")
            {
                break;
            }

            // 解析 SSE 事件并映射为 RuntimeProviderEvent
            foreach (var providerEvent in ParseChunk(request.InvocationId, json))
            {
                if (providerEvent is InvocationCompletedProviderEvent completed)
                {
                    finishReason = completed.FinishReason;
                }
                yield return providerEvent;
            }
        }

        // 确保有终止事件
        if (finishReason is null)
        {
            yield return new InvocationCompletedProviderEvent(
                request.InvocationId, 
                "stop");
        }
    }

    private object BuildApiRequest(RuntimeProviderRequest request)
    {
        // 映射 RuntimeProviderRequest -> AI 服务 API 格式
        return new
        {
            model = request.ModelId,
            messages = request.Messages.Select(m => new
            {
                role = m.Role.ToString().ToLowerInvariant(),
                content = string.Join("", m.Content
                    .OfType<TextContentBlock>()
                    .Select(t => t.Text))
            }).ToArray(),
            max_tokens = request.MaxTokens,
            temperature = request.Temperature,
            stream = true
        };
    }

    private IEnumerable<RuntimeProviderEvent> ParseChunk(
        string invocationId, 
        string json)
    {
        // 解析 AI 服务响应并映射为 RuntimeProviderEvent
        // 这里简化演示，实际需根据厂商协议实现
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("choices", out var choices) &&
            choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta))
            {
                if (delta.TryGetProperty("content", out var content))
                {
                    var text = content.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        yield return new OutputDeltaProviderEvent(
                            invocationId, 
                            text);
                    }
                }
            }

            if (choice.TryGetProperty("finish_reason", out var finishReasonElem) &&
                finishReasonElem.ValueKind == JsonValueKind.String)
            {
                var reason = finishReasonElem.GetString();
                if (!string.IsNullOrEmpty(reason))
                {
                    yield return new InvocationCompletedProviderEvent(
                        invocationId, 
                        reason);
                }
            }
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
```

### 3. 项目文件配置

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <AssemblyName>madorin-provider-myprovider</AssemblyName>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Madorin.AI.Runtime.Providers.Abstractions" Version="0.1.*" />
    <PackageReference Include="Madorin.AI.Runtime.Providers.WorkerHost" Version="0.1.*" />
  </ItemGroup>
</Project>
```

**AOT 发布**（可选）：

```bash
dotnet publish -c Release -r win-x64 --self-contained
```

## 完整示例：OpenAI Provider

以下是 Runtime 内置 OpenAI Provider 的简化版本，展示生产级实现的关键模式。

### Program.cs

```csharp
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.OpenAI;
using Madorin.AI.Runtime.Providers.WorkerHost;

const string Protocol = "OpenAI";
const string Slug = "openai";

var slug = Slug;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--slug")
    {
        slug = args[i + 1];
        break;
    }
}

if (!string.Equals(slug, Slug, StringComparison.Ordinal))
{
    Console.Error.WriteLine($"slug '{slug}' is not '{Slug}'.");
    return 1;
}

var streams = ProviderWorkerStreams.Bind();
return await ProviderWorkerHost.RunAsync(
    Protocol,
    Slug,
    BuildAdapter,
    streams.Stdin,
    streams.Stdout,
    streams.Stderr);

static IRuntimeProviderAdapter BuildAdapter(ProviderConnectionConfig config)
{
    ArgumentNullException.ThrowIfNull(config);
    return new OpenAIProviderAdapter(
        config.ProviderId,
        config.ApiKey,
        string.IsNullOrWhiteSpace(config.BaseUrl)
            ? "https://api.openai.com/v1"
            : config.BaseUrl.Trim(),
        ToProviderModels(config.Models));
}

static List<ProviderModel> ToProviderModels(IReadOnlyList<string>? ids) =>
    ids is null || ids.Count == 0
        ? []
        : ids.Select(static id => new ProviderModel(id, id)).ToList();
```

### OpenAIProviderAdapter.cs（核心结构）

```csharp
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Contracts;

public sealed class OpenAIProviderAdapter :
    IRuntimeProviderAdapter,
    IRuntimeProviderCredentialUpdater,
    IDisposable
{
    private readonly Func<string, IChatClient> _chatClientFactory;
    private readonly ConcurrentDictionary<string, IChatClient> _chatClients = new();
    private readonly HttpClient? _ownedHttpClient;
    private readonly ApiKeyCredential? _credential;

    public OpenAIProviderAdapter(
        string providerId,
        string apiKey,
        string baseUrl,
        IReadOnlyList<ProviderModel>? models)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        ProviderId = providerId;
        Models = models ?? [];
        _credential = new ApiKeyCredential(apiKey);

        // 创建 HttpClient
        _ownedHttpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        // 创建 OpenAI 客户端
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(baseUrl.TrimEnd('/'), UriKind.Absolute),
            NetworkTimeout = TimeSpan.FromMinutes(10),
            RetryPolicy = new ClientRetryPolicy(0),
            Transport = new HttpClientPipelineTransport(_ownedHttpClient)
        };
        var responsesClient = new ResponsesClient(_credential, options);

        _chatClientFactory = modelId =>
            responsesClient.AsIChatClient(modelId);
    }

    public string ProviderId { get; }

    public ProviderCapabilities Capabilities { get; } = new(
        Streaming: true,
        ToolCalling: true,
        Vision: true,
        StructuredOutput: true,
        Reasoning: true,
        Files: true,
        Usage: true,
        RemoteCancellation: false);

    public IReadOnlyList<ProviderModel> Models { get; }

    public Task ValidateCapabilitiesAsync(
        ContentBlock[] input,
        CancellationToken ct = default)
    {
        return ProviderCapabilityValidator.ValidateAsync(Capabilities, input, ct);
    }

    public Task UpdateCredentialsAsync(
        CredentialsUpdateParameters parameters,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!string.Equals(parameters.ProviderId, ProviderId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Credential update targets another Provider.");
        }

        _credential?.Update(parameters.Credential);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
        RuntimeProviderRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 能力验证
        RuntimeError? failure = null;
        try
        {
            await ProviderCapabilityValidator.ValidateAsync(
                Capabilities,
                request,
                Models,
                ct).ConfigureAwait(false);
        }
        catch (RuntimeProviderException ex)
        {
            failure = ex.Error;
        }

        if (failure is not null)
        {
            yield return new InvocationFailedProviderEvent(request.InvocationId, failure);
            yield break;
        }

        // 获取或创建 ChatClient
        var chatClient = _chatClients.GetOrAdd(
            request.ModelId,
            modelId => _chatClientFactory(modelId));

        // 转换消息
        var messages = ConvertMessages(request.Messages);

        // 创建选项
        var options = new ChatOptions
        {
            ModelId = request.ModelId,
            MaxOutputTokens = request.MaxTokens,
            Temperature = (float?)request.Temperature,
            TopP = (float?)request.TopP,
            Tools = ConvertTools(request.Tools)
        };

        // 调用流式 API
        IAsyncEnumerator<StreamingChatCompletionUpdate>? enumerator = null;
        try
        {
            enumerator = chatClient.CompleteStreamingAsync(messages, options, ct)
                .GetAsyncEnumerator(ct);
        }
        catch (Exception ex)
        {
            failure = ProviderErrorFactory.Create(ProviderId, ex, isProtocolError: false);
        }

        if (failure is not null || enumerator is null)
        {
            yield return new InvocationFailedProviderEvent(request.InvocationId, failure!);
            yield break;
        }

        // 流式读取响应
        string? finishReason = null;
        await using var responseEnumerator = enumerator;

        while (true)
        {
            StreamingChatCompletionUpdate update;
            try
            {
                if (!await responseEnumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }
                update = responseEnumerator.Current;
            }
            catch (Exception ex)
            {
                failure = ProviderErrorFactory.Create(ProviderId, ex, isProtocolError: false);
                break;
            }

            // 映射更新为事件
            foreach (var providerEvent in MapUpdate(request.InvocationId, update))
            {
                if (providerEvent is InvocationCompletedProviderEvent completed)
                {
                    finishReason = completed.FinishReason;
                }
                yield return providerEvent;
            }
        }

        if (failure is not null)
        {
            yield return new InvocationFailedProviderEvent(request.InvocationId, failure);
            yield break;
        }

        if (finishReason is null)
        {
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }
    }

    private ChatMessage[] ConvertMessages(IReadOnlyList<MessageBlock> messages)
    {
        // 转换 RuntimeProviderRequest.Messages -> ChatMessage[]
        return messages.Select(m => new ChatMessage
        {
            Role = MapRole(m.Role),
            Contents = m.Content.Select(MapContent).ToList()
        }).ToArray();
    }

    private ChatRole MapRole(MessageRole role) => role switch
    {
        MessageRole.User => ChatRole.User,
        MessageRole.Assistant => ChatRole.Assistant,
        MessageRole.System => ChatRole.System,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    private AIContent MapContent(ContentBlock content) => content switch
    {
        TextContentBlock text => new TextContent(text.Text),
        ImageContentBlock image => new ImageContent(image.Source),
        ToolCallContentBlock toolCall => new FunctionCallContent(
            toolCall.ToolUseId,
            toolCall.ToolName,
            JsonSerializer.Serialize(toolCall.Input)),
        ToolResultContentBlock toolResult => new FunctionResultContent(
            toolResult.ToolUseId,
            toolResult.Content),
        _ => throw new ArgumentOutOfRangeException(nameof(content))
    };

    private IList<AITool>? ConvertTools(IReadOnlyList<ToolDefinition>? tools)
    {
        if (tools is null || tools.Count == 0)
        {
            return null;
        }

        return tools.Select(t => new AIFunction(
            t.Name,
            t.Description,
            JsonSerializer.Deserialize<JsonElement>(t.InputSchema))).ToList<AITool>();
    }

    private IEnumerable<RuntimeProviderEvent> MapUpdate(
        string invocationId,
        StreamingChatCompletionUpdate update)
    {
        // 映射流式更新为 RuntimeProviderEvent
        if (update.Text is { Length: > 0 } text)
        {
            yield return new OutputDeltaProviderEvent(invocationId, text);
        }

        if (update.ToolCallUpdate is { } toolCall)
        {
            yield return new ToolCallProviderEvent(
                invocationId,
                toolCall.CallId,
                toolCall.FunctionName,
                toolCall.FunctionArguments);
        }

        if (update.FinishReason is { } reason)
        {
            yield return new InvocationCompletedProviderEvent(
                invocationId,
                reason.ToString());
        }

        if (update.Usage is { } usage)
        {
            yield return new UsageUpdatedProviderEvent(
                invocationId,
                usage.InputTokenCount,
                usage.OutputTokenCount,
                ProviderUsageAccuracy.Accurate);
        }
    }

    public void Dispose()
    {
        _ownedHttpClient?.Dispose();
        foreach (var client in _chatClients.Values)
        {
            if (client is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        _chatClients.Clear();
    }
}
```

## 关键模式

### 1. 连接池与多配置支持

```csharp
private readonly ConcurrentDictionary<string, IChatClient> _chatClients = new();

var chatClient = _chatClients.GetOrAdd(
    request.ModelId,
    modelId => _chatClientFactory(modelId));
```

**原因**：同一 Worker 可服务多个不同的 `BaseUrl/ApiKey`，使用字典缓存客户端实例。

### 2. 能力声明与验证

```csharp
public ProviderCapabilities Capabilities { get; } = new(
    Streaming: true,
    ToolCalling: true,
    Vision: true,
    StructuredOutput: true,
    Reasoning: true,
    Files: true,
    Usage: true,
    RemoteCancellation: false);

public Task ValidateCapabilitiesAsync(ContentBlock[] input, CancellationToken ct)
{
    return ProviderCapabilityValidator.ValidateAsync(Capabilities, input, ct);
}
```

**作用**：Runtime 只会向 Provider 发送其声明支持的能力相关请求。

### 3. 错误处理与事件映射

```csharp
RuntimeError? failure = null;
try
{
    await ProviderCapabilityValidator.ValidateAsync(
        Capabilities, request, Models, ct);
}
catch (RuntimeProviderException ex)
{
    failure = ex.Error;
}

if (failure is not null)
{
    yield return new InvocationFailedProviderEvent(request.InvocationId, failure);
    yield break;
}
```

**原则**：所有错误都映射为 `InvocationFailedProviderEvent`，不要抛出未捕获异常。

### 4. 流式转发

```csharp
while (true)
{
    if (!await responseEnumerator.MoveNextAsync())
    {
        break;
    }
    var update = responseEnumerator.Current;

    foreach (var providerEvent in MapUpdate(request.InvocationId, update))
    {
        yield return providerEvent;
    }
}
```

**性能**：立即转发每个块，不要缓冲整个响应。

### 5. 凭据更新支持

```csharp
public Task UpdateCredentialsAsync(
    CredentialsUpdateParameters parameters,
    CancellationToken ct = default)
{
    if (!string.Equals(parameters.ProviderId, ProviderId, StringComparison.Ordinal))
    {
        throw new ArgumentException("Credential update targets another Provider.");
    }

    _credential?.Update(parameters.Credential);
    return Task.CompletedTask;
}
```

**用途**：支持运行时更新 API Key（通过 `IRuntimeProviderCredentialUpdater`）。

## 测试

### 单元测试

```csharp
[Fact]
public async Task CompleteStreamingAsync_ReturnsEvents()
{
    var adapter = new MyProviderAdapter(
        "test",
        "sk-test",
        "https://api.example.com",
        ["gpt-4"]);

    var request = new RuntimeProviderRequest
    {
        InvocationId = "inv-1",
        ModelId = "gpt-4",
        Messages = [
            new MessageBlock(MessageRole.User, [
                new TextContentBlock("Hello")
            ])
        ],
        MaxTokens = 100
    };

    var events = new List<RuntimeProviderEvent>();
    await foreach (var evt in adapter.CompleteStreamingAsync(request))
    {
        events.Add(evt);
    }

    Assert.Contains(events, e => e is OutputDeltaProviderEvent);
    Assert.Contains(events, e => e is InvocationCompletedProviderEvent);
}
```

### 集成测试（与真实 Runtime）

```bash
# 准备 Provider 配置；交互式向导支持添加自定义兼容协议
madorin config edit

# 直接通过 CLI 测试 Provider（run 不使用 serve 的 --instance 选项）
madorin run --workspace /path/to/project --provider test --input "Hello"
```

`madorin serve` 是独立常驻 Runtime Server，不是 `madorin run` 的后台模式。服务启动需要 `--workspace`，并从受控 stdin 读取握手密钥；Provider 集成协议的具体测试步骤请使用 SDK/协议测试程序连接该实例。

## 发布

### 本地发布

```bash
dotnet publish src/Providers/Madorin.AI.Runtime.Providers.MyProvider.Worker \
  -c Release \
  -r win-x64 \
  --self-contained \
  -o bin/
```

### 集成到 Runtime

1. **注册 slug**

在 `ProviderProtocolSlugs.cs` 添加：

```csharp
public static class ProviderProtocolSlugs
{
    public const string MyProvider = "myprovider";
}
```

2. **更新 `build/provider-workers.json`**

```json
{
  "workers": [
    {
      "protocol": "MyProvider",
      "slug": "myprovider",
      "projectPath": "src/Providers/Madorin.AI.Runtime.Providers.MyProvider.Worker"
    }
  ]
}
```

3. **发布脚本集成**

Runtime 的 `build/publish-all.ps1` 会自动发布所有注册的 Provider。

## 调试技巧

### 1. 查看 Worker 日志

Worker 的 stderr 输出会被 Runtime 捕获并记录：

```csharp
Console.Error.WriteLine($"[MyProvider] Processing invocation {request.InvocationId}");
```

查看日志：

```bash
cat .madorin/logs/runtime-*.log | grep "\[MyProvider\]"
```

### 2. 本地调试

直接运行 Worker 并手动输入帧：

```bash
echo '{"type":"hello","ipcVersion":1,"protocol":"MyProvider","slug":"myprovider","runtimeVersion":"1.0.0"}' | \
  ./madorin-provider-myprovider --slug myprovider
```

### 3. 使用 IDE 调试

在 `launchSettings.json` 配置：

```json
{
  "profiles": {
    "MyProvider": {
      "commandName": "Project",
      "commandLineArgs": "--slug myprovider",
      "environmentVariables": {
        "DOTNET_ENVIRONMENT": "Development"
      }
    }
  }
}
```

然后在 IDE 中设置断点并启动调试。

## 常见问题

### Q: 如何处理超大响应？

A: 使用流式转发，不要缓冲：

```csharp
await foreach (var chunk in ReadStreamAsync(response))
{
    yield return new OutputDeltaProviderEvent(invocationId, chunk);
}
```

### Q: 如何支持取消？

A: `CompleteStreamingAsync` 的 `CancellationToken` 会在 Runtime 发送 `cancel` 帧时被触发：

```csharp
public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
    RuntimeProviderRequest request,
    [EnumeratorCancellation] CancellationToken ct = default)
{
    // ct 会在取消时触发
    await _httpClient.PostAsync("/api", content, ct);
}
```

### Q: 如何支持工具调用？

A: 在能力中声明 `ToolCalling: true`，然后映射 `request.Tools` 到 API 请求：

```csharp
if (request.Tools is { Count: > 0 })
{
    apiRequest.tools = request.Tools.Select(t => new
    {
        type = "function",
        function = new
        {
            name = t.Name,
            description = t.Description,
            parameters = JsonSerializer.Deserialize<JsonElement>(t.InputSchema)
        }
    }).ToArray();
}
```

当 AI 服务返回工具调用时，发送 `ToolCallProviderEvent`：

```csharp
yield return new ToolCallProviderEvent(
    invocationId,
    toolUseId,
    toolName,
    toolInput);
```

## 下一步

- 查看 [语言无关实现指南](./03-语言无关实现指南.md) 了解其他语言实现
- 阅读 [Provider 协议](../04-协议规范/04-Provider协议.md) 了解协议细节
- 参考现有 Provider 实现：
  - `src/Providers/Madorin.AI.Runtime.Providers.OpenAI.Worker/`
  - `src/Providers/Madorin.AI.Runtime.Providers.Anthropic.Worker/`
  - `src/Providers/Madorin.AI.Runtime.Providers.DeepSeek.Worker/`
