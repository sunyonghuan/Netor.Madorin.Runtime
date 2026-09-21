# Expert 模式工具按模式过滤机制

## 1. 问题背景

当前架构中，所有模式（Expert/Work/Meeting）共享同一个 `ToolCatalogSnapshot`，没有按模式过滤工具的机制。这导致：

1. **工具泄露**：如果把 `mcli_expert_*` 工具注册到 `BuiltinToolRegistry`，Work/Meeting 模式也会看到
2. **违反规格**：`工作与会议模式.md` §7 明确要求：**Expert 不含任何 Work/Meeting 专用工具**
3. **宿主工具风险**：宿主插件工具命名不统一，无法用前缀过滤（如 `mcli_work_*`）可能误伤

## 2. 设计原则

### 2.1 核心约束

1. **协议不变**：`ToolCatalogItem` / `ToolCatalogReplaceRequest` / `ToolCatalogPatchRequest` **不增加字段**
2. **向后兼容**：现有宿主工具不受影响，默认所有模式可见
3. **内部标记**：`SupportedModes` 仅在 Runtime 内部使用，不暴露给宿主

### 2.2 过滤规则

- **内置工具**：显式标记适用模式（`SupportedModes: ["expert"]`）
- **宿主工具**：不带标记（`SupportedModes: null`），默认所有模式可见
- **通用工具**：标记为 `null`，所有模式可见（如 `mcli_fs_read`）

## 3. 实现方案

### 3.1 扩展 ToolDescriptor（Runtime 内部）

```csharp
// src/Madorin.AI.Runtime.Tools.Abstractions/ToolDescriptor.cs
public sealed record ToolDescriptor(
    string ToolId,
    string Namespace,
    string DisplayName,
    string Description,
    string InputSchemaJson,
    string OutputSchemaJson = "{}",
    string RiskLevel = "low",
    bool RequiresApproval = false,
    int TimeoutSeconds = 30,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? Capabilities = null,
    ToolRiskLevel Risk = ToolRiskLevel.Low,
    ToolExecutionTarget ExecutionTarget = ToolExecutionTarget.Builtin,
    bool IsIdempotent = true,
    IReadOnlyList<string>? SupportedModes = null);  // ✅ 新增字段
    // 值：null（通用） | ["expert"] | ["work"] | ["meeting"]
```

**注意**：`ToolCatalogItem`（协议层）**不增加** `SupportedModes` 字段。

### 3.2 ToolCatalogSnapshot 增加过滤方法

```csharp
// src/Madorin.AI.Runtime.Tools.Abstractions/ToolCatalogSnapshot.cs

/// <summary>按模式过滤工具目录。</summary>
/// <param name="mode">模式名称：expert / work / meeting</param>
/// <param name="agentRole">Agent 角色（Work/Meeting 模式需要）：general-manager / project-lead / meeting-host / participant</param>
public ToolCatalogSnapshot FilterByMode(string mode, string? agentRole = null)
{
    var filtered = _allTools.Where(tool =>
    {
        // 宿主工具或通用工具：不过滤（SupportedModes == null）
        if (tool.SupportedModes is null || tool.SupportedModes.Count == 0)
            return true;
        
        // 检查是否支持当前模式
        if (!tool.SupportedModes.Contains(mode, StringComparer.OrdinalIgnoreCase))
            return false;
        
        // Work 模式需要按角色过滤
        if (string.Equals(mode, "work", StringComparison.OrdinalIgnoreCase) && agentRole is not null)
            return IsAllowedForWorkRole(tool, agentRole);
        
        // Meeting 模式需要按角色过滤
        if (string.Equals(mode, "meeting", StringComparison.OrdinalIgnoreCase) && agentRole is not null)
            return IsAllowedForMeetingRole(tool, agentRole);
        
        return true;
    }).ToArray();
    
    return new ToolCatalogSnapshot(HostCatalogVersion, EffectiveVersion, filtered);
}

/// <summary>Work 模式按角色过滤（参考工作与会议模式.md §1）。</summary>
private static bool IsAllowedForWorkRole(ToolDescriptor tool, string agentRole)
{
    // general-manager (A): 所有 Work 工具
    if (string.Equals(agentRole, "general-manager", StringComparison.OrdinalIgnoreCase))
        return true;
    
    // project-lead (B): 只能用 dispatch_step + read_template_file，不能 ask_user / set_plan
    if (string.Equals(agentRole, "project-lead", StringComparison.OrdinalIgnoreCase))
    {
        return tool.ToolId switch
        {
            "mcli_ask_user" => false,  // B 不能问用户
            "mcli_work_set_plan" => false,
            "mcli_work_list_plan_templates" => false,
            "mcli_work_load_plan_from_template" => false,
            "mcli_work_load_plan_from_chat_history" => false,
            "mcli_work_load_plan_from_groupchat" => false,
            "mcli_work_get_recent_completed_task_plan" => false,
            "mcli_work_dispatch_step" => true,  // B 可以执行步骤
            "mcli_work_read_template_file" => true,  // B 可以读模板文件
            _ => true  // 其他通用工具
        };
    }
    
    return true;
}

/// <summary>Meeting 模式按角色过滤（参考工作与会议模式.md §1）。</summary>
private static bool IsAllowedForMeetingRole(ToolDescriptor tool, string agentRole)
{
    // meeting-host: 所有 Meeting 工具
    if (string.Equals(agentRole, "meeting-host", StringComparison.OrdinalIgnoreCase))
        return true;
    
    // 参会者 (participant): 只能 list_attachments，不能 output_summary / confirm_end / ask_user
    return tool.ToolId switch
    {
        "mcli_meeting_output_summary" => false,
        "mcli_meeting_confirm_end" => false,
        "mcli_ask_user" => false,
        "mcli_check_pending_user_input" => false,
        "mcli_meeting_list_attachments" => true,
        _ => true  // 其他通用工具
    };
}
```

### 3.3 BuiltinToolRegistry 标记工具范围

```csharp
// src/Madorin.AI.Runtime.Tools.Builtin/BuiltinToolRegistry.cs

// Expert 专用工具
private static ToolDescriptor CreateExpertCreatePlanTool() => new(
    ToolId: "mcli_expert_create_plan",
    Namespace: "expert",
    DisplayName: "Create Execution Plan",
    Description: "Create a step-by-step execution plan for medium-complexity tasks.",
    InputSchemaJson: "...",
    SupportedModes: new[] { "expert" }  // ✅ 只在 Expert 模式可见
);

private static ToolDescriptor CreateExpertExecuteStepTool() => new(
    ToolId: "mcli_expert_execute_step",
    Namespace: "expert",
    DisplayName: "Execute Plan Step",
    Description: "Execute a single step from the active plan.",
    InputSchemaJson: "...",
    SupportedModes: new[] { "expert" }
);

// Work 专用工具
private static ToolDescriptor CreateWorkSetPlanTool() => new(
    ToolId: "mcli_work_set_plan",
    Namespace: "work",
    DisplayName: "Set Work Plan",
    Description: "...",
    InputSchemaJson: "...",
    SupportedModes: new[] { "work" }  // ✅ 只在 Work 模式可见
);

// Meeting 专用工具
private static ToolDescriptor CreateMeetingOutputSummaryTool() => new(
    ToolId: "mcli_meeting_output_summary",
    Namespace: "meeting",
    DisplayName: "Output Meeting Summary",
    Description: "...",
    InputSchemaJson: "...",
    SupportedModes: new[] { "meeting" }  // ✅ 只在 Meeting 模式可见
);

// 通用工具（所有模式可见）
private static ToolDescriptor CreateFileReadTool() => new(
    ToolId: "mcli_fs_read",
    Namespace: "filesystem",
    DisplayName: "Read File",
    Description: "Read file contents.",
    InputSchemaJson: "...",
    SupportedModes: null  // ✅ 所有模式可见
);

private static ToolDescriptor CreateMemoryWriteTool() => new(
    ToolId: "mcli_memory_write",
    Namespace: "memory",
    DisplayName: "Write Memory",
    Description: "...",
    InputSchemaJson: "...",
    SupportedModes: null  // ✅ 所有模式可见
);
```

### 3.4 各 Orchestrator 调用过滤

```csharp
// src/Madorin.AI.Runtime.Modes.Expert/ExpertModeOrchestrator.cs
// 在准备工具目录时调用 FilterByMode
var catalogSnapshot = _toolGateway?.CaptureCatalogSnapshot()
    ?.FilterByMode("expert");  // ✅ 过滤出 Expert 模式可用的工具

// src/Madorin.AI.Runtime.Modes.Work/WorkModeOrchestrator.cs
var catalogSnapshot = _toolGateway?.CaptureCatalogSnapshot()
    ?.FilterByMode("work", currentAgentRole);  // ✅ 需要传入角色

// src/Madorin.AI.Runtime.Modes.Meeting/MeetingModeOrchestrator.cs
var catalogSnapshot = _toolGateway?.CaptureCatalogSnapshot()
    ?.FilterByMode("meeting", currentParticipantRole);  // ✅ 需要传入角色
```

### 3.5 宿主工具的处理

宿主通过 `tool.catalog.replace` 注册的工具：

```json
{
  "catalogVersion": "v1",
  "tools": [
    {
      "toolId": "my_custom_analysis_tool",
      "displayName": "Custom Analysis",
      "description": "Analyze data with custom logic.",
      "inputSchema": {...},
      "outputSchema": {...},
      "risk": "low",
      "timeoutSeconds": 60,
      "tags": ["analysis"],
      "capabilities": [],
      "executionTarget": "host",
      "requiresApproval": false,
      "isIdempotent": true
      // ❌ 协议层没有 supportedModes 字段
    }
  ]
}
```

Runtime 内部转换时：

```csharp
// src/Madorin.AI.Runtime.Tools/ToolCatalogStore.cs
private static ToolDescriptor FromCatalogItem(ToolCatalogItem item) => new(
    ToolId: item.ToolId,
    Namespace: ExtractNamespace(item.ToolId),
    DisplayName: item.DisplayName,
    Description: item.Description,
    InputSchemaJson: item.InputSchema.GetRawText(),
    OutputSchemaJson: item.OutputSchema.GetRawText(),
    Tags: item.Tags,
    Capabilities: item.Capabilities,
    Risk: item.Risk,
    ExecutionTarget: item.ExecutionTarget,
    RequiresApproval: item.RequiresApproval,
    TimeoutSeconds: item.TimeoutSeconds,
    IsIdempotent: item.IsIdempotent,
    SupportedModes: null  // ✅ 宿主工具默认为 null，所有模式可见
);
```

## 4. 测试用例

### 4.1 Expert 模式工具隔离

```csharp
[Test]
public void ExpertMode_ShouldNotSeeWorkAndMeetingTools()
{
    var allTools = new[]
    {
        CreateExpertPlanTool(),       // SupportedModes: ["expert"]
        CreateWorkSetPlanTool(),      // SupportedModes: ["work"]
        CreateMeetingConfirmEndTool(), // SupportedModes: ["meeting"]
        CreateFileReadTool()          // SupportedModes: null
    };
    
    var catalog = new ToolCatalogSnapshot("v1", "v1", allTools);
    var filtered = catalog.FilterByMode("expert");
    
    // Expert 应该只看到 expert 工具和通用工具
    Assert.That(filtered.Tools.Select(t => t.ToolId), Is.EquivalentTo(new[]
    {
        "mcli_expert_create_plan",
        "mcli_fs_read"
    }));
}
```

### 4.2 Work 模式按角色过滤

```csharp
[Test]
public void WorkMode_ProjectLead_ShouldNotSeeAskUser()
{
    var allTools = new[]
    {
        CreateAskUserTool(),          // SupportedModes: ["work", "meeting"]
        CreateWorkDispatchStepTool(), // SupportedModes: ["work"]
        CreateFileReadTool()          // SupportedModes: null
    };
    
    var catalog = new ToolCatalogSnapshot("v1", "v1", allTools);
    var filtered = catalog.FilterByMode("work", "project-lead");
    
    // project-lead 不能用 ask_user
    Assert.That(filtered.Tools.Select(t => t.ToolId), Is.Not.Contains("mcli_ask_user"));
    Assert.That(filtered.Tools.Select(t => t.ToolId), Does.Contain("mcli_work_dispatch_step"));
}
```

### 4.3 宿主工具在所有模式可见

```csharp
[Test]
public void HostTools_ShouldBeVisibleInAllModes()
{
    var hostTool = new ToolDescriptor(
        ToolId: "host_custom_tool",
        Namespace: "host",
        DisplayName: "Custom Tool",
        Description: "...",
        InputSchemaJson: "{}",
        SupportedModes: null  // 宿主工具
    );
    
    var catalog = new ToolCatalogSnapshot("v1", "v1", new[] { hostTool });
    
    Assert.That(catalog.FilterByMode("expert").Tools, Has.Count.EqualTo(1));
    Assert.That(catalog.FilterByMode("work").Tools, Has.Count.EqualTo(1));
    Assert.That(catalog.FilterByMode("meeting").Tools, Has.Count.EqualTo(1));
}
```

## 5. 实施清单

- [ ] 修改 `ToolDescriptor`，增加 `SupportedModes` 字段
- [ ] 实现 `ToolCatalogSnapshot.FilterByMode()`
- [ ] 实现 `IsAllowedForWorkRole()` 和 `IsAllowedForMeetingRole()`
- [ ] 修改 `BuiltinToolRegistry`，给所有内置工具标记范围
- [ ] 修改 `ExpertModeOrchestrator`，调用 `FilterByMode("expert")`
- [ ] 修改 `WorkModeOrchestrator`，调用 `FilterByMode("work", agentRole)`
- [ ] 修改 `MeetingModeOrchestrator`，调用 `FilterByMode("meeting", participantRole)`
- [ ] 确认 `ToolCatalogStore.FromCatalogItem()` 中宿主工具的 `SupportedModes` 为 `null`
- [ ] 编写单元测试：Expert/Work/Meeting 工具隔离、宿主工具可见性
- [ ] 更新规格文档：`工作与会议模式.md` §7 增加过滤机制说明

## 6. 风险与缓解

### 6.1 风险：协议扩展压力

**描述**：未来宿主可能需要注册模式专用工具。

**缓解**：
- 当前方案可以扩展：在 `ToolCatalogItem` 增加 `supportedModes` 字段（可选）
- 向后兼容：老版本宿主不传，Runtime 默认为 `null`

### 6.2 风险：Work/Meeting 角色过滤复杂

**描述**：`IsAllowedForWorkRole` 硬编码了工具 ID，不易维护。

**缓解**：
- 短期：保持简单，工具数量有限（< 15 个）
- 长期：引入 `RequiredRole` 元数据，在 `ToolDescriptor` 标记

## 7. 参考文档

- `docs/工作&会议/策划/工作与会议模式.md` §1 §7：工具可见性规则
- `docs/策划/Expert模式内置@委派机制设计文档.md`：Expert 委派机制
- `docs/策划/Expert模式小型计划功能实现方案.md`：Expert 计划功能

---

**状态**：设计完成，待实施
**优先级**：P0（Expert 计划功能的前置依赖）
**预计工作量**：1 个工作日
