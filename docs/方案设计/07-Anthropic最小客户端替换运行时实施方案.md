# Anthropic.Minimal 替换 Runtime 实施方案

## 1. 文档定位

本文是 `Netor.Madorin.Ai.Runtime` 仓库的实施文档，目标是将 Anthropic Provider 的底层客户端从完整 `Netor.Anthropic` 替换为 `Netor.Anthropic.Minimal.Extensions`，同时保持 Runtime 的 Provider 契约、MAF 执行链、能力声明和商业功能不变。

目标仓库的配套修改见：

- `Netor.Anthropic/docs/Anthropic最小客户端协议补充与扩展实施方案.md`

本方案不是降低能力范围的瘦身方案。`Streaming`、`ToolCalling`、`Vision`、`StructuredOutput`、`Reasoning`、`Files`、`Usage` 必须全部支持并通过验收。瘦身只针对完整生成 SDK 的无关 API、模型和依赖，不针对 Anthropic Messages API 的实际协议能力。

## 2. 目标架构

```text
Madorin.AI.Runtime.Providers.Anthropic
  AnthropicProviderAdapter                 Runtime Provider 语义与生命周期
  AnthropicRuntimeRequestMapper            Runtime -> MEAI/Minimal 映射
  AnthropicRuntimeResponseMapper           MEAI/Minimal -> Runtime 事件
              |
              v
Netor.Anthropic.Minimal.Extensions         IChatClient/MEAI 扩展
  AnthropicMinimalChatClient               MAF/MEAI 兼容实现
  ChatMessage/AIContent/Tool/Usage 映射
              |
              v
Netor.Anthropic.Minimal                    Anthropic 协议通讯核心
  HTTP、认证、JSON、SSE、Messages API、图片/文档/工具/Thinking/结构化输出
```

职责边界：

| 层 | 必须负责 | 不负责 |
| --- | --- | --- |
| `Anthropic.Minimal` | Anthropic HTTP、请求 wire model、响应 wire model、SSE 分帧、协议错误、source-generated JSON | Runtime 类型、MAF、Provider 重试策略、工具授权 |
| `Anthropic.Minimal.Extensions` | `IChatClient`、`ChatMessage`、`AIContent`、`ChatResponseUpdate`、工具和 Usage 的 MEAI 映射 | Runtime Session、Blob Store、ProviderId、凭据更新策略 |
| Runtime Anthropic Provider | Runtime 请求校验、能力声明、工具命名、Blob Resolver、凭据轮换、RuntimeProviderEvent、错误归一化 | 重新实现 Anthropic HTTP/SSE 协议 |

## 3. 当前项目依赖改造

修改：

- `src/Providers/Madorin.AI.Runtime.Providers.Anthropic/Madorin.AI.Runtime.Providers.Anthropic.csproj`
- `Directory.Packages.props`
- `src/Providers/Madorin.AI.Runtime.Providers.Anthropic/AnthropicProviderAdapter.cs`
- 必要时新增 `Protocol/AnthropicRuntimeRequestMapper.cs`、`Protocol/AnthropicRuntimeResponseMapper.cs`

删除：

```xml
<PackageReference Include="Netor.Anthropic" />
```

替换为：

```xml
<PackageReference Include="Netor.Anthropic.Minimal.Extensions" />
```

保留：

```xml
<PackageReference Include="Microsoft.Agents.AI" />
<PackageReference Include="Microsoft.Extensions.Http" />
```

`Microsoft.Agents.AI` 属于 Runtime 的编排能力，不属于被替换的 Anthropic SDK；不能为了瘦身移除。完整 `Netor.Anthropic` 才是本次移除对象。

中央包版本增加：

```xml
<PackageVersion Include="Netor.Anthropic.Minimal.Extensions" Version="x.y.z" />
```

版本必须固定，不允许使用浮动版本。Runtime 发布前需确认 `Netor.Anthropic.Minimal.Extensions` 与 `Netor.Anthropic.Minimal` 的版本一致。

## 4. Adapter 公共兼容面

以下公共构造函数、接口和属性必须保留：

```csharp
AnthropicProviderAdapter(
    IChatClient chatClient,
    IReadOnlyList<ProviderModel>? models = null,
    IProviderBlobResolver? blobResolver = null);

AnthropicProviderAdapter(
    string apiKey,
    string baseUrl,
    IReadOnlyList<ProviderModel>? models = null,
    IProviderBlobResolver? blobResolver = null);

AnthropicProviderAdapter(
    string providerId,
    string apiKey,
    string baseUrl,
    IHttpClientFactory httpClientFactory,
    IReadOnlyList<ProviderModel>? models = null,
    IProviderBlobResolver? blobResolver = null);
```

保留：

- `IRuntimeProviderAdapter`
- `IRuntimeProviderCredentialUpdater`
- `IDisposable`
- `ProviderId` 与 `Models` 行为
- `ConfigurationSchema`
- `Capabilities` 全能力声明
- `CompleteStreamingAsync` 的 Runtime 事件协议

可以删除的旧类型依赖：

- `IAnthropicClient`
- `AnthropicClient`
- `Anthropic.Core.ClientOptions`
- `Anthropic.Exceptions.*`
- `AsIChatClient` 的完整 SDK 扩展方法

新的内部创建逻辑：

```csharp
var minimal = new AnthropicMinimalClient(
    new AnthropicMinimalClientOptions
    {
        BaseUrl = baseUrl,
        HttpClient = providerHttpClient,
        ApiKey = null
    });

var chatClient = minimal.AsIChatClient(defaultModelId);
```

API Key 由 Runtime 的 `ProviderAuthenticationHandler` 动态注入，不能同时在 Minimal options 和 Handler 中重复注入。

## 5. 能力契约

Adapter 必须继续声明：

```csharp
new ProviderCapabilities(
    Streaming: true,
    ToolCalling: true,
    Vision: true,
    StructuredOutput: true,
    Reasoning: true,
    Files: true,
    Usage: true,
    RemoteCancellation: false);
```

能力打开的前提不是“类型存在”，而是以下端到端链路全部可用：

| 能力 | Runtime 输入 | Minimal/Extensions 协议 | Runtime 输出 |
| --- | --- | --- | --- |
| Streaming | `CompleteStreamingAsync` | SSE、增量文本、终止事件 | `TextDeltaProviderEvent`、Completed |
| ToolCalling | `ToolDescriptor`、ToolCall/Result history | `tools`、`tool_use`、`tool_result`、input JSON delta | ToolCallDelta/Complete |
| Vision | `BlobRefContentBlock` | image source/base64 或 URL | 请求成功且内容类型保持 |
| Files | Blob/document 输入 | document content block；若使用 Files API 则走独立服务 | 请求成功且无静默降级 |
| StructuredOutput | `RuntimeStructuredOutput` | Anthropic output format/json schema | 结构化响应可解析 |
| Reasoning | `ReasoningContentBlock` | thinking、redacted thinking、signature | ReasoningDelta + extension |
| Usage | 响应流 | message_start/message_delta usage | `UsageUpdatedProviderEvent` |

若某个能力在目标包尚未完成，禁止合并 Runtime 包版本，禁止暂时把能力改为 false 来绕过验收。目标项目必须先完成协议补充。

## 6. 请求映射对标

Runtime 到 `Anthropic.Minimal.Extensions` 的映射必须固定如下：

| Runtime | MEAI/Minimal | 处理要求 |
| --- | --- | --- |
| `RuntimeProviderRequest.ModelId` | `ChatOptions.ModelId` / Minimal model | 必须原样传递 |
| `MaxTokens` | `MaxOutputTokens` / `max_tokens` | null 时使用 Provider 默认策略，不发送 0 |
| `Temperature` | `Temperature` / `temperature` | null 时省略 |
| `system` 消息 | `ChatOptions.Instructions` 或 Minimal system | 不得作为普通 user 消息重复发送 |
| user 文本 | `TextContent` | 保持顺序和 Unicode |
| assistant 文本 | `TextContent` | 保持 assistant role |
| assistant 工具调用 | `FunctionCallContent` -> `tool_use` | 保留 CallId、ToolId、Name、JSON Input |
| Runtime tool 消息 | `FunctionResultContent` -> user/tool_result | Anthropic role 规则由 Extensions 统一处理 |
| `ToolDescriptor` | `AIFunctionDeclaration` -> `MinimalTool` | schema 必须是 object JSON schema |
| `BlobRefContentBlock` | `DataContent` -> image/document | 先经 Blob Resolver 校验长度、SHA256、ContentType |
| `ReasoningContentBlock` | `TextReasoningContent` -> thinking | signature 属于 Anthropic provider extension |
| `RuntimeStructuredOutput` | `ChatResponseFormatJson` -> Anthropic output format | 不能改成 prompt 文本约束 |

工具命名必须继续使用 `ProviderToolNamePolicy`。Extensions 不得自行生成另一套工具 ID 规则。

## 7. 流式响应映射对标

Anthropic SSE 事件到 MEAI 更新，再到 Runtime 事件的链路必须支持：

| Anthropic SSE | Extensions | Runtime |
| --- | --- | --- |
| `message_start` | response metadata/model/initial usage | 保存响应上下文 |
| `content_block_start` text | TextContent start | 初始化文本块 |
| `content_block_start` thinking | TextReasoningContent start | 初始化 reasoning 块 |
| `content_block_start` tool_use | FunctionCallContent start | 初始化工具调用状态 |
| `content_block_delta.text_delta` | TextContent delta | `TextDeltaProviderEvent` |
| `content_block_delta.thinking_delta` | TextReasoningContent delta | `ReasoningDeltaProviderEvent` |
| `content_block_delta.signature_delta` | ProtectedData/provider extension | reasoning extension |
| `content_block_delta.input_json_delta` | FunctionCall JSON delta | `ToolCallDeltaProviderEvent` |
| `content_block_stop` | completed content | ToolCallComplete 或块收尾 |
| `message_delta.stop_reason` | FinishReason | 保存完成原因 |
| `message_delta.usage` | UsageContent | `UsageUpdatedProviderEvent` |
| `message_stop` | terminal update | `InvocationCompletedProviderEvent` |
| SSE error / HTTP error | exception | `InvocationFailedProviderEvent` |

Runtime 不应自行解析原始 SSE。所有 SSE framing、事件 JSON 解析和 Anthropic 事件类型判断由 Minimal/Extensions 完成。

## 8. 凭据、HttpClient 和取消

构造链必须保持：

```text
ProviderAuthenticationHandler
    -> ProviderHttpResponseHandler
    -> HttpClient
    -> AnthropicMinimalClient
    -> AnthropicMinimalChatClient
```

要求：

1. `x-api-key` 由 `ProviderAuthenticationHandler` 添加。
2. `UpdateCredentialsAsync` 后下一次请求必须使用新 Key。
3. 不得在共享 `HttpClient.DefaultRequestHeaders` 中累积多个 Key。
4. `CancellationToken` 必须贯穿 MAF、Extensions、Minimal、`HttpClient.SendAsync`。
5. 流式请求必须使用 `ResponseHeadersRead`。
6. 客户端 Dispose 只能释放 Runtime 自己创建的资源。注入的外部客户端不能被错误释放。

## 9. 错误归一化

Extensions 将 Minimal 异常映射为 `HttpRequestException` 或保留 `AnthropicMinimalException`；Runtime 最终统一调用 `ProviderErrorFactory.Create`。

至少保留：

- HTTP status code
- Anthropic response body（脱敏后）
- 是否协议错误
- 是否可重试
- 原始异常类型

API Key、Authorization、Blob 内容和完整请求体不得进入日志。

## 10. 代码改造步骤

1. 等待 `Netor.Anthropic.Minimal` 完成完整 Messages/SSE/Content/Structured Output 协议。
2. 等待 `Netor.Anthropic.Minimal.Extensions` 完成 `IChatClient` 适配并发布同版本包。
3. 修改中央包管理和 Anthropic Provider csproj。
4. 将 Adapter 的客户端构造从完整 SDK 改为 `AnthropicMinimalClient` + Extensions。
5. 删除 `Anthropic`、`Anthropic.Core`、`Anthropic.Exceptions` using。
6. 保留 MAF 的 `MafSkillsAgentFactory` 和 Runtime 的工具/Blob/错误逻辑。
7. 只在编译器提示 API 差异时增加兼容 shim，不把 Minimal 类型泄漏到 Runtime 公共 API。
8. 刷新 CodeMap overlay：`index_refresh_overlay`。
9. 完成测试矩阵后再升级包版本。

## 11. 验收测试矩阵

必须在 Runtime 仓库执行：

```powershell
dotnet restore .\Madorin.AI.Runtime.slnx
dotnet build .\Madorin.AI.Runtime.slnx -c Debug
dotnet test .\tests\Madorin.AI.Runtime.Provider.Tests\Madorin.AI.Runtime.Provider.Tests.csproj -c Debug
dotnet test .\Madorin.AI.Runtime.slnx -c Debug --no-build
```

Anthropic 专项必须覆盖：

- 普通文本非流式与流式
- 多轮文本历史
- tool declaration
- tool_use 增量 JSON
- tool_result 历史回传
- 多工具并发/顺序调用
- thinking delta 与 signature
- redacted thinking
- image 输入
- document/file 输入
- structured output schema
- usage input/output token
- stop reason：`end_turn`、`tool_use`、`max_tokens`、`stop_sequence`
- HTTP 4xx/5xx
- SSE error
- cancellation
- credential rotation
- Native AOT Debug/Release publish

发布前必须执行至少一次真实 Anthropic smoke：普通文本、流式、工具调用各一次。

## 12. 完成判定

只有同时满足以下条件才算替换完成：

1. Runtime 不再引用 `Netor.Anthropic`。
2. Runtime 公共 Provider 契约无破坏性变化。
3. 全部能力仍声明为 true 且端到端测试通过。
4. `Anthropic.Minimal.Extensions` 可被其他 .NET 项目独立引用。
5. Native AOT 无未处理 trim 警告或运行时反射缺失。
6. 包体积对比报告显示完整 SDK 依赖已从 Runtime 发布闭包中移除。
7. 真实 smoke 和本地 fake SSE 测试均通过。

## 13. 回滚策略

若发布验收失败，只回滚 Runtime 的包版本和 Adapter 构造适配，不回滚目标仓库的协议补充。目标仓库新增协议应通过独立版本继续演进。

回滚版本必须恢复：

```xml
<PackageReference Include="Netor.Anthropic" />
```

并保留本方案文档及失败测试记录，禁止以降低 Capabilities 的方式伪造通过。
