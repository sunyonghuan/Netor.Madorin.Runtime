# Expert 模式完整指南

> **适用版本**：Madorin.AI.Runtime 0.1.69+  
> **最后更新**：2026-10-06

Expert 模式是 Madorin 的主力运行模式，专为复杂、需要多步推理、工具调用和子 Agent 协作的任务设计。

## 概述

Expert 模式的核心特点：

- **单 Agent 驱动**：一个主 Agent 负责规划与执行
- **子 Agent 委派**：通过 `mcli_delegate_agent` 将任务交给其他专业 Agent
- **并行作业**：支持 `mcli_agent_job_*` 系列工具实现后台并行执行
- **小型计划**：内置 2-15 步计划功能，适合中等复杂度任务
- **精细控制**：通过 `RequestControl` 精确控制采样参数、结构化输出等

与已移除的 Work/Meeting 模式不同，Expert 模式专注于**轻量、高可控、可委派**的执行风格。

## 基础用法

### CLI 方式

```bash
# 交互式运行（默认 Expert 模式）
madorin run

# 单次执行 + 指定模型
madorin run --provider deepseek --model deepseek-v4-pro \
  --input "帮我分析这个项目的架构并给出改进建议"

# 非流式同步返回（适合脚本）
madorin run --no-stream --input "总结昨天的会议纪要"
```

### 程序化 / SDK 方式

```csharp
var selection = new NextTurnSelection(
    SelectionVersion: 1,
    Mode: RuntimeMode.Expert,
    DefaultSelection: new DefaultSelection("deepseek", "deepseek-v4-pro"),
    ModeOptions: new ExpertModeOptions(
        agent: new AgentRef("architect", "1", systemPrompt: "你是一个资深架构师..."),
        requestControl: new ExpertRequestControl
        {
            Temperature = 0.3,
            MaxTokens = 4000
        }));

var runId = await client.StartNewSessionRunAsync(new NewSessionRunRequest(...));
```

## RequestControl：精细控制

Expert 模式最重要的增强之一是 `ExpertRequestControl`，可以对每一次 Run 进行采样控制。

常用字段：

| 字段                  | 类型     | 说明                              | 典型取值     |
|-----------------------|----------|-----------------------------------|--------------|
| `Temperature`         | double?  | 采样温度                          | 0.0 ~ 2.0    |
| `TopP`                | double?  | 核采样                            | 0.1 ~ 1.0    |
| `MaxTokens`           | int?     | 最大生成长度                      | 1024 ~ 8192  |
| `Seed`                | int?     | 随机种子（确定性生成）            | 任意整数     |
| `StopSequences`       | string[] | 停止序列                          | ["\\n\\n"]   |
| `FrequencyPenalty`    | double?  | 频率惩罚                          | -2.0 ~ 2.0   |
| `PresencePenalty`     | double?  | 存在惩罚                          | -2.0 ~ 2.0   |
| `ResponseFormat`      | string?  | "text" 或 "json"                  | "json"       |
| `Stream`              | bool?    | 是否流式（同步接口会强制 false）  | true/false   |

完整参数说明请参考《请求控制参数详解》。

### 结构化输出示例

```bash
madorin run \
  --response-format json \
  --input "请用 JSON 格式返回当前项目的 5 个核心模块及其职责"
```

Runtime 会把 `ResponseFormat=json` 透传给支持的 Provider（OpenAI、兼容层等）。

## 子 Agent 委派

Expert 模式最强大的能力之一是**子 Agent 委派**。

### 可用工具

- `mcli_delegate_agent`：同步委派任务给指定 Agent，等待返回结果
- `mcli_agent_job_create` / `create_many`：创建后台并行作业
- `mcli_agent_job_wait` / `result` / `status`：回收作业结果

### 基本委派

在 System Prompt 或对话中，主 Agent 可以这样调用：

```json
{
  "tool": "mcli_delegate_agent",
  "arguments": {
    "agentName": "security-reviewer",
    "task": "审查当前登录模块的安全性，给出 3 条高风险点"
  }
}
```

主 Agent 会收到该子 Agent 的完整返回内容，并继续后续步骤。

### 并行作业

适合多个独立子任务同时执行：

```json
{
  "tool": "mcli_agent_job_create_many",
  "arguments": {
    "jobs": [
      { "agentName": "frontend", "task": "设计登录页面交互" },
      { "agentName": "backend",  "task": "设计登录 API 接口" }
    ]
  }
}
```

之后使用 `mcli_agent_job_wait` 等待所有作业完成，再汇总结果。

## 小型计划功能

Expert 模式内置了轻量计划支持，适合 3~15 步的中等复杂度任务。

### 何时使用

- 需要明确步骤拆解的任务
- 希望在执行过程中记录进度
- 需要在中断后恢复执行

### 核心工具

| 工具                        | 作用                     |
|-----------------------------|--------------------------|
| `mcli_expert_create_plan`   | 创建一个 2-15 步计划     |
| `mcli_expert_execute_step`  | 执行并标记某个步骤       |
| `mcli_expert_get_plan`      | 查询当前活跃计划         |
| `mcli_expert_update_step_status` | 手动更新步骤状态    |
| `mcli_expert_abandon_plan`  | 放弃当前计划             |

计划会在每次模型调用前自动注入到 System Prompt 中，主 Agent 能清晰看到当前进度和下一步建议。

详细用法请阅读《小型计划功能使用指南》。

## 典型工作流示例

### 场景：完成一个中等复杂度的功能开发

1. 主 Agent 收到需求后，调用 `create_plan` 创建 6 步计划。
2. 依次执行步骤：
   - Step 1：调研现有代码 → 使用 `delegate_agent` 给 "code-researcher"
   - Step 2：设计接口 → 自己思考或委派
   - Step 3：实现后端 → 并行创建 2 个 job
   - Step 4：实现前端
   - Step 5：编写测试
   - Step 6：集成与自测
3. 每完成一步调用 `execute_step` 更新状态。
4. 全部完成后，主 Agent 输出最终交付物。

如果中途中断，下次启动相同 Session 时，计划会自动恢复。

## 最佳实践

1. **合理使用温度**
   - 创意/探索类任务：0.7 ~ 1.1
   - 规划/架构/代码审查：0.1 ~ 0.4
   - 需要稳定输出结构时：0.0 ~ 0.2 + Seed

2. **善用子 Agent**
   - 把「研究型」任务交给专门的 researcher Agent
   - 把「安全/性能」评审交给 reviewer Agent
   - 主 Agent 更应该做「协调 + 决策」

3. **并行 vs 串行**
   - 任务之间无依赖 → 用 job 并行
   - 后面步骤依赖前面结果 → 用 delegate 同步等待

4. **计划的适用边界**
   - 2~15 步：推荐使用内置计划
   - 更复杂、需要长期追踪的任务：考虑让主 Agent 自己维护 TODO 列表或外部状态

5. **非流式使用场景**
   - CI/CD、脚本自动化、需要拿到完整结果再处理
   - 推荐配合 `--no-stream` 或直接调用同步 HTTP 接口

## 常见问题

**Q: Expert 模式和以前的 Work 模式有什么区别？**  
A: Work 模式已被移除。Expert 更轻量，强调委派与并行，而不是长时间后台工作流。

**Q: 计划功能会持久化吗？**  
A: 会。计划以文件形式保存在工作区 `.madorin/plans/` 下，按会话隔离。只有 Pending/Running 状态的计划才会被恢复。

**Q: 如何让模型严格按照 JSON 返回？**  
A: 使用 `RequestControl.ResponseFormat = "json"`（部分 Provider 还支持传入完整 JSON Schema）。

**Q: 并行作业的超时怎么控制？**  
A: 在创建 job 时可传 `timeoutMs`，或在 RequestControl 中设置整体超时。

---

继续阅读：
- [请求控制参数详解](./请求控制参数详解.md)
- [小型计划功能使用指南](./小型计划功能使用指南.md)
- [示例代码](../examples/)
