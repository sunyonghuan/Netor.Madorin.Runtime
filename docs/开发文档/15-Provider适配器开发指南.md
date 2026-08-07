# Provider 适配器开发指南

> 当前生效版本：Madorin.AI.Runtime V1
>
> 本文描述 Provider 适配器的架构、核心接口和新增 Provider 的步骤。Provider 适配器将不同 AI 模型提供商统一在相同的 Runtime 接口之后，使上层逻辑不感知具体协议差异。

## 1. 概述

Runtime 当前包含 1 个抽象项目（`Abstractions`）与 10 个 Provider 实现：

| Provider | 说明 |
| --- | --- |
| OpenAI | OpenAI 官方 API |
| Anthropic | Anthropic Claude 系列 |
| OpenAICompatible | 兼容 OpenAI 协议的第三方服务 |
| DeepSeek | DeepSeek API |
| Kimi | Moonshot Kimi API |
| Aliyun | 阿里云百炼 / DashScope |
| Gemini | Google Gemini API |
| GLM | 智谱 GLM API |
| Ollama | 本地 Ollama 服务 |
| AzureOpenAI | Azure OpenAI Service（实验性） |
| Abstractions | 公共接口与基类，不含具体实现 |

所有 Provider 共享 `Providers.Abstractions` 中定义的接口和数据结构。新增 Provider 只需实现这些接口并注册到 DI 容器。

## 2. 核心接口

核心接口为 `IRuntimeProviderAdapter`，位于 `Providers.Abstractions` 项目。每个适配器通过 `ProviderCapabilities` 声明自身支持的能力子集：

| 能力标志 | 说明 |
| --- | --- |
| `Streaming` | 流式输出（SSE / chunked） |
| `ToolCalling` | 工具调用（function calling） |
| `Vision` | 图像输入 |
| `Audio` | 音频输入 / 输出 |
| `StructuredOutput` | JSON Schema 约束输出 |
| `Reasoning` | 推理 / 思考链 |
| `PromptCaching` | 提示缓存 |
| `Files` | 文件上传与引用 |
| `ComputerUse` | 计算机操作 |
| `Embeddings` | 向量嵌入 |
| `Usage` | Token 用量上报 |
| `RemoteCancellation` | 远端取消执行 |

未在 `ProviderCapabilities` 中声明的能力，Runtime 不会向该 Provider 发起对应请求。

## 3. Provider 项目组成

每个 Provider 项目的典型目录结构：

```text
Providers.<Name>/
├── Models/            模型目录：已知模型列表及其能力声明
├── Protocol/
│   ├── EventMapper    将 Provider 流式事件映射为 Runtime 事件
│   └── MessageMapper  将 Runtime 标准消息转换为 Provider API 请求格式
├── Http/              HTTP 管道配置（基地址、认证头、超时）
└── RetryPolicy        重试策略（指数退避参数、可重试状态码）
```

各部分职责：

- **Model catalog**：声明每个已知模型的 ID、显示名称、上下文长度和支持的 `ProviderCapabilities` 子集。
- **Event mapper**：将 Provider 的流式 delta 事件（text delta、tool call delta、usage 等）转换为 Runtime 统一的 `RuntimeEventEnvelope`。
- **Message mapper**：将 Runtime 的标准消息结构（`RuntimeMessage`）转换为 Provider API 的请求体格式。
- **HTTP pipeline**：配置 `HttpClient` 的基地址、认证方式、超时和日志。
- **Retry policy**：定义可重试的 HTTP 状态码、最大重试次数和退避间隔。

## 4. 新增 Provider 步骤

按以下顺序实现一个新 Provider：

1. **创建项目**：在 `src/Providers/` 下新建 `Madorin.AI.Runtime.Providers.<Name>`，引用 `Providers.Abstractions`。
2. **实现 `IRuntimeProviderAdapter`**：实现核心成员，包括流式方法 `CompleteStreamingAsync(RuntimeProviderRequest, CancellationToken)` 与输入校验方法 `ValidateCapabilitiesAsync`；已知模型通过只读属性 `IReadOnlyList<ProviderModel> Models` 暴露（非方法）。在线模型发现不属于核心接口，需另行实现 `IRuntimeProviderModelCatalogSource.FetchModelsAsync`。
3. **定义模型目录**：在 `Models/` 下声明已知模型列表，每个模型标注其支持的 `ProviderCapabilities`。
4. **实现 Event mapper**：将 Provider 的流式 delta 事件映射为 Runtime 标准事件（`output.delta`、`tool.call.start`、`run.completed` 等）。
5. **实现 Message mapper**：将 Runtime 标准消息（system / user / assistant / tool result）转换为 Provider API 请求体。
6. **注册到 DI 容器**：在 `ServiceCollection` 扩展中注册适配器，使其可通过 Provider ID 解析。
7. **加入配置向导**：在 `config init` 的 Provider 选择列表中添加新条目。

## 5. 配置与发现

Provider 在配置初始化向导（`config init`）中作为可选项出现。选择 Provider 后可使用 `discover` 关键词触发在线模型发现：

- 发现超时为 10 秒，超时或网络失败时优雅降级，不阻塞配置流程。
- AzureOpenAI 标记为实验性，默认不在向导列表中显示。
- `custom` 协议类型映射到 `OpenAICompatible` 适配器，用于接入未被专门适配的 OpenAI 兼容服务。

## 6. 能力投影

Runtime 在运行期将 Provider 声明的 `ProviderCapabilities` 与当前模型的实际支持范围取交集，得到最终生效的能力集合。当能力集合发生变化时（例如切换模型后某些能力不再可用），Runtime 发出 `provider.projection.adjusted` 事件通知宿主。

宿主应监听该事件并据此更新 UI 状态（如禁用不支持的工具调用按钮）。

## 7. 凭据管理

Provider 的 API Key 来源有两种：

- **配置文件**：通过 `config init` 或 `profile` 命令写入本地配置。
- **宿主注入**：宿主通过 Client SDK 在 Session 创建时传入凭据。

当 Provider 返回认证失败（HTTP 401 / 403）时，Runtime 发出 `credentials.refresh.request` 事件。宿主收到该事件后通过 `UpdateCredentialsAsync` 提供新凭据，Runtime 自动重试失败的请求。`UpdateCredentialsAsync` 定义在可选接口 `IRuntimeProviderCredentialUpdater`，并非核心适配器成员；适配器需额外实现该可选接口才支持凭据热更新，否则认证失败不会触发运行时重试。

独立 CLI 模式下不提供运行时凭据刷新，认证失败直接终止当前 Run。
