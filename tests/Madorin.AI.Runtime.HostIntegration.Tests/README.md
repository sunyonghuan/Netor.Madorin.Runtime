# 宿主集成测试方案

## 概述

本文档描述了完整的宿主集成测试方案，用于验证三种模式（Work、Meeting、Expert）的Agent委派和工具调用功能。

## 测试目标

1. **验证Agent集合注入**：宿主通过API显式注入Agent池，而不是依赖CLI自动发现
2. **验证委派机制**：
   - Work模式：GM通过`targetAgentId`委派给子Agent
   - Meeting模式：Host通过JSON `participantId`委派给Participant
   - Expert模式：通过内置@语法委派给子Agent（待实现）
3. **验证工具调用**：委派的Agent能够成功调用工具（文件、进程、HTTP）
4. **验证完整链路**：从宿主API调用 → 委派 → 工具调用 → 结果返回

## 测试场景

### 场景1：Work模式 - GM委派Developer读取文件

**步骤**：
1. 宿主创建3个Agent：GM、Developer、Tester
2. 宿主通过`WorkModeOptions`注入Agent池
3. 用户输入：`"Let developer read /path/to/file.txt and tell me its content"`
4. GM生成Plan，step中设置`targetAgentId: "developer"`
5. WorkModeOrchestrator调用Developer Agent
6. Developer Agent调用`mcli_fs_read`工具读取文件
7. Developer返回文件内容

**验证点**：
- ✅ GM的System Prompt包含注入的Agent列表
- ✅ Plan中的`targetAgentId`正确设置为"developer"
- ✅ Developer Agent被成功调用
- ✅ `mcli_fs_read`工具被调用并返回文件内容
- ✅ 最终输出包含文件内容

---

### 场景2：Meeting模式 - Host委派Developer Participant执行文件操作

**步骤**：
1. 宿主创建Host和2个Participant（Developer、Tester）
2. 宿主通过`MeetingModeOptions`注入Participant列表
3. 用户输入：`"Let's discuss file.txt. Developer should read it."`
4. Host输出JSON：`{"participantId": "cli-developer", "conclude": false}`
5. MeetingModeOrchestrator调用Developer Participant
6. Developer调用`mcli_fs_read`工具
7. Developer返回文件内容

**验证点**：
- ✅ Host的System Prompt包含注入的Participant列表
- ✅ Host输出的JSON格式正确
- ✅ Developer Participant被成功调用
- ✅ `mcli_fs_read`工具被调用
- ✅ 最终输出包含文件内容

---

### 场景3：Expert模式 - 主Agent通过@委派子Agent

**状态**：⚠️ **待实现**

**设计方案**：
Expert模式需要新的内置委派机制（方案B），参考Work/Meeting模式的设计：

1. ExpertModeOrchestrator在执行前解析用户输入
2. 检测`@agentName`语法（如`@developer do something`）
3. 提取`agentName`和`task`
4. 创建子Agent调用上下文
5. 调用指定的子Agent
6. 将子Agent的结果合并回主对话

**实现要点**：
- 不依赖`mcli_delegate_agent`工具
- 在Orchestrator层拦截和解析@语法
- 支持子Agent调用工具
- 保持与Work/Meeting模式的一致性

---

## 宿主API调用示例

### Work模式完整示例

```csharp
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Modes.Work;

// 1. 创建Runtime Core
var runtime = new RuntimeExecutionCore(
    dataRoot: "/path/to/data",
    providerConfigs: new[] { mockProviderConfig },
    // ... 其他配置
);

// 2. 定义Agent
var gmRef = new AgentRef(
    AgentId: "gm",
    ProviderId: "deepseek",
    ModelId: "deepseek-v4-flash",
    SystemPrompt: "You are a general manager. Delegate tasks to specialized agents.",
    Temperature: 0.7);

var developerRef = new AgentRef(
    AgentId: "developer",
    ProviderId: "deepseek",
    ModelId: "deepseek-v4-flash",
    SystemPrompt: "You are a software developer. Use tools to complete file operations.",
    Temperature: 0.7);

var testerRef = new AgentRef(
    AgentId: "tester",
    ProviderId: "deepseek",
    ModelId: "deepseek-v4-flash",
    SystemPrompt: "You are a QA tester.",
    Temperature: 0.7);

// 3. 创建Work模式选项（显式注入Agent池）
var workOptions = new WorkModeOptions(
    GeneralManager: gmRef,
    AvailableAgents: new[] { gmRef, developerRef, testerRef },
    WorkflowPolicy: WorkflowPolicy.Default);

// 4. 创建Run请求
var request = new NewSessionRunRequest(
    Mode: RuntimeMode.Work,
    InitialInput: "Let developer read /tmp/test.txt and tell me its content",
    Selection: new NextTurnSelection(
        Mode: RuntimeMode.Work,
        DefaultSelection: new ProviderModelSelection("deepseek", "deepseek-v4-flash"),
        ModeOptions: workOptions,
        ToolCatalogVersion: null
    )
);

// 5. 执行Run
var sessionId = Guid.NewGuid().ToString("N");
var runId = Guid.NewGuid().ToString("N");

await foreach (var envelope in runtime.RunAsync(runId, sessionId, request, CancellationToken.None))
{
    // 处理事件流
    Console.WriteLine($"{envelope.MessageType}: {envelope.Payload}");
}
```

### Meeting模式完整示例

```csharp
// 1. 定义Participants
var hostRef = new AgentRef(
    AgentId: "host",
    ProviderId: "deepseek",
    ModelId: "deepseek-v4-flash",
    SystemPrompt: "You are a meeting host. Select participants to speak.",
    Temperature: 0.7);

var developerParticipant = new MeetingParticipant(
    ParticipantId: "cli-developer",
    DisplayName: "Developer",
    Agent: developerRef,
    Status: ParticipantStatus.Active);

var testerParticipant = new MeetingParticipant(
    ParticipantId: "cli-tester",
    DisplayName: "Tester",
    Agent: testerRef,
    Status: ParticipantStatus.Active);

// 2. 创建Meeting选项（显式注入Participant列表）
var meetingOptions = new MeetingModeOptions(
    Host: hostRef,
    Participants: new[] { developerParticipant, testerParticipant },
    MeetingPolicy: MeetingPolicy.Default,
    Selector: null);

// 3. 创建Run请求
var request = new NewSessionRunRequest(
    Mode: RuntimeMode.Meeting,
    InitialInput: "Let's discuss the file /tmp/test.txt. Developer should read it.",
    Selection: new NextTurnSelection(
        Mode: RuntimeMode.Meeting,
        DefaultSelection: new ProviderModelSelection("deepseek", "deepseek-v4-flash"),
        ModeOptions: meetingOptions,
        ToolCatalogVersion: null
    )
);

// 4. 执行（同Work模式）
```

---

## 工具调用验证清单

### 文件工具（mcli_fs_*）

- [ ] `mcli_fs_read`：读取文件内容
- [ ] `mcli_fs_write`：写入文件
- [ ] `mcli_fs_list`：列出目录内容
- [ ] `mcli_fs_delete`：删除文件

### 进程工具（mcli_process_*）

- [ ] `mcli_process_exec`：执行命令

### HTTP工具（mcli_http_*）

- [ ] `mcli_http_request`：发送HTTP请求

### 工具授权流程

- [ ] NeedsPermission → WaitingForApproval → Approved → Executed
- [ ] 工具权限在Agent委派场景下正常工作

---

## 当前状态

| 场景 | 状态 | 备注 |
|------|------|------|
| Work模式委派机制 | ✅ 已验证 | System Prompt注入正确 |
| Meeting模式委派机制 | ✅ 已验证 | System Prompt注入正确 |
| Expert模式委派机制 | ❌ 未实现 | 需要实现方案B |
| 宿主API调用测试 | ⚠️ 进行中 | 框架代码编写中 |
| 工具调用测试 | ⚠️ 待测试 | 需要完整环境 |

---

## 下一步行动

1. **优先级1**：实现Expert模式的内置@委派机制（方案B）
2. **优先级2**：完成宿主集成测试框架（WorkModeHostIntegrationTests.cs）
3. **优先级3**：添加工具调用验证测试
4. **优先级4**：运行完整的端到端测试并记录结果

---

## 参考文档

- `docs/CLI/开发文档/21-Agent委派使用指南.md` - 委派机制说明
- `src/Madorin.AI.Runtime.Modes.Work/WorkModeOrchestrator.cs:2100` - Work模式注入逻辑
- `src/Madorin.AI.Runtime.Modes.Meeting/MeetingModeOrchestrator.cs:3659` - Meeting模式注入逻辑
- `samples/Madorin.AI.Runtime.SampleHost/ReferenceHost.cs` - 宿主集成参考实现
