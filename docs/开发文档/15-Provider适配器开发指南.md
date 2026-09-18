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

所有 Provider 共享 `Providers.Abstractions` 中定义的接口和数据结构。实现项目不得再被 CLI/Server 直接引用；生产路径通过独立 AOT Worker 进程加载，见第 9 节。

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
6. **包装为 Worker 并登记 slug**：见第 9 节。不要把实现项目加回 Server/CLI 的 ProjectReference，也不要在主程序里 `new XxxAdapter`。
7. **加入配置向导**：在 `config init` 的 Provider 选择列表中添加新条目。向导协议字符串与能力/凭据仍以本文第 5–7 节为准。

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

## 8. Provider 工具名策略（`ProviderToolNamePolicy`）

模型侧 function name 字符集通常不允许 `.` 等符号。所有适配器在出站声明工具、历史回放、入站解析 ToolId 时必须走 `Providers.Abstractions.ProviderToolNamePolicy`，禁止各 Provider 私自替换规则。

### 8.1 出站命名 `GetName`（三级回退）

| 优先级 | 条件 | 结果示例 |
| --- | --- | --- |
| 1 | `DisplayName` 已是 Provider 安全名（字母数字 `_` `-`，长度 ≤64） | 原样使用 |
| 2 | `DisplayName` 为**调用名风格**（含 `.` `/` `:` `@` 等轻度非法，**无**空格/中文） | `SanitizeForProvider`：`tool.search` → `tool_search` |
| 3 | 仍不合法（描述性名称如 `Read file`、中文等 **不**压成短名） | `EncodeToolId(ToolId)` 稳定编码 |

- `MaximumNameLength = 64`。
- **禁止**把描述性 DisplayName 压成 `Read_file`，避免模型看到无意义短名。
- 同一请求内 `ValidateUniqueNames`：若两个描述符映射到同一 Provider 名则拒绝请求。

### 8.2 历史名 `GetHistoryName`

优先按 `toolId` 在当前 tools 列表中找描述符并 `GetName`；否则对 `storedName` 再走安全名 / 调用名风格净化 / `EncodeToolId`。

### 8.3 入站回解析 `ResolveToolId`

按顺序匹配，保证「发出去是 `skill_search`、回来能对上 `host.skill.search`」：

1. `GetName(tool) == providerName`
2. 兼容旧行为：`DisplayName` / `ToolId` 精确相等
3. `SanitizeForProvider(DisplayName) == providerName`
4. 仍无命中则原样返回 `providerName`（交上层处理）

### 8.4 公开 API

| 方法 | 用途 |
| --- | --- |
| `GetName` / `GetHistoryName` | 出站与历史 |
| `ResolveToolId` | 入站 |
| `SanitizeForProvider` | 可单测的净化实现 |
| `ValidateUniqueNames` | 发请求前去重校验 |

单测：`ProviderToolNamePolicyTests`。宿主工具如 `skill.search` 经净化后常为 `skill_search`；Skills 激活钩子同时识别 `host.skill.search` / `skill.search` / `skill_search` 及编码 ToolId。

## 9. Worker 包装、slug 与发布清单

HTTP、工具名、凭据、能力投影和配置向导的权威仍在第 2–8 节。本节只补进程隔离后的注册方式。完整规格见 [12-Provider 进程隔离 AOT 按需加载](../方案设计/12-Provider进程隔离AOT按需加载方案.md)。

1. **实现项目**：仍放 `src/Providers/Madorin.AI.Runtime.Providers.<Name>/`，只被对应 Worker 工程引用。
2. **Worker 工程**：`src/Providers/Madorin.AI.Runtime.Providers.<Name>.Worker/`，`AssemblyName=madorin-provider-{slug}`，`Program.cs` 只映射本协议后调用 `ProviderWorkerHost.RunAsync`。
3. **slug**：在 `ProviderProtocolSlugs` 与 `build/provider-workers.json` 同时登记。`Custom` 与 `OpenAI Compatible` 共用 `openai-compatible`，不要为 Custom 再开 Worker。
4. **清单**：发布脚本读 JSON，把 CLI 与 10 个 Worker 发到同一 `bin/`，并写 `providers.manifest.json`（`version`、`rid`、`workers[{protocol,slug,fileName}]`，本版本不写 `download`）。
5. **启动**：Runtime 按 slug 懒启动最多一个 Worker；连接信息走每条 `invoke.connection`，不要按 ApiKey 再开进程。

协议字符串必须保持：`OpenAI`、`Anthropic`、`OpenAI Compatible`、`DeepSeek`、`Kimi`、`Aliyun`、`Azure OpenAI`、`Gemini`、`GLM`、`Custom`、`Ollama`。

---

## 修订记录

| 日期 | 说明 |
| --- | --- |
| 2026-09-18 | 补 §9 Worker 包装 / slug / `providers.manifest.json`；新增步骤改为登记 Worker 而非主程序 DI |
| 2026-09-07 | 新增 §8 ProviderToolNamePolicy（调用名风格短名化、描述性名称不误压、对称回解析） |
