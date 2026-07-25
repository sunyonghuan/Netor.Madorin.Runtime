# Provider 扩展迁移执行计划 : 100%

> 专项状态：已完成
>
> 编制日期：2026-07-26
>
> 设计依据：[Provider 扩展迁移详细方案](../策划/02-Provider扩展迁移详细方案.md)
>
> Git 基线：`b4a1378c36eed14f837aa82a9c9f5f0d14f9724c`

## 0. 执行口径

本计划是阶段 4/4A 完成后的 Provider 加法扩展，不改写既有 V1 十三个阶段的完成数。原 V1 总览中“Gemini 正式 Adapter 不进入 V1”的范围结论，由本次 2026-07-26 专项策划显式扩展；其余 Runtime 不变量继续有效。

用户要求按详细方案推进，视为接受方案末尾三项决策：

1. 本批不迁移 Aliyun/OpenAI 图片和视频公开能力。
2. `Custom` 只作为 `OpenAI Compatible` 协议别名，不建立 Provider 空壳项目。
3. Azure OpenAI 先实现协议和离线门禁，真实 Azure 冒烟通过前保持默认向导隐藏。

当前工作区包含未提交的 CLI 欢迎界面、工具名策略、Provider 消息映射及一致性测试改动。本专项只做局部补丁，不回退、不覆盖这些改动。

## 1. 进度跟踪

| 批次 | 状态 | 已完成 | 总数 | 进度 |
| --- | --- | ---: | ---: | ---: |
| Step 1 公共扩展与协议目录 | 已完成 | 8 | 8 | 100% |
| Step 2 Gemini、GLM、Custom | 已完成 | 8 | 8 | 100% |
| Step 3 Ollama | 已完成 | 8 | 8 | 100% |
| Step 4 Aliyun | 已完成 | 8 | 8 | 100% |
| Step 5 Azure OpenAI | 已完成 | 8 | 8 | 100% |
| Step 6 配置、Doctor 与运行模式 | 已完成 | 9 | 9 | 100% |
| Step 7 测试矩阵 | 已完成 | 9 | 9 | 100% |
| Step 8 构建、AOT 与交付 | 已完成 | 10 | 10 | 100% |
| **合计** | **已完成** | **68** | **68** | **100%** |

进度只按已经实现并通过对应门禁的检查项计算。External 冒烟因缺少凭据而跳过时，不得把依赖真实资源的产品启用项标记为完成。

## 2. 基线与门禁

### 2.1 已确认基线

- [√] Git HEAD 为 `b4a1378c36eed14f837aa82a9c9f5f0d14f9724c`。
- [√] .NET SDK 为 `10.0.301`，目标框架为 `net10.0`，语言版本为 C# 14。
- [√] CodeMap baseline 已存在并创建 `session` workspace；当前缓存索引覆盖仓库主解决方案，Runtime 子解决方案语义缺口以精确源码读取交叉验证。
- [√] Provider 定向基线为 `147` 通过、`3` External 跳过、`0` 失败。
- [√] 已记录相关未提交文件，后续仅使用局部补丁。

### 2.2 每批共同入口

1. 检查受影响目录 `README.md` 和当前 `git diff`。
2. 先补协议/故障测试，再实现生产代码。
3. 构建本批新增项目并运行 Provider 定向测试。
4. 修改 C# 后刷新 CodeMap overlay，检查公共符号和引用边界。
5. 更新本文件的检查项、测试数量、限制与回退点。

### 2.3 每批共同出口

- Debug 编译 `0` 警告、`0` 错误。
- 默认离线测试不访问公网，不依赖真实凭据。
- API Key、Authorization、完整 Prompt 和响应体不进入输出。
- `ProviderId` 始终等于配置实例 `ProviderEntry.Name`。
- 不新增 `Netor.Cortana.*` 项目引用，不修改 IPC 与 SQLite Schema。
- 取消令牌传递到 HTTP、流读取和 JSON 解析。

## 3. 分批执行

### Step 1 公共扩展与协议目录 : 100%

- [√] 复核并记录工作区 Provider 相关未提交改动。
- [√] 增加 `IRuntimeProviderModelCatalogSource`，冻结返回类型和取消契约。
- [√] 提供有界模型目录响应读取、Ordinal 去重排序和协议错误基础设施。
- [√] OpenAI Compatible 增加显式无认证模式，默认认证行为不变。
- [√] 证明无认证模式不发送 `Authorization`，凭据更新在该模式下被拒绝。
- [√] 新增 `StandaloneProviderProtocolCatalog`，收口已有五种协议。
- [√] 工厂、校验器和向导改为使用统一目录。
- [√] 旧五协议配置、构建和向导行为回归通过并刷新 CodeMap overlay。

### Step 2 Gemini、GLM 和 Custom : 100%

- [√] 创建 Gemini Provider 项目、Options、Adapter、模型发现和 JsonContext。
- [√] Gemini Chat 组合 OpenAI Compatible，不复制消息映射。
- [√] 创建 GLM Provider 项目、Options、Adapter 和保守模型目录。
- [√] 在无协议证据时不为 GLM 增加专项 Handler。
- [√] `Custom` 复用 `BuildCompatible` 并保持行为一致。
- [√] 将三种协议接入目录、配置向导、校验和工厂。
- [√] 更新解决方案、Server 与 Provider Tests 项目引用。
- [√] 完成离线流式、工具、错误、取消、模型发现与脱敏测试。

### Step 3 Ollama : 100%

- [√] 创建 Ollama Provider 项目、Options、Adapter、Tags DTO 和 JsonContext。
- [√] 默认无认证，允许显式代理认证。
- [√] 实现 BaseURL 去除末尾 `/v1` 后访问 `/api/tags`。
- [√] 模型目录覆盖空列表、重复、缺失字段、超大响应和无效 JSON。
- [√] 配置校验只对 Ollama 允许空 API Key。
- [√] Doctor 区分连接、超时、认证和协议错误。
- [√] Fake HTTP 覆盖 Chat、Streaming、Tool、Usage 和取消。
- [√] 增加默认跳过的本机 Ollama External 冒烟测试。

### Step 4 Aliyun : 100%

- [√] 创建 Aliyun Provider 项目、Options、Adapter、模型目录客户端和 JsonContext。
- [√] 组合 DashScope Compatible Chat，提供可覆盖区域端点。
- [√] 实现远程模型发现、Bearer 认证、请求 ID 和错误映射。
- [√] 逐项冻结 Streaming、ToolCalling、Reasoning、Vision、Usage 能力。
- [√] 未支持能力在发请求前失败，不按模型名称猜测。
- [√] 记录媒体扩展输入，不增加不可达公开方法。
- [√] 覆盖取消、429、5xx、断流、超大响应和脱敏。
- [√] 增加默认跳过的 DashScope External 冒烟测试。

### Step 5 Azure OpenAI : 100%

- [√] 用录制 Handler 固定当前 OpenAI SDK 的 Azure URL 行为。
- [√] 记录 SDK 复用或强类型原始 HTTP Mapper 的决策依据。
- [√] 创建 Azure OpenAI Provider 项目、Options、Adapter 和 JsonContext。
- [√] 仅发送 `api-key`，不发送 Bearer Authorization。
- [√] 固定 BaseURL、Deployment 和 API Version 配置语义。
- [√] 覆盖 Streaming、ToolCalling、Usage、401、429、5xx、取消和协议错误。
- [√] 增加默认跳过的真实 Azure External 冒烟测试。
- [√] 真实资源未验证前保持默认向导隐藏并记录未关闭门禁。

### Step 6 配置、Doctor 与运行模式 : 100%

- [√] `config init/edit/show/validate` 支持正式新增协议。
- [√] Key 输入、配置展示、异常和 Doctor 输出保持脱敏。
- [√] 模型发现失败后允许手工输入且不写入半有效配置。
- [√] 工厂按 Provider ID 隔离同协议多个实例。
- [√] Expert 使用每个正式新增 Provider 完成确定性执行。
- [√] Meeting 验证不同 Provider 实例不串流。
- [√] Work 验证步骤级 Provider 覆盖、并发、取消和失败策略。
- [√] 宿主 Resolver 不读取个人 CLI 配置。
- [√] Session Resume 与 Provider Switch 继续使用 CanonicalHistory。

### Step 7 测试矩阵 : 100%

- [√] 扩展 Compatible 专用数据驱动测试工厂。
- [√] 正式 Adapter 纳入 Provider Conformance Suite。
- [√] 增加模型发现、认证头、Schema 和协议目录测试。
- [√] 增加慢流、半包、断流、无效 JSON、超大响应和取消测试。
- [√] 增加凭据、Prompt 和响应体不泄露测试。
- [√] 增加同协议多实例并发与释放测试。
- [√] 增加配置 JSON 源生成序列化往返测试。
- [√] 增加 Expert、Meeting、Work 端到端矩阵。
- [√] External 测试保持凭据驱动和默认跳过。

### Step 8 构建、AOT 与交付 : 100%

- [√] 定向构建全部新增 Provider 项目。
- [√] 运行 `Madorin.AI.Runtime.Provider.Tests`。
- [√] 运行相关 CLI 与 EndToEnd 测试。
- [√] 运行完整解决方案 Debug 测试。
- [√] 运行完整解决方案 Release 非增量构建。
- [√] 执行 win-x64 Self-contained Native AOT 发布。
- [√] 确认编译、分析器、trimming 和 AOT 为零警告、零错误。
- [√] 更新独立 CLI 使用文档和 Provider 支持矩阵。
- [√] 记录实际测试数量、External 跳过项、Git 基线和限制。
- [√] 刷新 CodeMap overlay 并复核项目依赖和公共 API 边界。

## 4. 测试执行顺序

```powershell
dotnet test .\tests\Madorin.AI.Runtime.Provider.Tests\Madorin.AI.Runtime.Provider.Tests.csproj --nologo -v minimal
dotnet test .\tests\Madorin.AI.Runtime.EndToEnd.Tests\Madorin.AI.Runtime.EndToEnd.Tests.csproj --nologo -v minimal
dotnet test .\Madorin.AI.Runtime.slnx --nologo -v minimal
dotnet build .\Madorin.AI.Runtime.slnx -c Release --no-incremental --nologo
dotnet publish .\src\Madorin.AI.Runtime.Cli\Madorin.AI.Runtime.Cli.csproj -c Release -r win-x64 --self-contained --nologo
```

真实 Provider 冒烟统一由 `MADORIN_PROVIDER_SMOKE=1` 显式开启。普通测试不得读取真实凭据或访问公网。

## 5. 回退点

1. 公共接口与无认证模式是加法变更，可独立保留。
2. 每个新 Provider 项目可连同 Solution、Server、Tests 和协议目录引用独立移除。
3. 现有五种协议字符串及配置 JSON 字段不得修改。
4. Azure 的实验入口可独立撤回，不影响其他 Provider。
5. 本专项不修改 Runtime IPC Version、SQLite Schema 或 Session 持久化结构。

## 6. 验收记录

| 项目 | 当前记录 |
| --- | --- |
| 实施基线 | Git `b4a1378c36eed14f837aa82a9c9f5f0d14f9724c`；工作区存在用户未提交改动 |
| SDK / TFM | .NET SDK `10.0.301`；`net10.0`；C# `14.0` |
| 实施前 Provider 测试 | `147` 通过，`3` External 跳过，`0` 失败 |
| 协议 / 数据库 | 本专项不修改 IPC Protocol Version 与 SQLite Schema |
| 当前未关闭限制 | Azure 真实资源门禁；各厂商 External 冒烟需要用户凭据；Azure 在取得真实资源成功证据前继续保持实验隐藏 |
| 当前回退参考 | 使用 Git 基线与本文件逐批记录的修改文件进行差异回退；不得回退用户原有工作树改动 |

### Step 1 验收

| 验收项 | 结果 |
| --- | --- |
| Provider 定向测试 | `156` 通过，`3` External 跳过，`0` 失败 |
| 配置安全定向测试 | `13` 通过，`0` 失败 |
| Release 全解决方案构建 | `0` 警告，`0` 错误 |
| 完整 EndToEnd 探测 | `256` 通过，`12` 失败；失败均因既有进程测试缺少 `madorin.dll`/`madorin.exe` 或预构建 artifact，留作 Step 8 门禁 |
| CodeMap | overlay revision `3`，刷新 `48` 个文件；Runtime 子解决方案不在缓存 baseline 中，公共符号边界以源码和项目引用检查补证 |

### Step 2 验收

| 验收项 | 结果 |
| --- | --- |
| Provider 定向测试 | `170` 通过，`3` External 跳过，`0` 失败 |
| 配置安全定向测试 | `13` 通过，`0` 失败 |
| Release 全解决方案构建 | `0` 警告，`0` 错误 |
| 协议边界 | Gemini 使用强类型源生成模型目录；GLM 保持 Compatible 纯组合；Custom 无独立项目 |
| CodeMap | overlay revision `8`；Runtime 子解决方案仍不在缓存 baseline 中，以源码、项目引用和编译结果补证 |

### Step 3 验收

| 验收项 | 结果 |
| --- | --- |
| Provider 定向测试 | `180` 通过，`4` External 跳过，`0` 失败 |
| Doctor / 配置安全定向测试 | `21` 通过，`0` 失败 |
| Release 全解决方案构建 | `0` 警告，`0` 错误 |
| 协议边界 | Ollama Chat 组合 Compatible；Tags 使用强类型源生成 DTO、有界读取和实际连接探测；默认无认证 |
| CodeMap | overlay revision `12`；Runtime 子解决方案仍以源码、项目引用和编译结果补证 |

### Step 4 验收

| 验收项 | 结果 |
| --- | --- |
| Provider 定向测试 | `199` 通过，`5` External 跳过，`0` 失败 |
| Aliyun / 共享错误映射定向测试 | `37` 通过，`1` External 跳过，`0` 失败 |
| Doctor / 配置安全定向测试 | `21` 通过，`0` 失败 |
| Release 全解决方案构建 | `0` 警告，`0` 错误 |
| 协议边界 | Chat 组合 Compatible；模型目录使用强类型源生成 DTO 与有界读取；默认只声明 Streaming；媒体生成未进入公开契约 |
| 错误语义 | 保留 `x-request-id` / `retry-after`，响应正文不外泄；认证失败不再错误标记为可重试 |
| External 限制 | 真实 DashScope 冒烟需要 `MADORIN_PROVIDER_SMOKE=1`、API Key 和 Model ID，默认跳过 |
| CodeMap | overlay revision `17`；Runtime 子解决方案仍以源码、项目引用和编译结果补证 |

### Step 5 验收

| 验收项 | 结果 |
| --- | --- |
| Azure 定向测试 | `20` 通过，`1` External 跳过，`0` 失败 |
| Provider 定向测试 | `219` 通过，`6` External 跳过，`0` 失败 |
| Doctor / 配置安全定向测试 | `21` 通过，`0` 失败 |
| Release 全解决方案构建 | `0` 警告，`0` 错误 |
| SDK 协议决策 | 复用现有 OpenAI SDK 与 Compatible 消息映射；项目内 Handler 固定 Deployment 路径、唯一 `api-version` 和 `api-key` 认证 |
| 错误与配置语义 | 保留 `apim-request-id`、`retry-after` 和安全厂商错误码；错误正文有界且不外泄；模型 ID 解释为 Deployment；`ApiVersion` 源生成往返并按字段校验 |
| External 限制 | 真实 Azure 冒烟需要 `MADORIN_PROVIDER_SMOKE=1`、API Key、BaseURL 和 Deployment；当前无成功证据，默认向导继续隐藏 |
| CodeMap | overlay revision `21`；Azure 新项目已刷新 overlay，Runtime 子解决方案边界继续以源码、项目引用和编译结果补证 |

### Step 6 验收

| 验收项 | 结果 |
| --- | --- |
| 配置与 Doctor 定向测试 | `26` 通过，`0` 失败；覆盖发现成功、失败回退、取消不落盘、释放、脱敏、`ApiVersion` 往返、模型样本上限和 Azure 实验标记 |
| Provider Factory 隔离测试 | `4` 通过，`0` 失败；同协议双实例的 Adapter、凭据、端点和请求互不串用 |
| 三模式 Resolver 定向测试 | `7` 通过，`0` 失败；Aliyun、Gemini、GLM、Custom、Ollama 均通过确定性 Expert 路由，既有 Meeting/Work/Resume 矩阵继续通过 |
| 相关 EndToEnd 回归 | `51` 通过，`0` 失败；宿主 Resolver 保持独立配置边界，Provider Switch/Resume 继续使用 CanonicalHistory |
| 诊断语义 | 模型发现先于能力探测；单 Provider 共用 10 秒超时；失败只输出认证、超时、限流、服务端、协议、连接、配置或未知类别，不回显异常正文 |

### Step 7 验收

| 验收项 | 结果 |
| --- | --- |
| Provider 全集 | `220` 通过，`6` External 跳过，`0` 失败 |
| Conformance / 故障矩阵 | 正式 Adapter 已覆盖流式、工具、认证、模型发现、慢流、半包、断流、无效 JSON、超大响应、取消和脱敏 |
| 多实例与生命周期 | 同协议双实例的缓存键、凭据、端点、请求和释放路径均有独立断言 |
| 配置与三模式 | 源生成 JSON 往返以及 Expert、Meeting、Work、Resume、Provider Switch 全部通过离线确定性矩阵 |
| External 跳过项 | OpenAI、Anthropic、OpenAI Compatible、DashScope、Azure OpenAI 和本机 Ollama；均需显式开关及对应外部资源 |

### Step 8 验收

| 验收项 | 结果 |
| --- | --- |
| 完整 Debug 测试 | `948` 通过，`6` External 跳过，`0` 失败；原 `12` 个 CLI 进程制品名失败已随 `madorin-ai-cli.dll/.exe` 修正闭合 |
| Release 非增量构建 | `0` 警告，`0` 错误 |
| win-x64 Native AOT | Self-contained 发布成功，`0` trimming/AOT 警告；`madorin-ai-cli.exe` 为 `76,914,688` 字节 |
| 发布制品冒烟 | `version --json` 与 `--help` 均返回 `0`；产品 `Madorin.AI.Runtime`、版本 `0.1.1`、协议 `1.1` |
| 使用文档 | 安装与初始化指南已补充 11 种协议的认证、端点、模型来源、默认向导可见性和支持状态矩阵 |
| CodeMap / 依赖边界 | overlay revision `26`；新增 Provider 只依赖 Abstractions、Compatible 和必要的 `Microsoft.Extensions.Http`，无 `Netor.Cortana.*` 项目引用；公共模型发现接口不暴露 SDK 类型 |
| 未关闭限制 | 未提供真实厂商凭据，6 项 External 保持跳过；Azure OpenAI 无真实资源成功证据，继续保持实验隐藏；Aliyun/OpenAI 图片与视频能力不在本批范围 |
