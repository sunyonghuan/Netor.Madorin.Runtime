# Expert 模式 - 小型计划功能完整示例

演示如何使用内置计划工具进行多步任务。

## 场景

实现一个中等复杂度的功能：为项目添加“用户偏好设置”模块。

## 推荐流程

1. 让 Agent 先创建计划（3-8 步）
2. 逐步执行，每步更新状态
3. 计划会自动注入到后续提示词中
4. 完成后给出最终总结

## 关键工具

- `mcli_expert_create_plan`
- `mcli_expert_execute_step`
- `mcli_expert_get_plan`
- `mcli_expert_abandon_plan`（必要时）

## 示例对话开头

用户：
> 请为我们的应用添加用户偏好设置功能，包括主题、通知偏好、语言三项。使用计划来管理执行过程。

Agent 通常会先调用：
```json
{ "tool": "mcli_expert_create_plan", "arguments": { ... } }
```

之后每完成一个步骤就调用 `execute_step`。

## 恢复

如果中途中断，使用同一个 Session 再次启动：

```bash
madorin run --session <之前的 sessionId>
```

计划会自动恢复。
