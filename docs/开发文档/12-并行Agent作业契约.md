# 并行 Agent 作业契约（无定时）

> CatalogVersion：`3.2.0`  
> 范围：**并行入队 + 结果回收**。不含 cron/delay。  
> V1 约束：实际子 Agent 调用**串行化**（保护共享 Provider/Gateway/SessionStore）；create 仍立即返回。

## 1. 目标

| 能力 | 支持 |
| --- | --- |
| 多子 Agent 入队 | ✅ `create` / `create_many` |
| 立即返回 jobId | ✅ |
| 状态查询 | ✅ `status` |
| **结果回收** | ✅ `result` / `wait` |
| 取消 | ✅ `cancel`（`cancelling` → `cancelled`） |
| 超时 | ✅ job `timeoutMs` + wait `timeoutMs` |
| 单 Run 作业上限 | ✅ 默认 64 |
| 定时调度 | ❌ |

## 2. 工具

| Tool ID | 作用 |
| --- | --- |
| `mcli_agent_job_create` | 创建 1 个作业 |
| `mcli_agent_job_create_many` | 批量创建（先全量校验再入队） |
| `mcli_agent_job_status` | 查状态 |
| `mcli_agent_job_result` | 回收；`jobId` 或 `jobIds` |
| `mcli_agent_job_wait` | 等待终态后回收；**未知 jobId 立即失败** |
| `mcli_agent_job_cancel` | 请求取消 |

同步短路径：`mcli_delegate_agent`。

## 3. 状态机

```text
pending → running → completed
    │         │  ↘ failed
    │         │  ↘ timed_out
    └─────────┴→ cancelling → cancelled
```

- **终态**：`completed` / `failed` / `cancelled` / `timed_out`
- **`cancelling`**：已请求取消，Worker 未停完；`result` 此时 `ready=false`
- **`cancel`**：best-effort；副作用可能仍在收尾，回收以终态 `cancelled` 为准

## 4. 回收约定

### `result`

| 情况 | `ready` | `success`（JSON 与 ToolResult 一致） |
| --- | --- | --- |
| 未终态 / cancelling | false | false |
| `completed` | true | true |
| 其它终态 | true | false |
| 批量：全部 completed | — | true；否则 false |

### `wait`

| 字段 | 含义 |
| --- | --- |
| `allTerminal` | 全部终态 |
| `allSucceeded` | 全部 `completed` |
| `timedOut` | 等待超时 |
| `success` / ToolResult.Success | **`allTerminal && allSucceeded`** |

未知 `jobId`：**不空等**，立即 `success=false` + 错误信息。

## 5. 限制

| 项 | 默认 |
| --- | --- |
| 并发池（排队进入 running） | 8 |
| Worker 实际调用 | **串行 1**（共享依赖安全） |
| 单 Run 非终态作业上限 | 64 |
| Job 默认超时 | 300_000 ms |
| Wait 默认超时 | 300_000 ms |
| Dispose 排空等待 | 30_000 ms |

`create_many`：任一请求非法 → **全部不入队**。容量不足 → 抛错且不入队。

## 6. 推荐流程

```text
mcli_agent_job_create_many → jobIds
mcli_agent_job_wait { jobIds, timeoutMs }
  → success=true 时使用 jobs[].result
```

## 7. 生命周期

- 作用域：单个 Runtime Run  
- Run 结束 `DisposeAsync`：对全部 job 请求 cancel，并等待 in-flight 排空（有上限）  
- 无跨进程持久化  

## 8. 测试

`AgentJobCoordinatorTests`：并行回收、未就绪、cancelling、未知 id 快失败、上限、create_many 校验、失败 AllSucceeded、Dispose 排空。
