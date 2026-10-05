# Meeting 模式文档备档

> ⚠️ **已废弃**：Meeting 模式在 v0.2.0 中被移除。

## 移除原因

- Runtime 重新定位为「轻量级 AI 访问协调器」
- Meeting 模式的多参与者轮询应由上层框架实现
- Expert 模式的子 Agent 委派 + 并行作业提供替代方案

## 替代方案

- 使用 **Expert 模式 + AvailableAgents**（多个子 Agent）
- 使用 **Expert 模式 + 并行作业**（`mcli_agent_job_*` 工具）
- 使用 **Expert 模式 + @agentName 委派语法**
- 或在上层框架基于 Expert 模式实现自定义轮询逻辑

## 迁移指南

参考：`../迁移指南/从Meeting模式迁移到Expert.md`（即将发布）

---

## 本目录包含的文档

- `16-Meeting模式契约.md` - Meeting 模式的协议与契约定义

---

此目录保留历史文档供参考，不再维护。

**废弃时间**：2025-10-05  
**最后维护版本**：v0.1.x  
**替代方案**：Expert 模式全面增强（参考 `../../策划/Expert模式全面增强与轻量化改造方案.md`）
