# Madorin.AI.Runtime 使用文档

> **适用版本**：Madorin.AI.Runtime V1  
> **更新日期**：2026-09-18  
> **目标读者**：第三方开发者、集成方和 Provider 提供商

本目录提供 Madorin.AI.Runtime 的完整使用文档，包括独立 CLI 使用、SDK 集成、协议规范和 Provider 开发指南。

## 文档导航

### 快速开始

| 文档 | 说明 |
| --- | --- |
| [快速开始](./01-快速开始.md) | 5 分钟上手指南，快速体验 Runtime 核心功能 |
| [独立 CLI 使用指南](./02-独立CLI使用指南.md) | 独立启动方式，适合命令行直接使用 |
| [SDK 集成指南](./03-SDK集成指南.md) | 主程序集成方式，适合嵌入到应用程序中 |

### Expert 模式进阶

| 文档 | 说明 |
| --- | --- |
| [Expert 模式完整指南](./Expert模式完整指南.md) | Expert 模式的核心用法、子 Agent 委派、并行作业 |
| [请求控制参数详解](./请求控制参数详解.md) | 所有 RequestControl 字段、Provider 支持矩阵、最佳实践 |
| [小型计划功能使用指南](./小型计划功能使用指南.md) | 内置计划工具的详细用法与恢复机制 |

### 协议规范

| 文档 | 说明 |
| --- | --- |
| [协议规范总览](./04-协议规范/README.md) | Runtime 通信协议架构概述 |
| [Runtime 协议](./04-协议规范/01-Runtime协议.md) | Host ↔ Runtime 通信协议（Named Pipe） |
| [事件模型](./04-协议规范/02-事件模型.md) | 事件流、消息格式和类型定义 |
| [Session 与 Run 生命周期](./04-协议规范/03-Session与Run生命周期.md) | 会话与运行的完整生命周期 |
| [Provider 协议](./04-协议规范/04-Provider协议.md) | Runtime ↔ Provider Worker 通信协议（stdin/stdout） |

### Provider 开发

| 文档 | 说明 |
| --- | --- |
| [Provider 开发总览](./05-Provider开发指南/README.md) | Provider 开发架构与流程概述 |
| [语言无关实现指南](./05-Provider开发指南/03-语言无关实现指南.md) | 使用任意语言实现 Provider（含 Python/Go/Rust/Node.js 示例） |
| [C# 实现示例](./05-Provider开发指南/04-CSharp实现示例.md) | 使用 C# 和 Runtime 基础设施快速实现 Provider |

## 两种使用方式

### 方式一：独立 CLI

直接使用 `madorin` 命令行工具，适合：
- 快速原型验证
- 命令行自动化脚本
- 本地开发和调试

```bash
# 交互式运行（默认 Expert）
madorin run

# 执行单次任务
madorin run --provider deepseek --model deepseek-v4-pro --input "分析系统架构"

# 启动常驻服务
madorin serve --instance main
```

详见 [独立 CLI 使用指南](./02-独立CLI使用指南.md)。

### 方式二：SDK 集成

通过 `Madorin.AI.Runtime.Client` SDK 集成到应用程序，适合：
- 桌面应用程序
- 服务端应用
- 需要完整控制和事件处理的场景

```csharp
using Madorin.AI.Runtime.Client;

// 连接到 Runtime Server
var client = await RuntimeClient.ConnectAsync(options);

// 创建 Session 并启动 Run
var runId = await client.StartNewSessionRunAsync(request);

// 监听事件流
await foreach (var evt in client.ReadEventsAsync(0))
{
    // 处理事件
}
```

详见 [SDK 集成指南](./03-SDK集成指南.md)。

## 核心概念

| 概念 | 说明 |
| --- | --- |
| **Workspace** | 工作区，一个独立的数据隔离单元，通常对应一个项目目录 |
| **Runtime** | 运行时实例，管理一个 Workspace 的所有 Session 和 Run |
| **Session** | 会话，包含完整的对话历史和上下文 |
| **Run** | 单次运行，一个 Session 内的一轮执行 |
| **Provider** | 模型提供商适配器，负责与 AI 服务通信 |
| **Agent** | Agent 定义，包含提示词、模型选择和工具配置 |
| **Selection** | 选择配置，指定 Provider、Model 和 Agent |

详见 [协议规范总览](./04-协议规范/README.md)。

## 通信架构

```
┌─────────────────┐
│   Host 应用      │  (你的应用程序)
└────────┬────────┘
         │ Named Pipe (JSON 消息)
         ↓
┌─────────────────┐
│  Runtime Server │  (madorin serve 或 LocalRuntime)
└────────┬────────┘
         │ stdin/stdout (长度前缀 JSON 帧)
         ↓
┌─────────────────┐
│ Provider Worker │  (madorin-provider-openai 等独立进程)
└────────┬────────┘
         │ HTTPS
         ↓
┌─────────────────┐
│   AI 服务商      │  (OpenAI/Anthropic/etc.)
└─────────────────┘
```

- **Host ↔ Runtime**：通过 Named Pipe 传输 JSON 消息，支持认证、心跳和事件流
- **Runtime ↔ Provider**：通过 stdin/stdout 传输帧，每个协议一个独立 Worker 进程
- **Provider ↔ AI 服务**：标准 HTTPS 调用

## Provider 开发概述

Madorin.AI.Runtime 支持自定义 Provider，实现语言无关：

1. **进程模型**：每个 Provider 是一个独立的可执行文件
2. **通信协议**：通过 stdin/stdout 传输大端 UInt32 长度前缀的 JSON 帧
3. **生命周期**：Runtime 按需启动 Worker，每个协议 slug 最多一个进程
4. **语言支持**：任何能读写标准输入输出并解析 JSON 的语言

### 帧类型

| 帧类型 | 方向 | 说明 |
| --- | --- | --- |
| `hello` / `hello.ok` | Runtime → Worker / Worker → Runtime | 握手协议 |
| `invoke` | Runtime → Worker | 发起模型调用 |
| `event` | Worker → Runtime | 返回流式事件 |
| `cancel` | Runtime → Worker | 取消正在进行的调用 |
| `shutdown` | Runtime → Worker | 优雅关闭 |
| `error` | 双向 | 错误通知 |

详见 [Provider 开发指南](./05-Provider开发指南/README.md)。

## 相关文档

- [项目主 README](../../README.md)：项目概述、构建和发布
- [开发文档](../开发文档/)：深入的概念、命令和运维文档
- [方案设计](../方案设计/)：架构设计和技术决策
- [命令矩阵](../命令规范/09-CLI命令矩阵.md)：完整的 CLI 命令参考

## 获取帮助

- 查看 CLI 帮助：`madorin --help`
- 检查环境状态：`madorin doctor`
- 查看版本信息：`madorin version`

## 下一步

- **新手**：从 [快速开始](./01-快速开始.md) 开始
- **CLI 用户**：阅读 [独立 CLI 使用指南](./02-独立CLI使用指南.md)
- **集成开发者**：阅读 [SDK 集成指南](./03-SDK集成指南.md)
- **Provider 开发者**：阅读 [Provider 开发指南](./05-Provider开发指南/README.md)
