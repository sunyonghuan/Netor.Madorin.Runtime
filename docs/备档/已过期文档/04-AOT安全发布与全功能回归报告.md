# Native AOT 安全发布与全功能回归报告

> 测试日期：2026-07-27  
> 测试平台：Windows x64，.NET SDK 10.0.301  
> 测试对象：当前工作树构建的 `Madorin.AI.Runtime.Cli` 0.1.5  
> 发布目录：`.tmp/aot-safe-release/run-20260727-safe-size/publish-full`

## 1. 结论

本轮采用“优先保证功能完整”的 Native AOT 瘦身配置，发布成功，发布后的程序能够启动并完成本机可验证的主要功能回归。

- 自动化测试：发现 1,146 项，执行 1,137 项，1,137 项通过，0 项失败，9 项因外部环境不足未执行。
- Native AOT 命令树：51 项帮助入口全部正常；9 项非法参数全部返回预期错误。
- Native AOT 功能 smoke：配置、Agent、记忆、Skills、Session 查询、SQLite、存储维护、Runtime 服务、命名管道 IPC、无参数交互入口均已验证。
- Provider 异常链：假 Provider 的超时取消和网络失败都转换为结构化错误，没有 AOT 反射缺失、类型缺失或进程崩溃。
- 运行文件体积：`madorin.exe + e_sqlite3.dll` 合计 **55.6987 MiB**。

因此，`OptimizationPreference=Size` 配合现有 `rd.xml`、全部 AI 根程序集和栈追踪，可以作为当前最保险的瘦身发布方案。2026-07-27 已在最终 B1 产物上使用 `deepseek/deepseek-v4-pro` 补跑真实问答 smoke，非流式和流式请求均返回 `OK`、退出码均为 0；这补齐了一个真实 Provider/模型组合，但其他生产 Provider 和负载场景仍需按实际部署矩阵验证。

另有一个与 AOT 瘦身无关、但应修复的健壮性问题：当自定义 `MADORIN_CONFIG_DIR` 所在目录不允许程序重写 ACL 时，Agent 写操作会抛出未处理的 `UnauthorizedAccessException`。正常用户 Temp/用户配置目录中的 Agent CRUD 已验证通过。

## 2. 发布策略

本轮没有删除运行时根程序集，没有排除传递依赖，也没有关闭栈追踪。发布命令如下：

```powershell
dotnet publish src\Madorin.AI.Runtime.Cli\Madorin.AI.Runtime.Cli.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  --no-restore `
  -p:OptimizationPreference=Size `
  -p:UseAgentFrameworkRuntimeDirectives=true
```

Native AOT 编译响应文件已复核包含：

```text
--Os
--dehydrate
--stacktracedata
--rdxml:...\publish\rd.xml
--root:Microsoft.Agents.AI
--root:Microsoft.Agents.AI.Abstractions
--root:Microsoft.Agents.AI.OpenAI
--root:Microsoft.Extensions.AI
--root:Microsoft.Extensions.AI.Abstractions
```

该配置只启用体积优先优化和运行时元数据脱水，继续保留动态路径所需描述文件、AI Framework 根程序集和栈追踪，风险明显低于激进删除根程序集或排除传递依赖。

## 3. 发布物与体积

| 文件范围 | 字节数 | MiB | 分发建议 |
|---|---:|---:|---|
| `madorin.exe` | 56,493,056 | 53.8760 | 必需 |
| `e_sqlite3.dll` | 1,911,296 | 1.8228 | 必需 |
| 必要运行文件合计 | 58,404,352 | **55.6987** | 正式安装包口径 |
| 托管项目 PDB 合计 | 1,526,420 | 1.4557 | 独立符号包 |
| `madorin.pdb` | 395,661,312 | 377.3320 | 独立符号包，禁止进入安装包 |
| 当前发布目录全部文件 | - | 434.4865 | 不应直接作为安装包 |

校验哈希：

```text
madorin.exe
872A538DC2960129268DF5A81B6243683981AC7C3966D477BADAD64AC8E5BA21

e_sqlite3.dll
AABF85D7A8B416FB15203CB754FCFC9858C8F1DD3BBC7EAB82335F6C362BA0D6
```

“发布后达到数百 MiB”主要是把 PDB 一并计算造成的；PDB 不参与程序运行。当前实际交付体积应按 55.70 MiB 计算，而不是按整个发布目录的 434.49 MiB 计算。

## 4. 自动化回归

使用 Release 配置执行 7 个测试项目，TRX 结果汇总如下：

| 指标 | 数量 |
|---|---:|
| 发现 | 1,146 |
| 执行 | 1,137 |
| 通过 | 1,137 |
| 失败 | 0 |
| 未执行 | 9 |

9 项未执行用例依赖当前机器没有提供的外部环境，主要包括真实 OpenAI、Anthropic、OpenAI Compatible、Azure OpenAI、Ollama Provider，以及缺少对应解释器的 `.csx`、`.sh` 用例。它们不是测试失败，但也不能视为真实集成已通过。

TRX 证据目录：

```text
.tmp/aot-safe-release/run-20260727-safe-size/managed-tests
```

## 5. 发布版命令与功能验证

### 5.1 命令树和参数校验

- 51 个命令/子命令帮助入口：退出码全部为 0，Usage 全部复核匹配。
- 9 组非法参数：退出码全部为 2，结构化错误消息全部与快照匹配。
- 顶级命令第一次由 PowerShell 单元素数组解包导致重复执行根帮助；改用强类型参数数组后，根命令和 12 个顶级命令共 13 项全部复核通过。这是测试驱动问题，不是发布程序问题。

证据文件：

```text
.tmp/aot-safe-release/run-20260727-safe-size/cli-command-matrix.json
.tmp/aot-safe-release/run-20260727-safe-size/cli-top-level-help.json
```

### 5.2 配置、Agent、记忆和 Skills

以下命令均直接调用发布后的 `madorin.exe`：

| 功能 | 结果 |
|---|---|
| `version` | 通过，返回 0.1.5、协议 1.1、win-x64 |
| `config validate` / `config show` | 通过，JSON 可解析且 `success=true` |
| `agent list` | 通过 |
| `agent create` / `agent list` / `agent delete` | 在正常用户 Temp 配置目录通过 |
| `memory init` / `show` / `clear` | 通过 |
| `skill refresh` / `list` / `validate` | 通过；隔离目录没有 Skills 时正确返回空目录结果 |
| `session list` | 通过，空数据时正确返回空集合 |

测试配置使用 `https://safe-test.invalid/v1` 和虚假密钥，不会调用真实 Provider，也没有写入用户正式配置。

### 5.3 SQLite 和存储维护

Runtime 实际初始化 schema 16、WAL 模式的数据目录后，以下功能全部退出码 0、JSON 可解析且 `success=true`：

- `db check`
- `db migrate --dry-run`
- `db repair --dry-run`
- `db rebuild --dry-run`
- `db vacuum`
- `db backup`
- `storage check --verify-hashes`
- `storage gc --dry-run`
- `session list --status all`

备份文件成功生成，大小 368,640 字节。证据文件：

```text
.tmp/aot-safe-release/run-20260727-safe-size/aot-storage-matrix.json
```

### 5.4 Runtime、IPC 和交互入口

使用临时 .NET 10 驱动启动发布后的 Native AOT 程序，避免 Windows PowerShell 5.1 标准输入编码影响认证：

| 场景 | 结果 |
|---|---|
| Runtime Server 启动 | 通过，进程启动并初始化 schema 16 |
| IPC 握手认证 | 通过 |
| `ctl status` | 退出码 0，`success=true` |
| `ctl sessions` | 退出码 0，`success=true` |
| `ctl runs` | 退出码 0，`success=true` |
| `ctl cancel missing-run` | 退出码 1，正确返回 `CancelRejected` |
| 真正零参数交互启动 | 退出码 0，显示 Banner 和隔离配置，标准错误为空 |

服务端已正常退出或由测试驱动按超时策略结束，没有遗留运行中的发布版进程。

### 5.5 Provider 错误和取消链

使用 `.invalid` 假域名执行一次性 `run`：

| 场景 | 退出码 | 结果 |
|---|---:|---|
| 1 秒超时 | 130 | 结构化 `RunCancelled` |
| 10 秒超时上限 | 1 | 结构化 `ProviderRequestFailed`，`isRetryable=true` |

两个场景均没有未处理异常、Native AOT 类型缺失、序列化失败或进程崩溃。这证明发布版的 Provider 失败路径和取消路径可用，但不等同于真实 Provider 成功响应验证。

## 6. 已知问题和未覆盖项

### P1：自定义配置目录 ACL 失败时存在未处理异常

当 `MADORIN_CONFIG_DIR` 指向仓库 `.tmp` 这类不允许程序收紧 ACL 的目录时，`agent create` 在配置锁写入阶段抛出未处理的 `UnauthorizedAccessException`。同一功能在系统用户 Temp 配置目录成功，说明不是 Native AOT 裁剪导致。

建议配置加载器在 `SetAccessControl` 失败时返回明确的配置/权限错误；是否允许保留继承 ACL 后继续写入，需要结合安全策略决定。修复前应把“配置目录必须允许当前用户修改 ACL”写入部署约束。

### P1：真实 Provider 覆盖矩阵仍不完整

补充验证已在最终 B1 产物上使用 `deepseek/deepseek-v4-pro` 完成一次非流式和一次流式请求，两次均返回 `OK`、退出码均为 0。API Key 只保存在用户级配置中，报告不记录其值。OpenAI、Anthropic、Azure OpenAI 等其他协议尚未执行真实成功请求；正式放行仍应按生产实际使用的 Provider 逐一完成非流式和流式验证。

### P2：部分外部解释器功能未验证

`.csx`、`.sh` 相关用例因当前 Windows 环境缺少解释器而未执行。若正式产品承诺这些能力，应在带对应解释器的目标环境补测。

### 可复现性说明

本次发布基于当前有未提交修改的工作树，而不是完全干净的 Git 提交。测试结果对当前生成物有效；正式归档时应先确定提交版本，再用相同参数重新发布并记录哈希。

## 7. 放行建议

当前发布物满足“最保险瘦身配置的候选版本”要求：本机可验证功能未发现由 `OptimizationPreference=Size` 引起的回归，55.70 MiB 的必要运行体积也比 70 多 MiB 的原始口径明显下降。

建议按以下门禁处理：

1. 内部测试/候选版本：可以放行。
2. 正式外部发布：补齐实际使用 Provider 的流式、非流式 smoke 后放行。
3. 安装包：只收录 `madorin.exe`、`e_sqlite3.dll` 及确有需要的非符号资源；所有 PDB 进入独立符号包。
4. 后续修复配置目录 ACL 异常，避免不符合权限条件的自定义目录导致进程级崩溃。

## 8. P7 最终发布对齐

P7 完成后的安全发布结论保持不变：B1 `Size + Full roots` 是默认生产版。最终白名单包位于 `.artifacts/p7-manifests/release-0.1.5-cli-size-full.zip`，ZIP SHA-256 为 `8f3d1c8d79b03225047c5a3618061b57b2b7f2899df57d8cf4f133721ca52f48`。最终 CLI 为 56,492,544 字节（53.8755 MiB），SHA-256 为 `a449e3b5f0de48ccc05e00fe8d2e1057e94ade26c7f74b2cd330f7859525a2b1`。

最终复检补充证据：

- Debug/Release 全解决方案构建均为 0 警告、0 错误；两轮全量测试均为 1137 通过、9 跳过、0 失败。
- ZIP 解压后的 `madorin.exe` 与 `e_sqlite3.dll` 逐文件 SHA-256 与 manifest 一致。
- 解压版 `version --json`、MConsole P0、P5、P6 均 `passed=True`。
- `Directory.Build.targets` 仅在 Release、Exe、`PublishAot=true` 且已提供 RID 的发布上下文设置 `OptimizationPreference=Size`；普通解决方案构建不被 SelfContained/RID 污染。`Directory.Build.props` 中的 `AgentFrameworkRootProfile` 默认仍为 `Full`。
- 当前环境没有 7z/7zz/UPX；默认发行只使用 Windows 兼容的 ZIP Optimal，不使用可执行压缩壳。
