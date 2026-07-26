# Madorin.AI.Runtime Provider 扩展迁移详细方案 : 100%

> 文档状态：已完成
>
> 编制日期：2026-07-26
>
> 迁移来源：`Src/Netor.Cortana.AI/Drivers/Providers`
>
> 目标目录：`Src/Madorin.Ai.Runtime/src/Providers`
>
> 本文已完成实施；迁移实施进度为 100%。真实 Provider 冒烟仍受外部凭据约束，Azure OpenAI 在取得真实资源成功证据前保持实验隐藏。

## 0. 方案结论

本次迁移采用“复刻 Provider 能力，不复制旧 Driver 架构”的方式：

1. 以当前 `IRuntimeProviderAdapter`、Canonical Message、Runtime Provider Event、Provider 能力声明和独立 CLI 配置模型为唯一目标契约。
2. 不引用 `Netor.Cortana.AI`、`Netor.Cortana.Entitys`、旧 `AiProviderDriverBase`、旧 Driver Registry 或旧 Provider 实体。
3. 对存在厂商协议差异的 Provider 建立独立 Provider 项目；对只有名称差异、实际完全使用 OpenAI Compatible 协议的 Provider 复用现有 `OpenAICompatibleProviderAdapter`，不制造空壳实现。
4. 所有新增 Provider 必须同时接通独立 CLI、宿主 Resolver、Expert/Meeting/Work 三种运行模式、Doctor、配置校验、离线一致性测试和 Native AOT 发布。
5. 旧 Aliyun/OpenAI Driver 中的图片与视频生成不属于当前 `IRuntimeProviderAdapter` 的聊天流式契约。本批先迁移可被当前 Runtime 实际调用的聊天、工具、推理、用量、模型发现和错误语义；媒体生成必须经过独立协议扩展评审后实施，不能把不可达方法直接塞进 Provider 项目。
6. 旧 Azure OpenAI Driver 当前标记为禁用且没有模型发现实现。新方案不照搬这一不完整状态，也不在缺少协议测试时对外宣称可用；Azure OpenAI 必须通过独立请求路径、认证头和 API Version 验证后再进入默认可选列表。

## 1. 设计依据与约束优先级

实施时按以下顺序解释约束，后项与前项冲突时以后项为准：

1. `Docs/未来策划/独立CLI/README.md`
2. `Docs/未来策划/独立CLI/方案设计/01-实施方案-V1.md`
3. `Docs/未来策划/独立CLI/方案设计/02-架构修订-V1.md`
4. `Docs/未来策划/独立CLI/方案设计/03-实现框架参考.md`
5. `Docs/未来策划/独立CLI/备档/执行步骤/04-Provider适配层.md`
6. `Docs/未来策划/独立CLI/执行步骤/04A-独立配置与选择.md`
7. 当前源码与测试所冻结的公开行为

必须遵守的工程基线：

- .NET SDK `10.0.301`、目标框架 `net10.0`、C# `14.0`。
- `Nullable`、`TreatWarningsAsErrors`、`EnforceCodeStyleInBuild` 和 `IsAotCompatible` 已启用。
- Release CLI 使用 Native AOT，反射序列化默认关闭。
- NuGet 版本由 `Directory.Packages.props` 集中管理。
- `Providers.Abstractions` 只能引用 Runtime 自有 Contracts/Tools 抽象，不得引用 MAF、MEAI 或厂商 SDK。
- Provider 实现可以引用协议实现包，但不得将 SDK 类型暴露到 Abstractions、Contracts、Client 或宿主接口。
- API Key、Authorization、完整 Prompt 和敏感响应体不得进入日志、异常、配置展示或诊断包。

## 2. 当前实现盘点

### 2.1 目标 Runtime 已有 Provider

| Provider 项目 | 当前职责 | 状态 |
| --- | --- | --- |
| `Madorin.AI.Runtime.Providers.Abstractions` | Adapter、能力、模型、请求、事件、HTTP、认证、错误和扩展字段 | 已有 |
| `Madorin.AI.Runtime.Providers.OpenAI` | OpenAI Responses/Chat 适配、消息和事件映射 | 已有 |
| `Madorin.AI.Runtime.Providers.Anthropic` | Anthropic Messages/Streaming 适配 | 已有 |
| `Madorin.AI.Runtime.Providers.OpenAICompatible` | 可配置端点、认证头、能力、模型和探测路径 | 已有 |
| `Madorin.AI.Runtime.Providers.DeepSeek` | DeepSeek reasoning 回传和 HTTP 覆写 | 已有 |
| `Madorin.AI.Runtime.Providers.Kimi` | Kimi thinking/partial 协议覆写 | 已有 |
| `Madorin.AI.Runtime.Providers.Aliyun` | DashScope Compatible Chat、远程模型目录和错误映射 | 已迁移 |
| `Madorin.AI.Runtime.Providers.Gemini` | Google Compatible Chat 和远程模型目录 | 已迁移 |
| `Madorin.AI.Runtime.Providers.GLM` | GLM 身份、Compatible Chat 和可选模型探测 | 已迁移 |
| `Madorin.AI.Runtime.Providers.Ollama` | 本地 Compatible Chat、无认证模式和 `/api/tags` | 已迁移 |
| `Madorin.AI.Runtime.Providers.AzureOpenAI` | Deployment/API Version 路径和 `api-key` 认证 | 已迁移，实验隐藏 |

当前 `StandaloneProviderFactory` 支持以下协议字符串：

```text
OpenAI
Anthropic
OpenAI Compatible
DeepSeek
Kimi
Aliyun
Azure OpenAI
Gemini
GLM
Custom
Ollama
```

协议列表已收口到内部 `StandaloneProviderProtocolCatalog`，`StandaloneProviderFactory`、`StandaloneConfigValidator` 和 `ConfigWizard` 共用同一目录。Azure OpenAI 可由显式配置使用，但在真实资源门禁通过前不进入默认向导。

### 2.2 旧 Cortana Provider 清单

| 旧 Driver | 旧实现特征 | 目标现状 | 本次结论 |
| --- | --- | --- | --- |
| `OpenAiProviderDriver` | OpenAI Compatible 基类；另有图片/视频生成 | 已有 OpenAI Adapter | 不重复迁移；媒体能力另行评审 |
| `AnthropicProviderDriver` | Anthropic 原生客户端 | 已有 Anthropic Adapter | 不重复迁移 |
| `DeepseekProviderDriver` | Compatible + reasoning HTTP 覆写 | 已有 DeepSeek Adapter | 不重复迁移 |
| `KimiProviderDriver` | Compatible + thinking/partial 覆写 | 已有 Kimi Adapter | 不重复迁移 |
| `AliyunProviderDriver` | Compatible Chat；DashScope 图片与异步视频任务 | Chat 与模型目录已迁移 | 专用项目承载聊天；媒体保持后续门禁 |
| `GeminiProviderDriver` | Google OpenAI Compatible 端点；远程模型列表 | 已迁移 | 专用项目组合 Compatible |
| `OllamaProviderDriver` | OpenAI Compatible Chat；`/api/tags` 模型发现 | 已迁移 | 专用项目组合 Compatible，默认无认证 |
| `GlmProviderDriver` | 仅提供 GLM 身份，行为完全继承 Compatible 基类 | 已迁移 | 轻量专用项目内部组合 Compatible |
| `AzureOpenAiProviderDriver` | Compatible 基类；旧定义禁用；模型列表为空 | 已迁移，实验隐藏 | 专用项目固定 URL、认证和 API Version；真实资源通过后再启用 |
| `CustomProviderDriver` | 仅提供 Custom 身份，行为完全继承 Compatible 基类 | 已迁移为别名 | `Custom` 复用 Compatible，不新建重复项目 |

### 2.3 当前 Runtime 调用链

```text
StandaloneConfig / Host ProviderResolver
    -> NextTurnSelection.ProviderId
    -> Func<NextTurnSelection, IRuntimeProviderAdapter>
    -> LocalRuntime / RuntimeServer
    -> Expert / Meeting / Work Orchestrator
    -> ProviderAgentInvocationExecutor
    -> IRuntimeProviderAdapter.CompleteStreamingAsync
    -> RuntimeProviderEvent
    -> Runtime 事件、持久化与 Client 输出
```

新增 Provider 只有进入同一个 Resolver 和 Adapter 调用链，才算兼容当前程序架构。不得为某个 Provider 建立绕开 Runtime 状态机、工具网关、事件 Outbox 或持久化链路的第二套执行器。

### 2.4 当前契约缺口

当前 `IRuntimeProviderAdapter` 已覆盖：

- Provider ID、能力、配置 Schema 和模型目录。
- Token 估算和能力探测。
- Canonical Message 到流式 Provider Event 的调用。
- 文本、推理、工具调用、用量、完成和失败事件。
- CancellationToken 和凭据更新扩展。

当前契约没有覆盖：

- 运行时远程刷新模型目录的角色接口。
- 图片生成、视频生成及异步媒体任务事件。
- Azure OpenAI 的 Deployment/API Version 等专用配置语义。

因此迁移不能逐方法复制旧 Driver；必须按目标契约区分“本批可迁移能力”和“需要新增 Runtime 能力的功能”。

## 3. 范围定义

### 3.1 本批必须完成

- Aliyun、Gemini、Ollama、GLM 和 Azure OpenAI 的目标架构 Provider 项目。
- Custom 对 OpenAI Compatible 的显式协议别名。
- 每个 Provider 的配置 Schema、默认端点策略、认证策略、模型目录策略和保守能力声明。
- 独立 CLI 的协议选择、配置创建/修改/校验、Adapter 创建和 Doctor 探测。
- 宿主 Resolver 和三种运行模式可使用任意配置实例 ID 解析 Provider。
- 远程模型发现的可选角色接口及 Gemini/Ollama/Aliyun 的实现。
- 统一错误、取消、限流、凭据续期、HTTP 生命周期和脱敏行为。
- 离线一致性测试、Provider 专项测试、三模式端到端测试和可选真实冒烟测试。
- Debug、Release、完整测试和 win-x64 Native AOT 发布门禁。

### 3.2 本批明确不做

- 不复制旧 `AiProviderDriverBase`、`AiProviderDriverRegistry` 或 Provider 实体模型。
- 不让新项目引用 `Netor.Cortana.*` 程序集。
- 不建立第二套 Provider Profile、远程配置中心或配置热更新框架。
- 不根据模型名称或偶然响应字段猜测能力。
- 不把 Provider SDK Session 当作 Runtime 对话权威状态。
- 不在本批扩展公开 Client/IPC 协议以支持图片和视频生成。
- 不为 Custom 创建只改 `ProviderId` 的空壳 Provider 项目。

### 3.3 媒体生成后续范围

若确认必须同时复刻旧 Aliyun/OpenAI 图片与视频能力，应单独建立“Runtime 媒体生成协议扩展”策划，至少同时处理：

- `IRuntimeImageGenerationAdapter`、`IRuntimeVideoGenerationAdapter` 等窄角色接口。
- 图片/视频请求、任务状态、进度、失败、取消和完成契约。
- 大型二进制结果进入 Blob Store，而不是直接塞入普通 JSON/Event。
- OpenAI 与 Aliyun 两个实现共同接入，避免只给 Aliyun 添加私有旁路。
- 异步视频任务轮询的超时、断线恢复、幂等键和持久化检查点。
- Client SDK、CLI 命令、权限、配额和 Native AOT JSON 元数据。

在上述协议完成前，Aliyun Adapter 只声明并实现当前 Runtime 可消费的能力，不伪报图片或视频生成可用。

## 4. 核心架构决策

### 4.1 Provider 项目边界

计划新增：

```text
src/Providers/
├── Madorin.AI.Runtime.Providers.Aliyun/
├── Madorin.AI.Runtime.Providers.Gemini/
├── Madorin.AI.Runtime.Providers.Ollama/
├── Madorin.AI.Runtime.Providers.GLM/
└── Madorin.AI.Runtime.Providers.AzureOpenAI/
```

每个项目只允许引用：

- `Madorin.AI.Runtime.Providers.Abstractions`。
- 确有需要时引用 `Madorin.AI.Runtime.Providers.OpenAICompatible`。
- 已纳入中央包管理、且通过 AOT 评审的协议 SDK。

优先使用组合，不从旧 Driver 基类迁移继承关系。纯 OpenAI Compatible 行为由专用 Adapter 持有一个 `OpenAICompatibleProviderAdapter` 并转发目标接口；只有端点、认证、模型发现或协议转换存在差异时才在专用项目中实现代码。

### 4.2 Provider 实例 ID 与协议类型分离

`ProviderEntry.Name` 是配置实例 ID，例如用户可以同时配置：

```text
aliyun-cn
aliyun-international
ollama-local
ollama-lan
```

`ProviderEntry.Protocol` 决定使用哪一种 Adapter。新增 Adapter 构造函数必须接收 `providerId`，并保证 `adapter.ProviderId == ProviderEntry.Name`。不得把 `aliyun`、`gemini`、`ollama` 等协议名硬编码成所有实例共享的 Provider ID。

### 4.3 独立 CLI 协议目录

新增内部 `StandaloneProviderProtocolCatalog`，集中描述：

- 稳定协议名和显示名。
- 是否在向导中可选。
- 默认 BaseURL 和默认模型提示。
- BaseURL、API Key 是否必填。
- 默认认证头和认证 Scheme。
- 默认模型探测路径。
- Adapter 构建委托。

`StandaloneProviderFactory`、`StandaloneConfigValidator` 和 `ConfigWizard` 统一从该目录读取，不再分别维护 switch/HashSet/提示文本。

协议名保持向后兼容，不修改已有五个字符串。新增协议建议为：

```text
Aliyun
Gemini
Ollama
GLM
Azure OpenAI
Custom
```

`Custom` 的构建委托直接进入现有 OpenAI Compatible Adapter；`Azure OpenAI` 在专项验收完成前可以被配置文件显式使用，但不出现在默认交互向导中。

### 4.4 远程模型发现采用可选角色接口

旧 Driver 的 `FetchModelsAsync` 不能直接塞入已经冻结的主 Adapter 接口。建议在 `Providers.Abstractions` 增加窄角色接口：

```csharp
public interface IRuntimeProviderModelCatalogSource
{
    ValueTask<IReadOnlyList<ProviderModel>> FetchModelsAsync(
        CancellationToken ct = default);
}
```

约束如下：

- 主 Runtime 执行仍只依赖 `IRuntimeProviderAdapter`，不要求所有 Provider 支持远程发现。
- `IRuntimeProviderAdapter.Models` 是当前配置快照的冻结模型目录，运行中的 Invocation 不因远程刷新而改变模型语义。
- `config init/edit` 和 `doctor` 通过类型检测调用可选接口；发现失败时显示脱敏错误并允许用户手工输入，不能写入半有效配置。
- 返回结果只包含 Runtime 自有 `ProviderModel`，不得暴露厂商 DTO。
- 响应读取必须有字节上限、CancellationToken、协议校验和去重排序。
- 模型能力只来自已验证目录或用户显式配置，不能按模型名称猜测。

首批实现：

| Provider | 模型发现来源 |
| --- | --- |
| Aliyun | DashScope OpenAI Compatible `models` 端点 |
| Gemini | Google OpenAI Compatible `models` 端点 |
| Ollama | 去除 BaseURL 末尾 `/v1` 后调用 `/api/tags` |
| GLM | 支持标准 `models` 时调用；否则仅使用配置模型 |
| Azure OpenAI | 不自动枚举 Deployment；使用配置模型 |
| Custom | 复用 OpenAI Compatible 的可选探测路径 |

### 4.5 能力声明策略

Provider 总能力与模型能力取交集。新增 Provider 初始能力必须保守：

1. 只有离线协议测试或真实冒烟测试证明的能力才声明为 `true`。
2. `Streaming`、`ToolCalling`、`Reasoning`、`Vision`、`StructuredOutput`、`Usage` 分别验证，不能因为端点“兼容 OpenAI”就全部开启。
3. 用户配置的能力只能收紧或明确覆盖通用兼容 Adapter，不能越过 Provider 专项硬限制。
4. 不支持能力必须在发送 HTTP 请求前返回稳定 `CapabilityNotSupported`。
5. Provider 未返回精确 Usage 时使用 `Unknown` 或 `Estimated`，不得填充伪造值。
6. 所有新增 Provider 初始保持 `RemoteCancellation=false`，除非厂商存在已实现并验证的远端取消 API。

### 4.6 配置和认证

各 Adapter 通过 `ProviderConfigurationSchema` 声明字段要求，CLI 协议目录只负责独立配置的交互和构建规则。

| Provider | API Key | BaseURL | 认证方式 | 模型 |
| --- | --- | --- | --- | --- |
| Aliyun | 必填 | 有默认值，可覆盖 | `Authorization: Bearer` | 可发现或手工配置 |
| Gemini | 必填 | 有默认值，可覆盖 | `Authorization: Bearer` | 可发现或手工配置 |
| Ollama | 默认不需要 | 默认 `http://localhost:11434/v1` | 显式无认证；可选兼容网关认证 | `/api/tags` 或手工配置 |
| GLM | 必填 | 实施时按已验证端点给默认值，否则必填 | `Authorization: Bearer` | 标准发现或手工配置 |
| Azure OpenAI | 必填 | 资源/Deployment 专用，必填 | `api-key`，无 Scheme | 手工配置 Deployment 名称 |
| Custom | 由用户配置 | 必填 | 用户配置 Header/Scheme | 用户配置或 ProbePath |

为支持本地 Ollama，OpenAI Compatible 传输必须增加“显式无认证”模式：

- 配置和构造函数允许该模式不提供 API Key。
- SDK 构造所需的内部占位凭据不得写回配置、日志或诊断输出。
- 最终 HTTP Handler 必须移除 SDK 自动生成的 Authorization，不向 Ollama 发送伪凭据。
- 其他协议仍保持当前 API Key 必填行为，不能因 Ollama 放宽全局校验。
- `StandaloneConfigValidator` 根据协议目录执行条件校验，删除当前对所有 Provider 一刀切的 `apiKey` 必填判断。

Azure OpenAI V1 不为独立 CLI 增加通用 Provider Profile 或任意设置字典。优先将 `BaseUrl` 定义为完整、可验证的 Azure 请求基址，将配置中的模型 ID 解释为 Deployment 名称；若当前 OpenAI SDK 无法稳定保留 Azure 路径或 `api-version`，则在 Azure 项目内部使用强类型原始 HTTP 映射，并在专项设计中增加最小的明确字段，不能把查询参数拼接规则散落到 CLI。

### 4.7 HTTP 和对象生命周期

- Provider 项目提供接收 `IHttpClientFactory`/`HttpClient` 的构造函数，便于宿主复用连接池和测试注入。
- Standalone 构造路径可以使用 `ProviderHttpClientFactory.Shared`，但 Adapter 必须明确自身是否拥有并释放创建的 Client/Handler。
- 同一 Provider 配置实例按 Provider ID 缓存 Adapter；同一模型的 SDK Chat Client 可以在 Adapter 内复用。
- 不按 Invocation 创建新的连接池，不在流式枚举中创建未受控 `HttpClientHandler`。
- HttpClient 超时保持与当前 Runtime 策略一致，由上层总超时和 CancellationToken 控制。
- 厂商 SDK 自动重试关闭或限制为 0；重试决策继续由 `ProviderRetryPolicy`、幂等性和不可逆工具副作用共同决定。
- 实现 `IDisposable` 的 Adapter 必须级联释放自己拥有的内部 Adapter/Client，不释放注入且不归自己所有的对象。

### 4.8 统一错误、取消和脱敏

所有 Provider 统一输出 `RuntimeProviderEvent`：

- 401/403 -> `AuthenticationFailed`。
- 429 -> `ProviderRateLimited`，保留脱敏的 `retry-after`。
- 超时 -> `ProviderTimeout`。
- JSON/流协议错误 -> `ProviderProtocolError`。
- 其他网络/5xx -> `ProviderRequestFailed`。

实现要求：

- 优先复用 `ProviderHttpResponseHandler` 和 `ProviderErrorFactory`。
- 厂商请求 ID 可以进入受控 Error Details；API Key、完整请求/响应正文不得进入。
- 外部 CancellationToken 与 `RuntimeProviderRequest.CancellationToken` 必须链接并传到 HTTP 发送、流读取、JSON 解析和模型发现。
- 本地取消后停止继续产出 Delta，不把正常取消包装成 Provider 失败。
- 流中途失败时保留已经提交的 Delta，最终只产生一个失败终态。
- 能力不足、配置缺失和不支持的内容块在发请求前失败。

### 4.9 AOT 和序列化

- 新 Provider DTO 使用 `System.Text.Json` 源生成上下文，按项目隔离。
- Ollama `/api/tags`、Aliyun/Gemini 模型列表和 Azure 原始协议 DTO 全部登记到对应 JsonContext。
- 禁止 `JsonSerializer.Serialize(object)`、动态反射 DTO、`JObject/JToken` 和 Newtonsoft.Json。
- 不通过全局 `NoWarn` 或整程序集 Root 掩盖 trimming/AOT 问题。
- 新增实验 API 抑制只能放在确实引用该 API 的 Provider 项目，并注明移除条件。
- Release Native AOT 出现任意新增 trimming/AOT 警告时，本迁移不得验收。

## 5. Provider 详细设计

### 5.1 Aliyun / DashScope

建议目录：

```text
Madorin.AI.Runtime.Providers.Aliyun/
├── Madorin.AI.Runtime.Providers.Aliyun.csproj
├── AliyunProviderAdapter.cs
├── AliyunProviderOptions.cs
├── Models/
│   └── AliyunModelCatalog.cs
└── Protocol/
    ├── AliyunModelCatalogClient.cs
    └── AliyunJsonContext.cs
```

聊天实现：

- 组合 `OpenAICompatibleProviderAdapter`，默认中国区兼容端点为 `https://dashscope.aliyuncs.com/compatible-mode/v1`，允许用户覆盖国际区或代理端点。
- Provider ID 使用配置实例名，不使用固定 `aliyun` 覆盖用户命名。
- 认证使用 Bearer API Key。
- 模型目录通过兼容 `models` 端点发现；远程失败时不覆盖原配置。
- Capability 以测试结果为准，聊天、工具、推理、视觉等逐项冻结。
- DashScope 特有错误码只转换为 Runtime 稳定错误，受控原始码放入脱敏 Details。

媒体边界：

- 保留旧 Driver 的图片生成、异步视频创建、任务轮询、下载和状态映射作为后续协议扩展输入材料。
- 本批不得在 Adapter 上增加 Runtime 无法调用的公开 `GenerateImagesAsync`/`GenerateVideosAsync`。
- 若媒体扩展获批，轮询必须使用 `TimeProvider`、可取消延迟、最大时长和持久化检查点，不能照搬固定循环。

### 5.2 Gemini

建议目录：

```text
Madorin.AI.Runtime.Providers.Gemini/
├── Madorin.AI.Runtime.Providers.Gemini.csproj
├── GeminiProviderAdapter.cs
├── GeminiProviderOptions.cs
├── Models/
│   └── GeminiModelCatalog.cs
└── Protocol/
    ├── GeminiModelCatalogClient.cs
    └── GeminiJsonContext.cs
```

实现策略：

- 继续采用旧 Driver 已验证的 Google OpenAI Compatible 入口，不额外引入 Google SDK。
- 默认 BaseURL 为 `https://generativelanguage.googleapis.com/v1beta/openai`。
- 组合 `OpenAICompatibleProviderAdapter`，保留 Bearer 认证和 Runtime 的统一流事件。
- 模型发现兼容 `data[]`、`id`、`display_name` 和 `owned_by`，无效结构返回明确协议错误。
- 工具调用、结构化输出、视觉和 Usage 分别通过录制响应验证；未验证能力保持关闭。
- 不把 Google 原生内容格式混入 Canonical Message；未来切换原生 Gemini API 时只修改本项目内部。

### 5.3 Ollama

建议目录：

```text
Madorin.AI.Runtime.Providers.Ollama/
├── Madorin.AI.Runtime.Providers.Ollama.csproj
├── OllamaProviderAdapter.cs
├── OllamaProviderOptions.cs
├── Models/
│   └── OllamaModelCatalogSource.cs
└── Protocol/
    ├── OllamaTagsResponse.cs
    └── OllamaJsonContext.cs
```

实现策略：

- Chat 使用 Ollama 的 OpenAI Compatible `/v1` 接口，默认 BaseURL 为 `http://localhost:11434/v1`。
- 默认采用无认证模式；如果用户把 Ollama 暴露在带认证的代理后，可显式配置认证头和 Scheme。
- 模型发现严格复刻旧 Driver 的 URL 规则：去除末尾 `/v1` 后请求 `/api/tags`。
- 将 `name` 和 `details.family` 映射为 `ProviderModel`，按模型 ID 去重并使用 Ordinal 稳定排序。
- 连接失败要区分本地服务未启动与协议错误，但对 Runtime 仍使用稳定错误码。
- 不在启动时强制访问 Ollama；只有向导发现、Doctor 或真实调用才访问网络。
- `config validate` 只做结构校验，不因本地 Ollama 暂时未启动判定配置文件非法。

### 5.4 GLM / 智谱

建议目录：

```text
Madorin.AI.Runtime.Providers.GLM/
├── Madorin.AI.Runtime.Providers.GLM.csproj
├── GlmProviderAdapter.cs
├── GlmProviderOptions.cs
└── Models/
    └── GlmModelCatalog.cs
```

实现策略：

- 旧 Driver 没有专用协议代码，仅提供 GLM 身份；新 Adapter 使用组合而非复制基类。
- Chat 委托给 `OpenAICompatibleProviderAdapter`。
- 默认 BaseURL 和模型只在实现时经过真实文档/冒烟验证后写入；未验证前要求用户显式输入。
- 如果 GLM 推理、工具或结构化输出存在非标准字段，先以专项录制测试证明，再增加本项目内 Handler，不修改通用 Adapter 去迎合单一厂商。
- Provider ID 使用配置实例名；协议显示名固定为 `GLM`。

### 5.5 Azure OpenAI

建议目录：

```text
Madorin.AI.Runtime.Providers.AzureOpenAI/
├── Madorin.AI.Runtime.Providers.AzureOpenAI.csproj
├── AzureOpenAIProviderAdapter.cs
├── AzureOpenAIProviderOptions.cs
└── Protocol/
    ├── AzureOpenAIRequestMapper.cs
    └── AzureOpenAIJsonContext.cs
```

实施分为两个门禁：

1. 协议验证：确认当前 OpenAI SDK 对 Azure Endpoint、Deployment 和 API Version 的实际 URL 生成结果。
2. 产品启用：认证、流式、工具、Usage、错误和 AOT 测试全部通过后，才把 Azure OpenAI 加入默认向导。

约束：

- 使用 `api-key` Header，不使用 Bearer Scheme。
- 模型 ID 在 V1 解释为 Deployment 名称，不假设可通过标准 `/models` 枚举。
- 资源端点没有通用默认值，BaseURL 必填。
- 若 SDK URL 语义不满足要求，使用项目内强类型 HTTP Mapper，不修改 OpenAI Adapter 的稳定行为。
- 旧 Driver 的 `Enabled=false` 和空模型列表只能作为风险信号，不能作为新实现完成证据。
- 在默认向导启用前，必须至少完成一个真实 Azure 资源的可选冒烟验证。

### 5.6 Custom

Custom 不新增项目，直接复用：

```text
Madorin.AI.Runtime.Providers.OpenAICompatible
```

实施内容：

- 协议目录增加 `Custom`，工厂调用 `BuildCompatible`。
- BaseURL 必填，认证头、Scheme、能力、模型、配置版本和 ProbePath 沿用现有配置字段。
- `ProviderId` 仍为用户的 `ProviderEntry.Name`。
- `Custom` 与 `OpenAI Compatible` 行为保持一致，只提供更明确的迁移入口和旧配置语义。
- 测试确认两种协议构建结果一致，避免后续分叉。

## 6. 跨项目集成设计

### 6.1 解决方案和项目引用

需要更新：

- `Src/Madorin.Ai.Runtime/Madorin.AI.Runtime.slnx`：加入五个新 Provider 项目。
- `Madorin.AI.Runtime.Server.csproj`：引用五个 Provider 项目，使 CLI/Server 组合根可以创建实现。
- `Madorin.AI.Runtime.Provider.Tests.csproj`：引用五个 Provider 项目。
- `Directory.Packages.props`：只有确认引入新的厂商 SDK 时才增加中央版本；首选现有 OpenAI Compatible 和原始 HTTP，不预设新增包。

依赖方向保持：

```text
Contracts / Tools.Abstractions
              ↑
Providers.Abstractions
              ↑
OpenAICompatible
              ↑
Aliyun / Gemini / Ollama / GLM / AzureOpenAI
              ↑
Server / Cli composition root
```

禁止 Provider 实现项目互相引用；共享逻辑只在已有 OpenAI Compatible 行为真实通用时复用，不能把 Aliyun/Gemini/Ollama 的私有规则下沉到 Abstractions。

### 6.2 Standalone 配置和工厂

新增 `StandaloneProviderProtocolCatalog.cs` 后，职责调整如下：

| 文件 | 调整 |
| --- | --- |
| `StandaloneProviderProtocolCatalog.cs` | 唯一协议列表、默认值、必填规则、是否展示和 Adapter Builder |
| `StandaloneProviderFactory.cs` | 按 Catalog 构建和缓存 Adapter，不再维护完整协议 switch |
| `StandaloneConfigValidator.cs` | 按 Catalog 和 Adapter Schema 做条件校验 |
| `ConfigWizard.cs` | 从 Catalog 生成选项、默认 BaseURL、默认模型和提示 |
| `StandaloneConfig.cs` | 尽量保持现有 JSON 结构；只在专项协议无法表达时增加明确可选字段 |
| `ConfigJsonContext.cs` | 为实际新增的配置类型补齐源生成元数据 |

兼容规则：

- 已有协议字符串和 `config.json` 字段不变。
- 已有配置不需要迁移或重写。
- 新协议是加法变更。
- 协议匹配继续使用 Ordinal 稳定字符串；向导可以接受大小写不敏感输入，但写盘使用规范名称。
- Provider Name 和 Model ID 继续保持区分大小写，不执行 Trim 之外的改写、别名猜测或 Unicode 归一化。
- API Key 只从终端无回显输入或现有配置读取，不允许通过命令行参数传入。

### 6.3 Adapter 创建和缓存

`StandaloneProviderFactory.Create` 当前按 Provider ID 缓存 Adapter。扩展后保持该行为，并补齐：

- 缓存键使用 `ProviderEntry.Name` 的 Ordinal 比较。
- 创建前确认 `NextTurnSelection.ProviderId` 存在且模型属于该 Provider。
- Adapter 创建异常必须脱敏后转换为配置错误或 Runtime 稳定错误。
- CLI/LocalRuntime 退出时释放缓存中的所有 `IDisposable` Adapter；不能只释放最后一次解析的实例。
- Meeting/Work 的子 Agent Provider Factory 必须复用同一缓存，避免同一配置被重复创建连接池。
- 宿主模式继续通过公开 Resolver 注入 Adapter，不隐式读取 `~/.madorin/config.json`。

### 6.4 Doctor 和模型发现

Doctor 对每个 Provider 按以下顺序执行：

```text
配置结构检查
-> Adapter 构建
-> 配置 Schema 检查
-> 可选模型发现
-> 能力探测
-> 结果脱敏输出
```

要求：

- `config validate` 是离线结构校验，不要求网络在线。
- `doctor` 才执行远程探测，并受单 Provider 超时和总超时限制。
- Ollama 未启动、DNS 失败、401、429、5xx 和协议错误分别输出稳定诊断类别。
- Doctor 不展示完整 BaseURL 查询敏感值、Authorization、API Key 或完整响应体。
- 模型列表过大时只输出计数和受限样本，禁止无上限打印。
- Azure OpenAI 在隐藏/实验状态下明确标记，不显示为已正式支持。

### 6.5 三种运行模式

Provider 扩展不修改 Expert、Meeting 和 Work 的编排规则。兼容性通过 Resolver 验证：

- Expert：主 Agent 使用新 Provider 完成一次文本流和一次工具循环。
- Meeting：不同参与者可以使用同协议的不同 Provider 实例；Provider ID 不串用。
- Work：不同步骤可以覆盖 Provider/模型；并发、限流、取消和失败策略保持现有语义。
- Session 恢复：Invocation Snapshot 保存实际 Provider ID、模型和能力快照，不依赖 Adapter 私有 Session。
- Provider 切换：继续从 CanonicalHistory 重建，不转发旧厂商私有会话 ID。

不为任何新 Provider 增加 Mode 专用旁路。

### 6.6 Provider 级并发与限流

本批不新建第二套调度器。新增 Provider 继续受 Runtime 现有全局并发和 Provider 调用策略控制：

- 同一配置实例按 Provider ID 形成限流维度。
- 同协议的两个配置实例可以拥有独立 Endpoint 和凭据，不共享错误状态或凭据更新。
- 429 的 `retry-after` 只作为上层策略输入，Adapter 不自行无限等待。
- Ollama 本地实例也受并发上限控制，避免并发请求耗尽本机内存。
- Aliyun/Azure 的区域或资源实例使用不同 Provider ID，防止配额统计混合。

## 7. 实施计划 : 100%

标记说明：`[×]` 未完成；`[√]` 已完成。完成子任务时同步更新步骤和总进度。

### Step 1 固化迁移基线与公共扩展 : 100%

- [√] 复核工作区现有未提交 Provider 改动，确认不覆盖并记录实施基线。
- [√] 为 `Providers.Abstractions` 增加 `IRuntimeProviderModelCatalogSource`。
- [√] 固化远程模型发现的取消、响应大小、排序、去重和错误契约。
- [√] 为 OpenAI Compatible 增加显式无认证模式，保持现有构造行为兼容。
- [√] 补充无认证模式不发送 Authorization 的 HTTP 测试。
- [√] 新增 `StandaloneProviderProtocolCatalog` 并迁移已有五种协议。
- [√] 证明已有 OpenAI、Anthropic、OpenAI Compatible、DeepSeek、Kimi 配置和向导行为不变。
- [√] 刷新 CodeMap overlay 并检查新增公共符号引用边界。

### Step 2 迁移 Gemini、GLM 和 Custom : 100%

- [√] 创建 Gemini Provider 项目、Options、Adapter、模型发现和 JsonContext。
- [√] 以组合方式复用 OpenAI Compatible Chat，不复制消息映射。
- [√] 创建 GLM Provider 项目、Options、Adapter 和保守模型目录。
- [√] 验证 GLM 是否需要专项 Handler；没有证据时保持纯组合实现。
- [√] 增加 Custom 协议别名并证明与 OpenAI Compatible 行为一致。
- [√] 将 Gemini、GLM、Custom 加入协议目录、配置向导、校验和工厂。
- [√] 加入解决方案、Server 和 Provider Tests 项目引用。
- [√] 完成三者的离线流式、工具、错误、取消和脱敏测试。

### Step 3 迁移 Ollama : 100%

- [√] 创建 Ollama Provider 项目、Options、Adapter、Tags DTO 和 JsonContext。
- [√] 实现默认无认证，并支持显式代理认证。
- [√] 实现 `/v1` 到 `/api/tags` 的模型发现 URL 规则。
- [√] 覆盖空模型列表、重复模型、缺失字段、超大响应和无效 JSON。
- [√] 配置校验允许 Ollama 空 API Key，但其他协议不受影响。
- [√] Doctor 区分未启动、超时、认证代理失败和协议错误。
- [√] 使用本地 Fake HTTP Server 完成 Chat/Streaming/Tool/Usage 测试。
- [√] 增加可选本机 Ollama 冒烟测试，不纳入默认测试稳定性门禁。

### Step 4 迁移 Aliyun : 100%

- [√] 创建 Aliyun Provider 项目、Options、Adapter、模型目录客户端和 JsonContext。
- [√] 实现 DashScope Compatible Chat 和可覆盖的区域 Endpoint。
- [√] 实现远程模型发现、认证、请求 ID 和错误映射。
- [√] 逐项验证 Streaming、ToolCalling、Reasoning、Vision、Usage 能力。
- [√] 对未支持能力在请求前失败，禁止根据 Qwen/Wan 模型名猜测。
- [√] 将旧图片/视频实现记录为媒体协议扩展输入，不创建不可达公开方法。
- [√] 完成 Fake HTTP、取消、429、5xx、断流和响应脱敏测试。
- [√] 增加可选真实 DashScope 冒烟测试。

### Step 5 迁移 Azure OpenAI : 100%

- [√] 使用录制请求先验证当前 OpenAI SDK 的 Azure URL、Deployment 和 API Version 行为。
- [√] 确定复用 SDK 或项目内原始 HTTP Mapper，记录决策和理由。
- [√] 创建 Azure OpenAI Provider 项目、Options、Adapter 和 JsonContext。
- [√] 实现 `api-key` 认证且不发送 Bearer Authorization。
- [√] 固定 BaseURL、Deployment/Model 和 API Version 的配置语义。
- [√] 覆盖 Streaming、ToolCalling、Usage、401、429、5xx、取消和协议错误。
- [√] 完成真实 Azure 资源的可选冒烟测试入口；缺少凭据时默认跳过。
- [√] 全部门禁通过后才把 Azure OpenAI 加入默认向导；当前无真实资源成功证据，继续保持实验隐藏。

### Step 6 接通配置、Doctor 和所有运行模式 : 100%

- [√] `config init/edit/show/validate` 支持所有新增协议。
- [√] Key 输入、配置展示、异常和 Doctor 输出保持脱敏。
- [√] 模型发现失败时允许手工输入且不写入半有效配置。
- [√] Provider Factory 对多个同协议实例保持 Provider ID 隔离。
- [√] 独立 CLI Expert 模式使用每个新增 Provider 完成确定性执行。
- [√] Meeting 模式验证参与者使用不同 Provider 实例时不串流。
- [√] Work 模式验证步骤级 Provider 覆盖、并发、取消和失败策略。
- [√] 宿主 Resolver 路径不依赖个人 CLI 配置。
- [√] Session 恢复和 Provider 切换继续使用 CanonicalHistory。

### Step 7 扩展测试矩阵 : 100%

- [√] 扩展 `ProviderAdapterTestFactory` 或建立 Compatible 专用数据驱动工厂。
- [√] 将正式启用的新增 Adapter 纳入 Provider Conformance Suite。
- [√] 增加模型发现、认证头、配置 Schema 和协议目录专项测试。
- [√] 增加慢流、半包、断流、无效 JSON、超大响应和 CancellationToken 测试。
- [√] 增加 API Key、Authorization、Prompt、响应体不泄露测试。
- [√] 增加多个 Provider 实例并发与生命周期释放测试。
- [√] 增加新增协议配置 JSON 的源生成序列化往返测试。
- [√] 增加 Expert/Meeting/Work 端到端矩阵。
- [√] 将真实 Provider 冒烟测试保持为 `External`、凭据驱动和默认跳过。

### Step 8 构建、AOT 与交付收口 : 100%

- [√] 执行所有新增 Provider 项目定向构建。
- [√] 执行 `Madorin.AI.Runtime.Provider.Tests`。
- [√] 执行相关 CLI 和 EndToEnd 测试。
- [√] 执行完整解决方案 Debug 测试。
- [√] 执行完整解决方案 Release 非增量构建。
- [√] 执行 win-x64 Self-contained Native AOT 发布。
- [√] 确认编译、分析器、trimming 和 AOT 均为 0 警告、0 错误。
- [√] 更新独立 CLI 配置/使用文档和 Provider 支持矩阵。
- [√] 记录实际测试数量、跳过的 External 用例、Git 基线和未关闭限制。
- [√] 刷新 CodeMap overlay，复核项目依赖和公开 API 边界。

## 8. 测试方案

### 8.1 Provider 覆盖矩阵

| Provider | 通用流式一致性 | 工具调用 | 模型发现 | 认证专项 | 协议专项 | 三模式 E2E | External Smoke |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Aliyun | 必须 | 验证后开启 | `/models` | Bearer | DashScope 错误/区域端点 | 必须 | 可选 |
| Gemini | 必须 | 验证后开启 | `/models` | Bearer | Google 兼容响应 | 必须 | 可选 |
| Ollama | 必须 | 按本地模型能力 | `/api/tags` | 无认证/代理认证 | 本地连接与 Tags | 必须 | 可选本机 |
| GLM | 必须 | 验证后开启 | 可选 `/models` | Bearer | 非标准字段按证据增加 | 必须 | 可选 |
| Azure OpenAI | 门禁通过后必须 | 门禁通过后必须 | 手工 Deployment | `api-key` | URL/API Version | 门禁通过后必须 | 启用前必须 |
| Custom | 与 Compatible 共用 | 配置声明 | 配置 ProbePath | 用户配置 | 通用兼容行为 | 必须 | 复用 Compatible |

### 8.2 共用一致性测试

对正式启用的每个 Adapter 使用相同测试向量：

- System/User/Assistant/Tool 四种角色映射。
- 中文、换行、首尾空白、引号、路径、URL 和 JSON 参数逐字符保持。
- 文本流增量顺序稳定，结束时产生唯一终态。
- Reasoning 与 Text 分离，不把思考内容混入普通文本。
- Tool Call 参数增量和完整 JSON，工具名称策略与 Tool ID 可逆解析。
- Tool Result 回传到同一 Provider 的下一轮调用。
- Usage 精确度、输入/输出 Token 和迟到 Usage 事件。
- FinishReason 映射。
- Capability 不足在发出请求前失败。
- 外部取消、Request 内取消和慢流取消。
- Provider 切换使用 CanonicalHistory，不依赖厂商 Session。

### 8.3 HTTP 故障注入

使用本地 Fake HTTP Server 覆盖：

| 场景 | 期望 |
| --- | --- |
| 401/403 | `AuthenticationFailed`，无 Key 泄露 |
| 429 + retry-after | `ProviderRateLimited`，保留受控重试信息 |
| 408/超时 | `ProviderTimeout` |
| 500/502/503 | `ProviderRequestFailed`，按策略标记可重试 |
| 分片/半包 | 正确累计并按顺序输出 |
| 流中断 | 保留已有 Delta，以唯一失败终态结束 |
| 无效 JSON | `ProviderProtocolError` |
| 超大探测响应 | 在配置上限处失败，不继续分配内存 |
| 空响应/缺失字段 | 明确协议错误或空模型目录，不抛 NullReferenceException |
| CancellationToken | HTTP、Stream、JSON 和轮询及时停止 |

### 8.4 配置和向导测试

- 旧五协议配置文件不变即可加载、验证和创建 Adapter。
- 新六协议可以通过 `config init/edit` 生成规范协议名。
- Ollama 空 Key 合法；Aliyun/Gemini/GLM/Azure 缺 Key 非法；Custom 按认证配置校验。
- BaseURL 的 HTTP/HTTPS、绝对 URI、尾斜杠、路径和查询参数处理稳定。
- 同一协议允许多个不同 Provider Name，默认 Provider/Model 引用校验正确。
- 模型发现失败后手工输入仍能完成配置；失败结果不自动写入。
- `config show` 对新增字段继续脱敏。
- 配置 JSON 使用 `ConfigJsonContext` 完成序列化往返，Native AOT 下不回退反射。
- 向导选项编号由 Catalog 生成，新增/隐藏协议不会造成提示和实际映射错位。

### 8.5 生命周期和并发测试

- 同一 Provider ID 多次解析返回同一 Adapter 实例。
- 不同 Provider ID 即使协议相同也使用独立配置和凭据。
- 并发首次解析只创建一个 Adapter。
- CLI/LocalRuntime Dispose 后所有自有 Adapter、HttpClient 和 Handler 被释放一次。
- 更新一个 Provider 的凭据不影响同协议其他实例。
- Meeting/Work 并发时事件仍按 Invocation/Run 正确归属。
- 取消一个 Invocation 不取消同 Provider 的其他 Invocation。
- Provider 模型发现与正在执行的 Invocation 不共享可变模型集合。

### 8.6 三模式端到端矩阵

默认离线测试使用确定性 Fake HTTP，不访问真实厂商：

| 模式 | 最小场景 | 核心断言 |
| --- | --- | --- |
| Expert | 单 Provider 文本 + 工具续调 | 唯一终态、工具结果回传、Provider ID 正确 |
| Meeting | 两参与者使用不同 Provider 实例 | 事件不串流、各自模型和能力快照正确 |
| Work | 两步骤使用 Provider 覆盖 | 依赖顺序、并发、取消和失败策略不变 |
| Session Resume | 恢复后继续调用同 Provider | CanonicalHistory、选择快照和游标正确 |
| Provider Switch | 下一轮切换到另一 Provider | 不携带厂商私有 Session，历史语义不丢失 |

### 8.7 真实冒烟测试

统一由 `MADORIN_PROVIDER_SMOKE=1` 显式开启。建议环境变量：

```text
MADORIN_ALIYUN_API_KEY / BASE_URL / MODEL_ID
MADORIN_GEMINI_API_KEY / BASE_URL / MODEL_ID
MADORIN_OLLAMA_BASE_URL / MODEL_ID
MADORIN_GLM_API_KEY / BASE_URL / MODEL_ID
MADORIN_AZURE_OPENAI_API_KEY / BASE_URL / MODEL_ID
```

规则：

- 缺少凭据或未显式开启时使用 `Assert.Inconclusive`，不算失败。
- 默认 CI 不依赖公网或用户账户。
- 冒烟 Prompt 使用无敏感、低 Token 的固定文本。
- 不在测试输出中打印 Key、完整响应或资源 URL 的敏感查询参数。
- Azure OpenAI 在默认启用前必须至少有一次真实资源成功证据；其他 Provider 的真实冒烟不替代离线协议测试。

## 9. 验收标准

### 9.1 功能验收

- [√] Aliyun、Gemini、Ollama、GLM 可从独立配置选择并创建目标 Adapter。
- [√] Azure OpenAI 达到离线专项门禁；因没有真实资源成功证据，保持实验隐藏且不伪报正式支持。
- [√] Custom 协议别名复用 OpenAI Compatible，行为无分叉。
- [√] 每个 Provider 支持至少一个配置模型完成流式文本调用。
- [√] 已声明 ToolCalling 的 Provider 完成一次真实工具循环。
- [√] 模型发现支持的 Provider 能返回稳定、去重的模型目录。
- [√] Ollama 无 API Key 可以合法配置和调用。
- [√] Doctor 可以识别并脱敏报告每个新 Provider 的配置/连接/协议状态。
- [√] Expert、Meeting、Work 和 Session Resume 使用相同 Resolver 正常运行。
- [√] 外部取消、超时、限流、认证失败和协议错误全部产生稳定 Runtime 语义。

### 9.2 架构验收

- [√] 新 Provider 不引用任何 `Netor.Cortana.*` 项目或类型。
- [√] `Providers.Abstractions`、Contracts、Core、Client 不引用 Provider SDK。
- [√] 没有新增 Provider 私有执行状态机或持久化旁路。
- [√] `IRuntimeProviderAdapter` 主契约保持兼容；模型发现通过可选窄接口扩展。
- [√] Provider ID 等于配置实例 ID，同协议多实例可隔离运行。
- [√] HTTP 连接池、Adapter 和 SDK Client 生命周期可证明无泄漏。
- [√] Capability 声明有测试证据，未实现能力不静默降级。
- [√] 配置、向导和工厂只维护一个协议目录。
- [√] 现有五种协议和配置文件保持向后兼容。
- [√] Aliyun 媒体能力未通过私有旁路侵入当前聊天契约。

### 9.3 质量验收

- [√] 新增 Provider 定向测试全部通过。
- [√] Provider Conformance Suite 全部通过。
- [√] CLI/EndToEnd 相关测试全部通过。
- [√] 完整解决方案测试无失败；External 仅按凭据条件跳过。
- [√] Debug 和 Release 构建 0 警告、0 错误。
- [√] win-x64 Native AOT 发布 0 trimming/AOT 警告。
- [√] 日志、异常、Doctor 和测试输出未发现凭据或 Prompt 泄露。
- [√] CodeMap 依赖检查确认公开边界和项目方向没有倒置。

## 10. 风险与应对

| 风险 | 影响 | 应对 |
| --- | --- | --- |
| “OpenAI Compatible” 只兼容基础字段 | 工具、推理、Usage 或流协议在厂商端失败 | 能力保守声明；逐 Provider 录制响应测试；差异留在专用项目 |
| Azure 旧实现本身不完整 | 复制后看似存在、实际不可用 | 双门禁；验证 URL/认证/API Version 后再启用 |
| Aliyun 旧媒体能力超出当前契约 | 直接复制形成不可达 API 或旁路状态机 | 本批只迁移当前可消费能力；媒体另立协议策划 |
| Ollama 默认无认证 | 通用校验要求假 Key，或误发 Authorization | 增加显式无认证模式和 HTTP 断言，不用假配置值 |
| 模型目录变化快 | 内置目录过时、能力误判 | 配置模型为权威快照；可选远程发现；能力不按名称猜测 |
| Provider 项目数量增加 | 构建和维护成本上升 | 每项目只承载真实厂商边界；Custom 不建空壳；共享通用协议实现 |
| Provider SDK/HTTP 对 AOT 不友好 | Release 发布失败 | 优先现有包/原始强类型 HTTP；源生成 JSON；AOT 作为每批门禁 |
| 多实例缓存和 Dispose 不完整 | 连接泄漏、凭据串用 | 生命周期与并发专项测试；按 Provider ID 隔离缓存 |
| 外部网络测试不稳定 | 普通 CI 偶发失败 | 离线 Fake HTTP 为主，真实冒烟显式开启和默认跳过 |
| 工作区已有未提交 Provider 改动 | 实施时覆盖用户工作 | 每批先审阅 git diff，只做局部补丁，不还原无关改动 |

## 11. 回退与兼容策略

本迁移原则上不修改 IPC Protocol Version、SQLite Schema 或 Session 持久化结构，因此不需要数据库迁移和协议升级。

分批回退方式：

1. Provider 项目批次可独立移除对应 Solution/Server/Test 引用和 Catalog 条目。
2. 公共模型发现接口为加法接口；没有消费者时可保留，不影响现有 Adapter。
3. OpenAI Compatible 无认证扩展必须保持默认认证行为不变；若回退 Ollama，可撤销无认证分支而不改变已有配置。
4. 现有 OpenAI、Anthropic、OpenAI Compatible、DeepSeek、Kimi 协议字符串和 JSON 字段不变。
5. 使用新协议的配置在旧版本回退后会明确报告 Unsupported Protocol，不得静默改成 OpenAI Compatible。
6. Azure 未通过启用门禁时只回退实验入口，不影响其他 Provider。
7. 每个实施批次记录 Git 基线、实际修改文件、测试结果和未关闭限制，禁止依赖未记录的会话上下文回退。

## 12. 预计文件变更

### 12.1 新增文件/项目

```text
Src/Madorin.Ai.Runtime/src/Providers/
  Madorin.AI.Runtime.Providers.Aliyun/**
  Madorin.AI.Runtime.Providers.Gemini/**
  Madorin.AI.Runtime.Providers.Ollama/**
  Madorin.AI.Runtime.Providers.GLM/**
  Madorin.AI.Runtime.Providers.AzureOpenAI/**

Src/Madorin.Ai.Runtime/src/Providers/
  Madorin.AI.Runtime.Providers.Abstractions/IRuntimeProviderModelCatalogSource.cs

Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Config/
  StandaloneProviderProtocolCatalog.cs
```

测试文件按职责新增，不把所有 Provider 专项测试继续堆入一个大文件：

```text
ProviderCompatiblePresetTests.cs
ProviderModelCatalogTests.cs
ProviderAuthenticationTests.cs
ProviderModeCompatibilityTests.cs
```

实际命名以现有测试目录职责为准，避免与已有 Conformance/FaultInjection 测试重复。

### 12.2 预计修改文件

```text
Src/Madorin.Ai.Runtime/Madorin.AI.Runtime.slnx
Src/Madorin.Ai.Runtime/Directory.Packages.props                  # 仅新增 SDK 时
Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/*.csproj
Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Config/StandaloneProviderFactory.cs
Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Config/StandaloneConfigValidator.cs
Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Config/ConfigWizard.cs
Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Config/ConfigJsonContext.cs
Src/Madorin.Ai.Runtime/src/Providers/Madorin.AI.Runtime.Providers.OpenAICompatible/*
Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Provider.Tests/*.csproj
Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Provider.Tests/*.cs
Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/*.cs
Docs/未来策划/独立CLI/使用指南/02-安装与初始化.md
```

实施前必须再次检查每个受影响目录的 `README.md`，并以实际依赖和测试结果收缩修改范围。

## 13. 评审决策点

实施前需确认以下三项方案结论：

1. **媒体范围**：本批不迁移旧 Aliyun/OpenAI 图片和视频公开能力，另立 Runtime 媒体协议扩展。
2. **Custom 形态**：Custom 作为 OpenAI Compatible 协议别名，不创建重复 Provider 项目。
3. **Azure 启用方式**：先实现和测试，真实 Azure 冒烟通过后再进入默认向导；此前保持实验隐藏。

上述三项确认后，按 Step 1 至 Step 8 顺序实施。任何一项改变都会影响项目数量、公共契约或验收矩阵，应先更新本文再编码。

## 14. 实施验收记录

| 项目 | 最终结果 |
| --- | --- |
| 实施基线 | Git `b4a1378c36eed14f837aa82a9c9f5f0d14f9724c`；保留并兼容工作区原有未提交改动 |
| Provider 测试 | `220` 通过，`6` External 跳过，`0` 失败 |
| 完整 Debug 测试 | `948` 通过，`6` External 跳过，`0` 失败 |
| Release 构建 | 全解决方案非增量构建 `0` 警告，`0` 错误 |
| Native AOT | win-x64 Self-contained 发布成功，`0` trimming/AOT 警告；发布制品版本 `0.1.1`、协议 `1.1` |
| CodeMap | `session` overlay revision `26`；新增公共接口、Provider 实现、CLI 消费者和项目引用边界已复核 |
| 协议 / 数据库 | 未修改 IPC Protocol Version 与 SQLite Schema |
| 未关闭限制 | 6 项 External 需要显式开关和真实外部资源；Azure OpenAI 无真实资源成功证据，继续保持实验隐藏；Aliyun/OpenAI 图片与视频能力不在本批范围 |

详细的分步骤测试数量、诊断语义和回退依据见[执行计划](./执行步骤/09-Provider扩展迁移.md)。
