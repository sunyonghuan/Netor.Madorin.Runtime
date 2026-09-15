# Expert模式内置@委派机制设计文档

## 设计目标

为Expert模式实现内置的`@子智能体`委派机制，**不依赖`mcli_delegate_agent`工具**，参考Work和Meeting模式的设计思路。

## 背景

### 当前三种模式的委派机制对比

| 模式 | 委派时机 | 委派方式 | 实现层 | 工具依赖 |
|------|---------|----------|--------|---------|
| Work | Planning阶段 | GM在Plan中设置`targetAgentId` | Orchestrator | ❌ 无 |
| Meeting | 每轮开始 | Host输出JSON `{"participantId": "..."}` | Orchestrator | ❌ 无 |
| Expert | 运行时 | ❌ **已删除的工具** | 工具层 | ✅ mcli_delegate_agent |

### 问题

Expert模式原本通过`mcli_delegate_agent`工具实现委派，但该工具已被删除（commit feceb66），导致Expert模式的`@子智能体`语法**完全失效**。

## 设计方案B：为Expert模式设计内置机制

### 核心思路

**在ExpertModeOrchestrator层解析用户输入，检测`@语法`，并直接调用子Agent**，类似Work/Meeting模式的内置机制。

### 架构设计

```
用户输入: "@developer read file.txt"
    ↓
ExpertModeOrchestrator.RunAsync()
    ↓
[1] 预处理阶段：解析输入，检测@语法
    - 正则匹配: @(\w+)\s+(.+)
    - 提取: agentName="developer", task="read file.txt"
    ↓
[2] 子Agent调用判断
    - 如果检测到@语法 → 进入子Agent调用分支
    - 否则 → 正常的单Agent执行流程
    ↓
[3] 子Agent调用分支
    - 从ExpertModeOptions.AvailableAgents查找目标Agent
    - 创建子Agent的Provider请求
    - 执行子Agent（允许其调用工具）
    - 将子Agent结果合并回主对话
    ↓
[4] 继续主Agent执行
    - 主Agent看到子Agent的结果
    - 主Agent继续对话或结束
```

### API变更

#### 1. ExpertModeOptions需要支持Agent池

**现状**：
```csharp
public record ExpertModeOptions(AgentRef Agent);  // 单Agent
```

**变更后**：
```csharp
public record ExpertModeOptions(
    AgentRef Agent,                    // 主Expert Agent
    AgentRef[]? AvailableAgents = null // 可用的子Agent池（用于@委派）
);
```

#### 2. ExpertModeOrchestrator需要支持子Agent调用

**新增方法**：
```csharp
private static (bool isDelegation, string? agentName, string? task) TryParseDelegationSyntax(string input)
{
    // 解析 "@agentName do something" 语法
    var match = Regex.Match(input, @"^@(\w+)\s+(.+)$", RegexOptions.Singleline);
    if (match.Success)
    {
        return (true, match.Groups[1].Value, match.Groups[2].Value);
    }
    return (false, null, null);
}

private async Task<(ContentBlock[] content, bool success)> InvokeSubAgentAsync(
    string agentName,
    string task,
    string sessionId,
    string invocationId,
    string parentAgentId,
    CancellationToken ct)
{
    // 1. 从AvailableAgents查找目标Agent
    // 2. 创建子Agent的Provider请求
    // 3. 执行子Agent（完整的工具调用循环）
    // 4. 返回子Agent的输出内容
}
```

**修改RunAsync主流程**：
```csharp
public async IAsyncEnumerable<RuntimeEventEnvelope> RunAsync(...)
{
    // ... 前置代码 ...

    // ✅ 新增：检测@委派语法
    var (isDelegation, agentName, task) = TryParseDelegationSyntax(request.InitialInput);

    if (isDelegation && expert.AvailableAgents is { Length: > 0 })
    {
        // 子Agent调用分支
        var (subAgentContent, success) = await InvokeSubAgentAsync(
            agentName!,
            task!,
            sessionId,
            invocationId,
            expert.Agent.AgentId,
            ct);

        if (success)
        {
            // 将子Agent的输出作为一条assistant消息追加到对话
            await AppendContentAsync(
                sessionId,
                invocationId,
                agentName!,
                "assistant",
                subAgentContent,
                DateTimeOffset.UtcNow,
                ct);

            // 继续主Agent的执行（主Agent可以看到子Agent的结果）
            // ... 正常的Provider循环 ...
        }
        else
        {
            // 子Agent调用失败，返回错误
            yield return CreateErrorEnvelope($"Failed to invoke sub-agent '{agentName}'");
            yield break;
        }
    }
    else
    {
        // 正常的单Agent执行流程（现有逻辑）
        // ... 不变 ...
    }
}
```

### System Prompt注入

与Work/Meeting模式类似，当`AvailableAgents`非空时，向主Agent的System Prompt注入可用子Agent列表：

```csharp
private static string AppendAvailableSubAgentsList(string baseInstructions, AgentRef[]? availableAgents)
{
    if (availableAgents is not { Length: > 1 })
    {
        return baseInstructions;
    }

    var agentListBuilder = new StringBuilder();
    agentListBuilder.AppendLine();
    agentListBuilder.AppendLine();
    agentListBuilder.AppendLine("## Sub-Agent Delegation in Expert Mode");
    agentListBuilder.AppendLine();
    agentListBuilder.AppendLine("When you need to delegate a task to a specialized agent, use the following syntax:");
    agentListBuilder.AppendLine();
    agentListBuilder.AppendLine("```");
    agentListBuilder.AppendLine("@agentName <task description>");
    agentListBuilder.AppendLine("```");
    agentListBuilder.AppendLine();
    agentListBuilder.AppendLine("Available sub-agents:");
    agentListBuilder.AppendLine();

    foreach (var agent in availableAgents)
    {
        if (agent.AgentId != expert.Agent.AgentId) // 排除自己
        {
            agentListBuilder.AppendLine($"- **@{agent.AgentId}** - {agent.SystemPrompt?.Split('\n')[0]}");
        }
    }

    agentListBuilder.AppendLine();
    agentListBuilder.AppendLine("The orchestrator will automatically invoke the specified sub-agent and return its result to you.");

    return baseInstructions + agentListBuilder.ToString();
}
```

### 工具调用支持

子Agent执行时需要完整的工具调用能力：

```csharp
private async Task<(ContentBlock[] content, bool success)> InvokeSubAgentAsync(...)
{
    // 子Agent的工具调用循环（类似主Agent的循环）
    for (var toolRound = 0; toolRound < maxToolRounds; toolRound++)
    {
        // 1. 调用Provider
        var response = await _provider.CompleteAsync(subAgentRequest, ct);

        // 2. 检查工具调用
        var toolCalls = response.Content.OfType<ToolCallContentBlock>().ToArray();
        if (toolCalls.Length == 0)
        {
            // 没有工具调用，返回最终内容
            return (response.Content, true);
        }

        // 3. 执行工具
        foreach (var toolCall in toolCalls)
        {
            var result = await _toolGateway.ExecuteAsync(
                new ToolInvocation(
                    toolCall.CallId,
                    toolCall.ToolId,
                    agentName,            // 子Agent ID
                    expert.Agent.AgentId, // ✅ ParentAgentId = 主Agent ID
                    toolCall.Arguments.GetRawText(),
                    runId,
                    sessionId,
                    invocationId,
                    ToolCatalogVersion: catalogVersion),
                permission: null,
                catalogSnapshot,
                ct);

            // 添加工具结果到对话
            subAgentMessages.Add(new RuntimeProviderMessage(
                RuntimeProviderRoles.Tool,
                [CreateToolResultContent(toolCall, result)]));
        }
    }

    return (finalContent, true);
}
```

### 事件流输出

子Agent调用时需要发出相应的事件：

```csharp
// 子Agent调用开始
yield return CreateEnvelope(
    MessageTypes.SubAgentInvocationStarted,
    new { AgentName = agentName, Task = task });

// 子Agent的Text Delta
yield return CreateEnvelope(
    MessageTypes.TextDelta,
    new { InvocationId = subInvocationId, Delta = "..." });

// 子Agent的工具调用
yield return CreateEnvelope(
    MessageTypes.ToolCallCompleted,
    toolCallEvent);

// 子Agent调用完成
yield return CreateEnvelope(
    MessageTypes.SubAgentInvocationCompleted,
    new { AgentName = agentName, Success = true });
```

### 对话历史记录

子Agent的输出需要记录到对话历史：

```csharp
// 子Agent的输出作为一条assistant消息
await _store.AppendMessageAsync(
    sessionId,
    "Expert",
    new ConversationMessageDraft(
        invocationId,
        agentName,          // ✅ AgentId = 子Agent ID
        "assistant",
        JsonSerializer.SerializeToElement(subAgentContent),
        DateTimeOffset.UtcNow),
    ct);
```

主Agent在后续轮次中可以看到这条消息：
```
User: @developer read file.txt
Developer: I've read the file. It contains "Hello World".
User: Thanks! Now analyze it.
[主Agent看到Developer的回复，继续分析]
```

## 实现步骤

### Phase 1: 最小可行实现
1. ✅ 修改`ExpertModeOptions`，添加`AvailableAgents`字段
2. ✅ 实现`TryParseDelegationSyntax`方法
3. ✅ 在`RunAsync`中添加分支判断
4. ✅ 实现`InvokeSubAgentAsync`（无工具调用）
5. ✅ 测试基本的@委派功能

### Phase 2: 工具调用支持
6. ✅ 在`InvokeSubAgentAsync`中添加工具调用循环
7. ✅ 设置正确的`ParentAgentId`
8. ✅ 测试子Agent调用文件工具

### Phase 3: 完善与优化
9. ✅ 实现System Prompt注入
10. ✅ 添加事件流输出
11. ✅ 错误处理和边界情况
12. ✅ 性能优化和资源清理

## 测试用例

### 测试1：基本@委派
```
用户: @developer say hello
预期: Developer Agent被调用并返回 "Hello!"
```

### 测试2：子Agent调用文件工具
```
用户: @developer read /tmp/test.txt
预期:
1. Developer Agent被调用
2. Developer调用mcli_fs_read工具
3. Developer返回文件内容
```

### 测试3：主Agent处理子Agent结果
```
用户: @developer read file.txt, then summarize it
预期:
1. Developer读取文件
2. 主Agent看到Developer的结果
3. 主Agent进行总结
```

### 测试4：错误处理
```
用户: @unknown_agent do something
预期: 返回错误 "Sub-agent 'unknown_agent' not found in AvailableAgents"
```

## 与Work/Meeting模式的一致性

| 特性 | Work | Meeting | Expert (新设计) |
|------|------|---------|-----------------|
| 委派时机 | Planning | 每轮开始 | 用户输入时 |
| 委派方式 | targetAgentId | JSON participantId | @agentName语法 |
| Agent池注入 | AvailableAgents | Participants | AvailableAgents |
| System Prompt注入 | ✅ | ✅ | ✅ |
| 工具调用支持 | ✅ | ✅ | ✅ |
| ParentAgentId | null | null | 主Agent ID |
| 工具依赖 | ❌ | ❌ | ❌ |

## 向后兼容性

- 如果`ExpertModeOptions.AvailableAgents`为null或空，保持现有行为（单Agent模式）
- 不影响现有的Expert模式使用场景
- 新的@委派功能是**可选增强**

## 文档更新

需要更新`docs/CLI/开发文档/21-Agent委派使用指南.md`：

- 第3节：添加Expert模式的@委派机制说明
- 第5节：添加Expert模式的测试用例
- 第7节：添加Expert模式的API参考

---

**当前状态**: 设计完成，待实现

**预计工作量**: 
- Phase 1: 2-3小时
- Phase 2: 1-2小时
- Phase 3: 1-2小时
- 总计: 4-7小时
