# Work 模式使用指南

> 多智能体协作的任务编排与执行

Work 模式通过"总经理 → 项目组长 → 员工"的三层架构，将复杂任务自动分解为步骤，并通过多个专业智能体协作完成。本文档帮助你理解 Work 模式的工作原理、使用方式和最佳实践。

---

## 1. 快速开始

### 1.1 启动 Work 模式

**命令行方式：**

```bash
# 单次任务执行
madorin run --mode work --input "分析项目代码并生成优化建议报告"

# 交互式 REPL
madorin --mode work
```

**SDK 方式：**

```csharp
var request = new NewSessionRunRequest(
    SessionIdempotencyKey: Guid.NewGuid().ToString(),
    RunIdempotencyKey: Guid.NewGuid().ToString(),
    Mode: RuntimeMode.Work,
    Selection: new NextTurnSelection
    {
        ProviderId = "my-openai",
        Model = "gpt-4o"
    },
    InitialInput: new[]
    {
        ContentBlock.CreateText("分析项目代码并生成优化建议报告")
    },
    ModeOptions: new WorkModeOptions
    {
        WorkflowPolicy = new WorkflowPolicy
        {
            MaxStepsPerRun = 20,
            MaxConcurrentSteps = 4
        }
    }
);

var runId = await client.StartNewSessionRunAsync(request);
```

### 1.2 基本执行流程

1. **用户输入任务**："分析项目代码并生成优化建议报告"
2. **总经理制定计划**：自动分解为多个步骤（如"扫描代码"、"分析性能"、"生成报告"）
3. **项目组长调度执行**：按依赖关系调度各步骤，支持并发执行
4. **员工执行任务**：每个专业智能体完成自己负责的步骤
5. **总经理生成总结**：汇总所有步骤结果，输出最终报告

---

## 2. 核心概念

### 2.1 三层架构

Work 模式采用企业化的三层管理架构：

| 角色 | 职责 | Agent 类型 |
|------|------|-----------|
| **总经理（General Manager）** | 任务分解、计划制定、结果汇总 | `general-manager` |
| **项目组长（Supervisor）** | 步骤调度、验证、重试、汇总 | `project-lead` |
| **员工（Worker）** | 执行具体步骤，调用工具完成工作 | 任意可用 Agent |

**执行流程：**

```
用户任务
   ↓
总经理：制定计划（WorkPlanDraft）
   ↓
项目组长：调度步骤执行
   ├→ 员工A：执行步骤1.1
   ├→ 员工B：执行步骤1.2
   └→ 员工C：执行步骤2.1（等待步骤1完成）
   ↓
项目组长：验证结果、生成汇总
   ↓
总经理：生成最终报告
```

### 2.2 二级步骤结构

Work 模式的计划采用**二级步骤结构**：

- **一级步骤**：交付物阶段（如"代码扫描"、"性能分析"、"报告生成"）
- **二级步骤**：实际执行单元（如"扫描Java代码"、"扫描Python代码"）

**示例：**

```json
{
  "goal": "分析项目代码并生成优化建议报告",
  "steps": [
    {
      "stepId": "1",
      "title": "代码扫描",
      "description": "扫描项目中的所有代码文件",
      "subSteps": [
        {
          "subStepId": "1.1",
          "goal": "扫描Java代码",
          "instruction": "使用代码扫描工具分析所有.java文件",
          "targetAgentId": "code-analyzer"
        },
        {
          "subStepId": "1.2",
          "goal": "扫描Python代码",
          "instruction": "使用代码扫描工具分析所有.py文件",
          "targetAgentId": "code-analyzer"
        }
      ]
    },
    {
      "stepId": "2",
      "title": "性能分析",
      "description": "分析代码的性能瓶颈",
      "dependsOn": ["1"],
      "subSteps": [
        {
          "subStepId": "2.1",
          "goal": "分析性能热点",
          "instruction": "基于扫描结果，识别性能瓶颈",
          "targetAgentId": "performance-analyst"
        }
      ]
    }
  ]
}
```

**关键特性：**

- **依赖关系**：`dependsOn` 定义步骤间的依赖（如步骤2依赖步骤1完成）
- **并发执行**：无依赖的步骤可并发执行（如1.1和1.2）
- **自动汇总**：一级步骤的所有二级步骤完成后，自动生成汇总报告

### 2.3 DAG 调度

步骤间的依赖关系形成**有向无环图（DAG）**：

```
步骤1.1 ─┐
         ├─→ 步骤2.1 ─→ 步骤3.1
步骤1.2 ─┘
```

- **拓扑排序**：按依赖关系自动调度
- **并发执行**：无依赖的步骤同时执行（受 `MaxConcurrentSteps` 限制）
- **失败隔离**：单个步骤失败不影响无依赖的其他步骤

---

## 3. 使用场景

### 3.1 适合 Work 模式的任务

✅ **多步骤流程任务**
- 代码分析 → 重构建议 → 生成报告
- 数据收集 → 清洗 → 分析 → 可视化

✅ **需要不同专业能力**
- UI设计师 + 前端开发 + 后端开发
- 数据收集员 + 数据分析师 + 报告撰写员

✅ **有明确依赖关系**
- 先扫描代码，再分析性能
- 先收集需求，再设计方案，再实现代码

✅ **可并发执行的任务**
- 同时分析多个模块
- 并行处理多个数据源

### 3.2 不适合 Work 模式的任务

❌ **简单单步任务**
- "写一个Hello World程序"
- "翻译这段文字"
→ 使用 **Expert 模式**

❌ **需要持续讨论的任务**
- 多人头脑风暴
- 反复讨论方案细节
→ 使用 **Meeting 模式**

❌ **高度动态的任务**
- 无法提前规划步骤
- 每步结果完全不可预测
→ 使用 **Expert 模式**

---

## 4. 策略配置

### 4.1 WorkflowPolicy

控制 Work 模式的资源限制和行为：

| 参数 | 默认值 | 硬上限 | 说明 |
|------|--------|--------|------|
| `MaxAgentDepth` | 3 | 10 | 子智能体嵌套深度，防止无限递归 |
| `MaxStepsPerRun` | 50 | 500 | 单次 Run 内最大步骤数（一级步骤） |
| `MaxSubAgentJobs` | 20 | — | 同时存在的子智能体任务数 |
| `MaxConcurrentSteps` | 4 | — | 并发执行的步骤数 |
| `StepTimeoutSeconds` | 300 | — | 单步骤超时（秒） |
| `MaxRetriesPerStep` | 2 | — | 每步骤最大重试次数 |
| `RetryBackoffMilliseconds` | 1000 | — | 指数退避的基础间隔（毫秒） |
| `RequirePlanConfirmation` | `false` | — | 计划生成后是否需要用户确认才执行 |

**配置示例：**

```csharp
var options = new WorkModeOptions
{
    WorkflowPolicy = new WorkflowPolicy
    {
        MaxStepsPerRun = 20,           // 最多20个一级步骤
        MaxConcurrentSteps = 8,        // 最多8个步骤并发
        StepTimeoutSeconds = 600,      // 单步骤10分钟超时
        MaxRetriesPerStep = 3,         // 失败最多重试3次
        RequirePlanConfirmation = true // 需要用户确认计划
    }
};
```

### 4.2 失败策略

`WorkStepFailurePolicy` 定义步骤失败后的行为：

| 策略 | 说明 | 使用场景 |
|------|------|----------|
| `Stop` | 停止整个 Plan，标记为 Failed | 关键步骤失败必须终止 |
| `Continue` | 跳过失败步骤，继续执行后续步骤 | 非关键步骤，允许部分失败 |
| `Retry` | 按 `MaxRetriesPerStep` 重试 | 临时性错误（网络、API限流） |
| `AskHost` | 暂停并请求宿主决策 | 需要人工判断的失败 |

**默认策略：`Retry`**

### 4.3 循环检测

`WorkLoopDetection` 控制 DAG 中循环引用的检测：

| 策略 | 说明 |
|------|------|
| `Strict` | 严格检测，任何疑似循环均阻止执行 |
| `Lenient` | 宽松检测，仅在明确循环时阻止 |
| `Disabled` | 关闭循环检测 |

**默认：`Strict` + `RequireHumanConfirmationOnLoop = true`**

---

## 5. 步骤生命周期

### 5.1 状态流转

```
Pending ─→ Running ─→ Completed
  ↓          ↓
Skipped   Failed ─┬─ Retry（未达上限）───→ Running（自动重试）
                  ├─ AskHost ─→ WaitingForApproval ─→ Running
                  └─ Stop / 重试耗尽 ─→ Failed（终止）

Running ─→ WaitingForCredentials ─→ Running（凭据更新后）
  ↓
Interrupted
```

| 状态 | 说明 |
|------|------|
| `Pending` | 等待调度，依赖的步骤尚未完成 |
| `Running` | 正在执行 |
| `Completed` | 成功完成 |
| `Failed` | 执行失败且未重试或重试耗尽 |
| `Skipped` | 因前置步骤失败或策略决定被跳过 |
| `Interrupted` | 被外部中断 |
| `WaitingForApproval` | 等待人工审批 |
| `WaitingForCredentials` | 等待凭据更新 |

### 5.2 监听步骤状态

**CLI 方式：**

```bash
# 实时查看步骤状态
madorin run --mode work --input "任务" --format jsonl
```

输出事件流：

```jsonl
{"messageType":"work.plan.updated","payload":{...}}
{"messageType":"work.step.started","payload":{"stepId":"1.1",...}}
{"messageType":"work.step.completed","payload":{"stepId":"1.1",...}}
{"messageType":"work.step.summary","payload":{"stepId":"1",...}}
```

**SDK 方式：**

```csharp
await foreach (var evt in client.ReadEventsAsync(0))
{
    switch (evt.MessageType)
    {
        case "work.plan.updated":
            var plan = evt.Payload.Deserialize<WorkPlanUpdatedEvent>();
            Console.WriteLine($"计划已更新：{plan.Goal}");
            break;
        
        case "work.step.started":
            var started = evt.Payload.Deserialize<WorkStepStartedEvent>();
            Console.WriteLine($"步骤开始：{started.StepId}");
            break;
        
        case "work.step.completed":
            var completed = evt.Payload.Deserialize<WorkStepCompletedEvent>();
            Console.WriteLine($"步骤完成：{completed.StepId}");
            break;
        
        case "work.step.summary":
            var summary = evt.Payload.Deserialize<WorkStepSummaryEvent>();
            Console.WriteLine($"步骤汇总：{summary.StepId}");
            break;
        
        case "run.completed":
            Console.WriteLine("任务完成");
            break;
    }
}
```

---

## 6. 计划确认与修订

### 6.1 计划确认门

当 `RequirePlanConfirmation = true` 时，总经理生成计划后会等待用户确认：

**CLI 交互：**

```
> 分析项目代码并生成优化建议报告

[总经理] 我制定了以下执行计划：

步骤1：代码扫描
  1.1 扫描Java代码
  1.2 扫描Python代码

步骤2：性能分析（依赖步骤1）
  2.1 分析性能热点

步骤3：报告生成（依赖步骤2）
  3.1 生成优化建议报告

是否确认执行此计划？（输入"确认"开始执行，或提出修改意见）

> 确认

[项目组长] 开始执行计划...
```

**明确确认语句：**
- "确认"、"确认执行"、"批准"
- "开始"、"执行"、"go"

**修订计划：**
- "步骤2改为只分析Java代码"
- "增加一个步骤：代码格式化"

### 6.2 运行中插话

Work 模式支持在步骤执行间隙插话修改计划：

```
> 分析项目代码

[项目组长] 步骤1.1完成：已扫描Java代码
[项目组长] 步骤1.2开始：扫描Python代码...

> 取消Python代码扫描，直接进入性能分析

[项目组长] 收到插话，暂停当前计划
[总经理] 已更新计划，取消步骤1.2
[项目组长] 继续执行...
```

**插话规则：**
- 插话在步骤间隙生效（不会中断正在执行的步骤）
- 可以修改未执行的步骤
- 可以添加新步骤或取消步骤

---

## 7. 结果汇总

### 7.1 三级汇总机制

Work 模式自动生成三级汇总：

1. **二级步骤结果**：每个二级步骤完成后，Worker Agent 输出结果
2. **一级步骤汇总**：项目组长汇总所有二级步骤结果
3. **最终报告**：总经理汇总所有一级步骤，生成最终报告

**示例：**

```
[步骤1.1完成] 扫描Java代码：发现327个类，12个性能问题
[步骤1.2完成] 扫描Python代码：发现89个模块，5个性能问题

[步骤1汇总] 代码扫描完成：
  - Java: 327类，12个性能问题
  - Python: 89模块，5个性能问题
  - 总计: 17个性能问题待优化

[步骤2.1完成] 性能分析：识别出3个关键瓶颈

[步骤2汇总] 性能分析完成：
  - 关键瓶颈: 数据库查询(2个)、算法复杂度(1个)
  - 优化建议: 见详细报告

[最终报告] 项目代码分析报告：
  本次分析扫描了416个代码文件，发现17个性能问题...
  关键优化建议：...
```

### 7.2 汇总事件

**SDK 监听汇总：**

```csharp
await foreach (var evt in client.ReadEventsAsync(0))
{
    if (evt.MessageType == "work.step.summary")
    {
        var summary = evt.Payload.Deserialize<WorkStepSummaryEvent>();
        Console.WriteLine($"===== 步骤{summary.StepId}汇总 =====");
        Console.WriteLine(summary.SummaryText);
    }
    else if (evt.MessageType == "output.delta")
    {
        // 最终报告以文本增量输出
        var delta = evt.Payload.Deserialize<TextDeltaEvent>();
        Console.Write(delta.Text);
    }
}
```

---

## 8. 错误处理

### 8.1 步骤失败

**自动重试（默认）：**

```
[步骤1.1执行] 扫描Java代码...
[步骤1.1失败] 网络超时
[步骤1.1重试1/2] 重新扫描Java代码...
[步骤1.1失败] 网络超时
[步骤1.1重试2/2] 重新扫描Java代码...
[步骤1.1完成] 扫描成功
```

**重试耗尽：**

```
[步骤1.1失败] 重试耗尽，步骤标记为失败
[整体计划] 因关键步骤失败而终止
```

### 8.2 依赖步骤失败

```
步骤1 ─→ 步骤2 ─→ 步骤3
```

如果步骤1失败：
- 步骤2自动标记为 `Skipped`
- 步骤3自动标记为 `Skipped`
- 整个计划标记为 `Failed`

**Continue 策略下：**

```
步骤1（失败）
步骤2（依赖步骤1，Skipped）
步骤3（无依赖）─→ 继续执行
```

### 8.3 凭据失效

```
[步骤1.1执行] 调用OpenAI API...
[步骤1.1暂停] API Key 失效，等待凭据更新

> madorin config edit  # 用户更新API Key

[步骤1.1继续] 凭据已更新，继续执行
[步骤1.1完成] 扫描成功
```

---

## 9. 最佳实践

### 9.1 任务分解

**✅ 好的分解：**

```
任务：生成项目文档
└─ 步骤1：收集信息
   ├─ 1.1 扫描代码结构
   └─ 1.2 提取API文档
└─ 步骤2：生成文档（依赖步骤1）
   ├─ 2.1 生成架构图
   ├─ 2.2 生成API参考
   └─ 2.3 生成用户手册
```

**❌ 不好的分解：**

```
任务：写一个Hello World
└─ 步骤1：打开编辑器
└─ 步骤2：输入代码
└─ 步骤3：保存文件
```
→ 过度分解，使用 Expert 模式更合适

### 9.2 设置合理的并发

```csharp
// 计算密集型任务：降低并发
MaxConcurrentSteps = 2

// I/O密集型任务：提高并发
MaxConcurrentSteps = 8

// 大量小任务：更高并发
MaxConcurrentSteps = 16
```

### 9.3 失败策略选择

```csharp
// 关键步骤：必须成功
FailurePolicy = WorkStepFailurePolicy.Stop

// 数据收集：部分失败可接受
FailurePolicy = WorkStepFailurePolicy.Continue

// 网络请求：临时失败重试
FailurePolicy = WorkStepFailurePolicy.Retry

// 需要人工判断：请求审批
FailurePolicy = WorkStepFailurePolicy.AskHost
```

### 9.4 超时设置

```csharp
// 快速任务
StepTimeoutSeconds = 60  // 1分钟

// 常规任务
StepTimeoutSeconds = 300  // 5分钟（默认）

// 长时间任务（如大文件处理）
StepTimeoutSeconds = 1800  // 30分钟
```

---

## 10. 高级用法

### 10.1 步骤复用

复用已完成的步骤，避免重复执行：

```json
{
  "steps": [
    {
      "stepId": "1",
      "reusesStepId": "previous-step-id",
      "title": "复用之前的扫描结果"
    }
  ]
}
```

### 10.2 步骤替换

替换计划中的步骤：

```json
{
  "steps": [
    {
      "stepId": "1",
      "replacesStepId": "old-step-id",
      "title": "新的实现方案"
    }
  ]
}
```

### 10.3 后台任务

标记步骤为后台任务，不阻塞后续步骤：

```json
{
  "steps": [
    {
      "stepId": "1",
      "isBackground": true,
      "title": "后台数据同步"
    }
  ]
}
```

### 10.4 Agent 技能过滤

为不同 Agent 绑定不同的技能集：

```csharp
var agents = new[]
{
    new AgentConfig
    {
        AgentId = "code-analyzer",
        SkillIds = new[] { "code-scan", "static-analysis" }
    },
    new AgentConfig
    {
        AgentId = "report-writer",
        SkillIds = new[] { "markdown-gen", "chart-gen" }
    }
};
```

---

## 11. 故障排查

### 11.1 计划无法生成

**症状：** 总经理输出文本，但没有生成可执行计划

**原因：**
- 任务描述不清晰
- 任务过于简单（不需要多步骤）
- 总经理模型能力不足

**解决：**
- 提供更详细的任务描述
- 使用更强的模型（如 GPT-4、Claude Opus）
- 明确要求："请制定详细的执行计划"

### 11.2 步骤一直 Pending

**症状：** 步骤状态停留在 `Pending`，不执行

**原因：**
- 依赖的步骤尚未完成
- 达到 `MaxConcurrentSteps` 限制
- 循环依赖检测阻止

**解决：**
- 检查依赖关系是否正确
- 提高 `MaxConcurrentSteps`
- 检查是否存在循环依赖

### 11.3 步骤频繁超时

**症状：** 步骤经常触发 `StepTimeoutSeconds`

**原因：**
- 超时设置过短
- 步骤任务过重
- 网络或API响应慢

**解决：**
- 增加 `StepTimeoutSeconds`
- 将大步骤拆分为小步骤
- 检查网络和API状态

### 11.4 重试耗尽

**症状：** 步骤反复失败，重试耗尽

**原因：**
- 持续性错误（如配置错误）
- API限流
- 资源不足

**解决：**
- 检查错误日志，修复根本问题
- 降低并发减少API压力
- 增加 `RetryBackoffMilliseconds` 延长重试间隔

---

## 12. 性能优化

### 12.1 并发优化

```csharp
// 根据任务类型调整并发
var policy = new WorkflowPolicy();

if (isIOIntensive)
{
    policy.MaxConcurrentSteps = 16;  // I/O密集型，高并发
}
else if (isCPUIntensive)
{
    policy.MaxConcurrentSteps = Environment.ProcessorCount;  // CPU密集型，按核心数
}
```

### 12.2 Token 优化

```csharp
var contextPolicy = new WorkContextPolicy
{
    PreservePlan = true,           // 保留计划（必须）
    PreserveCurrentStep = true,    // 保留当前步骤（必须）
    PreserveToolResults = true,    // 保留工具结果（推荐）
    PreserveReasoning = false,     // 不保留推理过程（节省Token）
    MaxTokens = 50000,             // 限制上下文大小
    TailMessageCount = 20          // 只保留最近20条消息
};
```

### 12.3 步骤粒度

**过粗：** 单个步骤耗时过长，失败代价大

```
步骤1：完成整个项目  // ❌ 太粗
```

**过细：** 调度开销大，总经理难以规划

```
步骤1：打开文件
步骤2：读取第一行
步骤3：处理第一行
...  // ❌ 太细
```

**合适：** 每个步骤1-5分钟，有明确的交付物

```
步骤1：扫描所有代码文件  // ✅ 合适
步骤2：分析性能问题      // ✅ 合适
步骤3：生成优化报告      // ✅ 合适
```

---

## 13. 与其他模式对比

| 特性 | Expert 模式 | Meeting 模式 | Work 模式 |
|------|------------|-------------|-----------|
| 任务复杂度 | 简单-中等 | 讨论-决策 | 多步骤-复杂 |
| Agent 数量 | 1个 | 多个（角色扮演） | 多个（专业分工） |
| 执行方式 | 单智能体 | 主持人调度发言 | DAG调度并发执行 |
| 适用场景 | 写代码、翻译、问答 | 头脑风暴、方案讨论 | 项目分析、报告生成 |
| 中断恢复 | 不支持 | 支持暂停/续跑 | 支持步骤重试 |
| 人工介入 | 不支持 | 支持插话 | 支持计划确认/修订 |

**选择建议：**

- **任务可以一次性完成** → Expert 模式
- **需要多角色讨论** → Meeting 模式
- **需要分步骤执行，有依赖关系** → Work 模式

---

## 14. 完整示例

### 14.1 代码分析项目

```bash
madorin run --mode work --input "分析项目代码并生成优化建议报告"
```

**执行流程：**

1. **总经理制定计划：**
   - 步骤1：代码扫描（1.1 Java、1.2 Python）
   - 步骤2：性能分析（2.1 识别瓶颈）
   - 步骤3：报告生成（3.1 生成报告）

2. **项目组长调度：**
   - 并发执行 1.1 和 1.2
   - 1.1、1.2 完成后，生成步骤1汇总
   - 执行 2.1
   - 2.1 完成后，生成步骤2汇总
   - 执行 3.1
   - 3.1 完成后，生成步骤3汇总

3. **总经理生成最终报告：**
   - 汇总所有步骤结果
   - 输出完整的优化建议报告

### 14.2 SDK 完整示例

```csharp
public async Task RunWorkModeAsync()
{
    // 1. 连接 Runtime
    var client = await RuntimeClient.ConnectAsync(new RuntimeClientOptions
    {
        InstanceName = "main"
    });

    // 2. 配置 Work 模式
    var request = new NewSessionRunRequest(
        SessionIdempotencyKey: Guid.NewGuid().ToString(),
        RunIdempotencyKey: Guid.NewGuid().ToString(),
        Mode: RuntimeMode.Work,
        Selection: new NextTurnSelection
        {
            ProviderId = "my-openai",
            Model = "gpt-4o"
        },
        InitialInput: new[]
        {
            ContentBlock.CreateText("分析项目代码并生成优化建议报告")
        },
        ModeOptions: new WorkModeOptions
        {
            WorkflowPolicy = new WorkflowPolicy
            {
                MaxStepsPerRun = 20,
                MaxConcurrentSteps = 4,
                RequirePlanConfirmation = true
            }
        }
    );

    // 3. 启动任务
    var runId = await client.StartNewSessionRunAsync(request);

    // 4. 监听执行过程
    await foreach (var evt in client.ReadEventsAsync(0))
    {
        if (evt.RunId != runId) continue;

        switch (evt.MessageType)
        {
            case "work.plan.updated":
                var plan = evt.Payload.Deserialize<WorkPlanUpdatedEvent>();
                Console.WriteLine($"[计划] {plan.Goal}");
                foreach (var step in plan.Steps)
                {
                    Console.WriteLine($"  步骤{step.StepId}: {step.Title}");
                }
                break;

            case "work.step.started":
                var started = evt.Payload.Deserialize<WorkStepStartedEvent>();
                Console.WriteLine($"[开始] 步骤{started.StepId}");
                break;

            case "work.step.completed":
                var completed = evt.Payload.Deserialize<WorkStepCompletedEvent>();
                Console.WriteLine($"[完成] 步骤{completed.StepId}");
                break;

            case "work.step.summary":
                var summary = evt.Payload.Deserialize<WorkStepSummaryEvent>();
                Console.WriteLine($"[汇总] 步骤{summary.StepId}");
                Console.WriteLine(summary.SummaryText);
                break;

            case "output.delta":
                var delta = evt.Payload.Deserialize<TextDeltaEvent>();
                Console.Write(delta.Text);
                break;

            case "run.completed":
                Console.WriteLine("\n[完成] 任务执行完毕");
                return;

            case "run.failed":
                var failed = evt.Payload.Deserialize<RunFailedEvent>();
                Console.WriteLine($"\n[失败] {failed.Message}");
                return;
        }
    }
}
```

---

## 15. 下一步

- **了解协议细节**：阅读 [协议规范](./04-协议规范/README.md)
- **查看开发文档**：阅读 [Work 模式契约](../开发文档/17-Work模式契约.md)
- **完整 API 参考**：查看 [IRuntimeClient 接口](./06-API参考/01-IRuntimeClient接口.md)
- **Meeting 模式对比**：阅读 [Meeting 模式使用指南](./08-Meeting模式使用指南.md)

---

**修订记录：**

| 日期 | 说明 |
|------|------|
| 2026-10-03 | 初始版本，基于重构后的 Work 模式架构 |
