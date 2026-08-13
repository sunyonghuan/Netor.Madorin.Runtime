# `state.db` 数据库参考

> 适用版本：Madorin.AI.Runtime V1，协议 1.1，SQLite Schema 16
>
> 本文以 `Madorin.AI.Runtime.Persistence.Sqlite/SqliteSchema.cs` 的最终迁移结果为准，描述工作区状态库的全部表、字段、约束和索引。实现或后续迁移与本文冲突时，以代码中的 Schema 迁移为准。

## 1. 范围与边界

`state.db` 是工作区 Runtime 状态库，默认路径为 `<workspace>/.madorin/state.db`。它保存会话、运行、事件、工具授权、Meeting、Work 和消息索引等元数据；消息正文的权威副本不在该库中，而在 `messages/<sessionId>.jsonl`。

不要绕过 Runtime 或 `madorin db` 命令直接修改本库。运行时会同时维护 `state.db-wal` 和 `state.db-shm`；活动库的备份必须使用 `madorin db backup`，不能只复制主数据库文件。

### 1.1 SQLite 连接设置

| 设置 | 值 | 作用 |
| --- | --- | --- |
| `journal_mode` | `WAL` | 使用预写日志，支持读写并发与在线备份 |
| `foreign_keys` | `ON` | 启用声明的外键约束 |
| `busy_timeout` | `5000` 毫秒 | 锁冲突时最多等待 5 秒 |
| `synchronous` | `NORMAL` | WAL 模式下的正常同步级别 |

### 1.2 字段说明约定

| 标记 | 含义 |
| --- | --- |
| `PK` | 主键 |
| `FK` | 外键；未标注时即使字段名带有 `_id`，也不表示数据库层面定义了外键 |
| `NN` | `NOT NULL` |
| `UQ` | 唯一约束或唯一索引 |
| `NULL` | 可为空 |
| `TEXT` | 普通字符串、时间文本或 JSON 文本；具体格式由上层契约约束 |
| `INTEGER` | 整数；布尔语义使用 `0` / `1` |
| `BLOB` | 二进制序列化数据 |

除 `schema_versions.applied_at` 由迁移 SQL 使用 UTC `strftime(...)` 写入外，其他时间列的格式由 Runtime 持久化层统一写入。本文中的“JSON”表示存储于 `TEXT` 列的 JSON 文本，并不代表 SQLite 内置 JSON 类型。

## 2. 全库总览

Schema 16 共包含 25 张业务表：

| 分类 | 表 |
| --- | --- |
| 版本与会话运行 | `schema_versions`、`sessions`、`runs`、`run_idempotency`、`session_idempotency`、`event_outbox`、`gsn_sequence` |
| 消息与选择快照 | `message_index`、`session_selections`、`agent_snapshots`、`invocation_snapshots` |
| 工具授权与审计 | `tool_intents`、`tool_grants`、`tool_approvals`、`tool_audit` |
| Meeting 模式 | `meeting_sessions`、`meeting_rounds`、`meeting_participants`、`meeting_invocations` |
| Work 模式 | `work_sessions`、`work_steps`、`work_step_dependencies`、`work_step_attempts`、`work_background_jobs`、`work_plan_revisions` |

## 3. 版本、会话与运行

### 3.1 `schema_versions`

每成功完成一个迁移版本，记录一行。当前 Runtime 仅支持向前迁移到版本 `16`，不支持降级。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `version` | `INTEGER` | PK | 已成功应用的 Schema 版本号 |
| `applied_at` | `TEXT` | NN | 迁移应用时间；迁移 SQL 以 UTC 写入 |

### 3.2 `sessions`

一行对应一个用户可见的连续会话。`mode` 标识 Session 创建时确定的运行模式。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `session_id` | `TEXT` | PK | Session 标识 |
| `mode` | `TEXT` | NN | 运行模式，如 `expert`、`meeting`、`work` |
| `status` | `TEXT` | NN | Session 当前状态 |
| `created_at` | `TEXT` | NN | 创建时间 |
| `updated_at` | `TEXT` | NN | 最近更新时间 |
| `title` | `TEXT` | NULL | Session 标题；Schema 14 新增 |

索引：

| 索引 | 列 | 用途 |
| --- | --- | --- |
| `idx_sessions_updated` | `(updated_at, session_id)` | 按最近更新时间稳定分页 |
| `idx_sessions_status` | `(status)` | 按状态筛选 |

### 3.3 `runs`

一行对应 Session 内的一次执行（Turn）。`run_sequence` 是该 Run 的业务序号；事件的连续游标另由 `event_outbox.gsn` 管理。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `run_id` | `TEXT` | PK | Run 标识 |
| `session_id` | `TEXT` | NN，FK -> `sessions.session_id` | 所属 Session |
| `status` | `TEXT` | NN | Run 当前状态 |
| `run_sequence` | `INTEGER` | NN | Session 内 Run 序号 |
| `created_at` | `TEXT` | NN | 创建时间 |
| `updated_at` | `TEXT` | NN | 最近更新时间 |
| `terminal_text` | `TEXT` | NULL | 终态文本；Schema 6 新增 |
| `phase` | `TEXT` | NULL | 当前执行阶段；Schema 16 新增 |
| `current_step` | `TEXT` | NULL | 当前步骤描述或标识；Schema 16 新增 |
| `completed_units` | `INTEGER` | NULL | 已完成工作单元数；Schema 16 新增 |
| `total_units` | `INTEGER` | NULL | 工作单元总数；Schema 16 新增 |
| `last_run_sequence` | `INTEGER` | NN，默认 `0` | 最后已持久化的 Run 事件序号；Schema 16 新增 |
| `error_json` | `TEXT` | NULL | 结构化错误信息；Schema 16 新增 |

索引：

| 索引 | 列 | 用途 |
| --- | --- | --- |
| `idx_runs_session` | `(session_id)` | 查询某 Session 的 Run |
| `idx_runs_status` | `(status)` | 按状态检索 Run |
| `idx_runs_progress` | `(session_id, status, updated_at, run_id)` | 查询会话内执行进度与恢复候选 |

### 3.4 `run_idempotency`

记录创建或提交 Run 时的幂等键，防止同一请求重复创建多个 Run。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `key` | `TEXT` | PK | 幂等键 |
| `run_id` | `TEXT` | NN | 已关联的 Run 标识 |
| `session_id` | `TEXT` | NN | 请求所属 Session 标识 |
| `expires_at` | `TEXT` | NN | 幂等记录失效时间 |
| `request_hash` | `TEXT` | NULL | 请求内容哈希；Schema 5 新增 |

### 3.5 `session_idempotency`

记录创建 Session 时的幂等键，防止客户端重试产生重复会话。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `key` | `TEXT` | PK | 幂等键 |
| `session_id` | `TEXT` | NN | 已关联的 Session 标识 |
| `expires_at` | `TEXT` | NN | 幂等记录失效时间 |
| `request_hash` | `TEXT` | NULL | 请求内容哈希；Schema 5 新增 |

### 3.6 `event_outbox`

持久化待投递或已确认的 Runtime 事件，用于 GSN 确认、断线重放和按事件状态筛选。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `id` | `INTEGER` | PK，自动递增 | 表内物理行标识 |
| `gsn` | `INTEGER` | NN，UQ | Runtime 实例内全局序列号 |
| `run_id` | `TEXT` | NN | 事件所属 Run |
| `run_sequence` | `INTEGER` | NN | Run 内事件序号 |
| `message_type` | `TEXT` | NN | 事件消息类型 |
| `payload` | `TEXT` | NN | 事件负载 JSON 文本 |
| `status` | `TEXT` | NN，默认 `'pending'` | 出站事件状态 |
| `created_at` | `TEXT` | NN | 事件写入时间 |
| `acked_at` | `TEXT` | NULL | 被客户端确认的时间 |

索引：

| 索引 | 列 | 用途 |
| --- | --- | --- |
| `idx_outbox_gsn` | `(gsn)` | 从 GSN 游标顺序重放 |
| `idx_outbox_status` | `(status)` | 查询待处理或已确认事件 |

### 3.7 `gsn_sequence`

全局序列号计数器。初始化时固定插入 `id = 1, next_gsn = 1`，并通过 `CHECK(id = 1)` 保证只存在一个计数器行。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `id` | `INTEGER` | PK，`CHECK(id = 1)` | 固定单例行标识 |
| `next_gsn` | `INTEGER` | NN，默认 `1` | 下一个待分配的 GSN |

## 4. 消息索引、选择与调用快照

### 4.1 `message_index`

消息正文保存于 JSONL 文件；本表只保存其可定位索引，用于校验消息顺序并按需定位文件记录。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `message_id` | `TEXT` | PK | 消息标识 |
| `session_id` | `TEXT` | NN | 所属 Session 标识 |
| `sequence` | `INTEGER` | NN | Session 内消息顺序号 |
| `invocation_id` | `TEXT` | NN | 产生消息的调用标识 |
| `agent_id` | `TEXT` | NN | 消息关联的 Agent 标识 |
| `role` | `TEXT` | NN | 消息角色 |
| `file_offset` | `INTEGER` | NN | JSONL 记录在文件内的起始字节偏移 |
| `record_length` | `INTEGER` | NN | JSONL 记录的字节长度 |
| `created_at` | `TEXT` | NN | 消息创建时间 |

索引：

| 索引 | 列 | 用途 |
| --- | --- | --- |
| `idx_message_index_session_sequence` | `(session_id, sequence)`，唯一 | 保证会话消息序号唯一并按顺序定位 |

### 4.2 `session_selections`

保存一个 Session 最新的下一轮选择（Provider、模型、Agent 和参数等）的版本化 JSON 快照。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `session_id` | `TEXT` | PK，FK -> `sessions.session_id`，`ON DELETE CASCADE` | 所属 Session |
| `selection_version` | `INTEGER` | NN | Selection 版本号 |
| `selection_json` | `TEXT` | NN | Selection JSON 快照 |
| `updated_at` | `TEXT` | NN | 最后更新时间 |

### 4.3 `agent_snapshots`

持久化 Session 使用过的 Agent 提示词版本和哈希，用于 Session Rehydrate 时验证当前 Agent 定义是否兼容。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `session_id` | `TEXT` | PK 组成列，FK -> `sessions.session_id`，`ON DELETE CASCADE` | 所属 Session |
| `agent_id` | `TEXT` | PK 组成列 | Agent 标识 |
| `prompt_template_version` | `TEXT` | NN | Prompt 模板版本 |
| `prompt_hash` | `TEXT` | NN | Prompt 内容哈希 |
| `provider_id` | `TEXT` | NULL | 快照对应 Provider 标识 |
| `model_id` | `TEXT` | NULL | 快照对应模型标识 |

主键：`(session_id, agent_id)`。

### 4.4 `invocation_snapshots`

保存一次模型或 Agent 调用开始时的恢复快照，供恢复流程关联 Run 与 Session。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `invocation_id` | `TEXT` | PK | 调用标识 |
| `run_id` | `TEXT` | NN | 所属 Run 标识 |
| `session_id` | `TEXT` | NN | 所属 Session 标识 |
| `snapshot_json` | `TEXT` | NN | 调用快照 JSON |
| `started_at` | `TEXT` | NN | 调用开始时间 |

索引：

| 索引 | 列 | 用途 |
| --- | --- | --- |
| `idx_invocation_snapshots_run` | `(run_id)` | 按 Run 查询调用快照 |
| `idx_invocation_snapshots_session` | `(session_id, started_at)` | 按 Session 和时间重建调用上下文 |

## 5. 工具调用、授权与审计

### 5.1 `tool_intents`

保存 Runtime 向宿主发出的工具调用意图及其执行结果。它描述投递生命周期，与宿主执行器使用的工具执行状态是不同概念。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `call_id` | `TEXT` | PK | 工具调用标识 |
| `invocation_id` | `TEXT` | NULL | 关联模型/Agent 调用标识 |
| `run_id` | `TEXT` | NULL | 所属 Run 标识 |
| `session_id` | `TEXT` | NULL | 所属 Session 标识 |
| `tool_id` | `TEXT` | NULL | 工具目录中的工具标识 |
| `arguments_hash` | `TEXT` | NULL | 工具参数哈希 |
| `status` | `TEXT` | NULL，默认 `'pending'` | 意图投递状态 |
| `result_json` | `TEXT` | NULL | 内联工具结果 JSON |
| `error_message` | `TEXT` | NULL | 错误说明 |
| `created_at` | `TEXT` | NULL | 意图创建时间 |
| `completed_at` | `TEXT` | NULL | 执行完成时间 |
| `agent_id` | `TEXT` | NULL | 发起调用的 Agent；Schema 8 新增 |
| `parent_agent_id` | `TEXT` | NULL | 上级 Agent；Schema 8 新增 |
| `catalog_version` | `TEXT` | NULL | 工具目录版本；Schema 8 新增 |
| `sent_at` | `TEXT` | NULL | 意图发送给宿主的时间；Schema 8 新增 |
| `grant_id` | `TEXT` | NULL | 使用的 Grant 标识；Schema 8 新增 |
| `approval_request_id` | `TEXT` | NULL | 关联的同意请求；Schema 8 新增 |
| `result_hash` | `TEXT` | NULL | 结果内容哈希；Schema 8 新增 |
| `result_blob_id` | `TEXT` | NULL | 大结果对应的 Blob 标识；Schema 8 新增 |
| `result_blob_length` | `INTEGER` | NULL | Blob 长度；Schema 8 新增 |
| `result_blob_sha256` | `TEXT` | NULL | Blob SHA-256；Schema 8 新增 |
| `result_blob_content_type` | `TEXT` | NULL | Blob 内容类型；Schema 8 新增 |
| `result_blob_access_scope` | `TEXT` | NULL | Blob 可访问范围；Schema 8 新增 |
| `result_blob_expires_at` | `TEXT` | NULL | Blob 访问或保留失效时间；Schema 8 新增 |
| `error_code` | `TEXT` | NULL | 机器可判定的错误代码；Schema 8 新增 |
| `result_visible` | `INTEGER` | NN，默认 `1` | 结果是否可对模型可见；Schema 8 新增 |
| `work_step_id` | `TEXT` | NULL | 所属 Work 步骤；Schema 11 新增 |
| `plan_version` | `TEXT` | NULL | 所属 Work 计划版本；Schema 11 新增 |

索引：

| 索引 | 列 | 用途 |
| --- | --- | --- |
| `idx_tool_intents_status` | `(status, sent_at)` | 恢复待发送或已发送意图 |
| `idx_tool_intents_run` | `(run_id, created_at)` | 查询 Run 内工具调用 |
| `idx_tool_intents_work_step` | `(work_step_id, plan_version)` | 关联 Work 步骤和计划版本 |

### 5.2 `tool_grants`

保存一次工具访问授权（Grant）的完整授权边界、委派链和撤销信息。一个子 Grant 可通过 `parent_grant_id` 指向父 Grant。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `grant_id` | `TEXT` | PK | Grant 标识 |
| `run_id` | `TEXT` | NN | Grant 所属 Run |
| `agent_id` | `TEXT` | NULL | 被授权 Agent 标识 |
| `parent_grant_id` | `TEXT` | NULL，FK -> `tool_grants.grant_id` | 父 Grant；根授权为空 |
| `root_grant_id` | `TEXT` | NN | 授权委派链的根 Grant |
| `workspace_root` | `TEXT` | NN | 授权生效的工作区根路径 |
| `allowed_tool_ids` | `TEXT` | NN | 允许调用的工具标识集合 |
| `allowed_call_ids` | `TEXT` | NN | 允许执行的调用标识集合 |
| `maximum_risk` | `INTEGER` | NN | 可接受的最高风险等级 |
| `allowed_read_roots` | `TEXT` | NN | 允许读取的根路径集合 |
| `allowed_write_roots` | `TEXT` | NN | 允许写入的根路径集合 |
| `allow_overwrite` | `INTEGER` | NN | 是否允许覆盖已有文件 |
| `allow_move` | `INTEGER` | NN | 是否允许移动文件 |
| `allow_delete` | `INTEGER` | NN | 是否允许删除文件 |
| `allowed_executables` | `TEXT` | NN | 允许执行的程序集合 |
| `allowed_environment_variables` | `TEXT` | NN | 允许使用或传递的环境变量集合 |
| `allow_powershell` | `INTEGER` | NN | 是否允许 PowerShell |
| `network_policy_json` | `TEXT` | NN | 网络访问策略 JSON |
| `expires_at` | `TEXT` | NN | 授权失效时间 |
| `allow_delegation` | `INTEGER` | NN | 是否允许继续委派 |
| `delegated_agent_ids` | `TEXT` | NN | 允许被委派的 Agent 集合 |
| `delegation_chain` | `TEXT` | NN | 委派链 JSON 或序列化文本 |
| `created_at` | `TEXT` | NN | 创建时间 |
| `revoked_at` | `TEXT` | NULL | 撤销时间 |
| `revocation_reason` | `TEXT` | NULL | 撤销原因 |
| `approval_request_id` | `TEXT` | NULL | 生成该授权的同意请求；兼容补齐字段 |
| `allowed_skill_script_keys` | `TEXT` | NN，默认 `'[]'` | 允许执行的 Skill 脚本键集合 |
| `skill_catalog_version` | `TEXT` | NULL | 生成授权时使用的 Skill 目录版本 |

索引：

| 索引 | 列 | 用途 |
| --- | --- | --- |
| `idx_tool_grants_run` | `(run_id, revoked_at, expires_at)` | 查询 Run 内有效或已撤销授权 |
| `idx_tool_grants_parent` | `(parent_grant_id)` | 追踪授权委派树 |

### 5.3 `tool_approvals`

保存需要用户或宿主决策的工具调用同意请求及最终决策。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `approval_request_id` | `TEXT` | PK | 同意请求标识 |
| `call_id` | `TEXT` | NN | 对应工具调用标识 |
| `run_id` | `TEXT` | NN | 所属 Run 标识 |
| `agent_id` | `TEXT` | NN | 发起请求的 Agent |
| `tool_id` | `TEXT` | NN | 工具标识 |
| `arguments_hash` | `TEXT` | NN | 待同意参数的哈希 |
| `decision` | `TEXT` | NN | 同意、拒绝或其他决策结果 |
| `grant_id` | `TEXT` | NULL | 决策成功后关联的 Grant |
| `reason` | `TEXT` | NULL | 决策理由 |
| `requested_at` | `TEXT` | NN | 请求创建时间 |
| `decided_at` | `TEXT` | NULL | 决策完成时间 |
| `request_kind` | `TEXT` | NN，默认 `'approval'` | 请求类别；Schema 15 新增 |

索引：

| 索引 | 列 | 用途 |
| --- | --- | --- |
| `idx_tool_approvals_call` | `(call_id)` | 按工具调用查询同意请求 |

### 5.4 `tool_audit`

不可依赖消息正文重建的工具审计记录。除基本调用信息外，Schema 12 增加了 Session、调用和 Work 谱系字段。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `audit_id` | `TEXT` | PK | 审计记录标识 |
| `call_id` | `TEXT` | NN | 工具调用标识 |
| `run_id` | `TEXT` | NN | 所属 Run 标识 |
| `agent_id` | `TEXT` | NN | 发起调用的 Agent |
| `parent_agent_id` | `TEXT` | NULL | 上级 Agent |
| `tool_id` | `TEXT` | NN | 工具标识 |
| `catalog_version` | `TEXT` | NN | 工具目录版本 |
| `risk` | `INTEGER` | NN | 调用风险等级 |
| `arguments_hash` | `TEXT` | NN | 工具参数哈希 |
| `target_summary` | `TEXT` | NN | 目标对象的脱敏摘要 |
| `grant_id` | `TEXT` | NULL | 使用的 Grant 标识 |
| `approval_request_id` | `TEXT` | NULL | 使用的同意请求标识 |
| `delegation_chain` | `TEXT` | NN | 调用时的委派链 |
| `status` | `TEXT` | NN | 审计的调用终态或当前状态 |
| `result_hash` | `TEXT` | NULL | 结果哈希 |
| `diagnostic_id` | `TEXT` | NN | 关联诊断标识 |
| `created_at` | `TEXT` | NN | 审计创建时间 |
| `completed_at` | `TEXT` | NULL | 调用完成时间 |
| `duration_ms` | `INTEGER` | NULL | 调用持续时间（毫秒） |
| `session_id` | `TEXT` | NULL | 所属 Session；Schema 12 新增 |
| `invocation_id` | `TEXT` | NULL | 当前调用标识；Schema 12 新增 |
| `parent_invocation_id` | `TEXT` | NULL | 父调用标识；Schema 12 新增 |
| `work_step_id` | `TEXT` | NULL | 所属 Work 步骤；Schema 12 新增 |
| `plan_version` | `TEXT` | NULL | 所属 Work 计划版本；Schema 12 新增 |
| `root_grant_id` | `TEXT` | NULL | 授权委派链根 Grant；Schema 12 新增 |

索引：

| 索引 | 列 | 用途 |
| --- | --- | --- |
| `idx_tool_audit_call` | `(call_id, created_at)` | 按调用查询按时间排序的审计记录 |

## 6. Meeting 模式

Meeting 模式使用四张表持久化会议状态、轮次、参与者和逐次 Agent 调用。它们均由 Schema 10 引入。

### 6.1 `meeting_sessions`

一个 Meeting Session 对应一行，保存会议调度器状态、策略和待人工审批信息。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `session_id` | `TEXT` | PK，FK -> `sessions.session_id`，`ON DELETE CASCADE` | 对应通用 Session |
| `run_id` | `TEXT` | NN | 当前或最近关联 Run |
| `status` | `TEXT` | NN | Meeting 生命周期状态 |
| `current_round` | `INTEGER` | NN，默认 `0` | 当前轮次序号 |
| `selector_state` | `BLOB` | NULL | 调度器内部状态的二进制快照 |
| `policy_json` | `TEXT` | NN，默认 `'{}'` | 会议策略 JSON |
| `policy_hash` | `TEXT` | NN，默认 `''` | 会议策略哈希 |
| `selection_version` | `INTEGER` | NN，默认 `0` | 对应 Selection 版本 |
| `pending_approval_request_id` | `TEXT` | NULL | 待处理同意请求标识 |
| `pending_approval_json` | `TEXT` | NULL | 待处理审批详情 JSON |
| `pending_approval_status` | `TEXT` | NULL | 待处理审批状态 |
| `created_at` | `TEXT` | NN | 创建时间 |
| `updated_at` | `TEXT` | NN | 最近更新时间 |

索引：

| 索引 | 列 | 用途 |
| --- | --- | --- |
| `idx_meeting_sessions_status` | `(status, updated_at)` | 按状态查询并按更新时间排序 |
| `idx_meeting_sessions_pending_approval` | `(pending_approval_request_id)`，部分索引 | 查询存在待审批请求的会议；仅索引该列非空的行 |

### 6.2 `meeting_rounds`

保存 Meeting Session 的每一个轮次及其摘要信息。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `session_id` | `TEXT` | PK 组成列，FK -> `sessions.session_id`，`ON DELETE CASCADE` | 所属 Meeting Session |
| `round_index` | `INTEGER` | PK 组成列 | Session 内轮次序号 |
| `run_id` | `TEXT` | NN | 所属 Run |
| `status` | `TEXT` | NN | 轮次状态 |
| `first_invocation_id` | `TEXT` | NULL | 本轮首个调用标识 |
| `summary_message_id` | `TEXT` | NULL | 本轮总结消息标识 |
| `summarizes_through_seq` | `INTEGER` | NULL | 总结已覆盖的消息序号上界 |
| `summary_policy_hash` | `TEXT` | NULL | 生成总结时的策略哈希 |
| `summary_agent_id` | `TEXT` | NULL | 生成总结的 Agent |
| `summary_invocation_id` | `TEXT` | NULL | 生成总结的调用标识 |
| `created_at` | `TEXT` | NN | 创建时间 |
| `started_at` | `TEXT` | NULL | 轮次开始时间 |
| `completed_at` | `TEXT` | NULL | 轮次完成时间 |

主键：`(session_id, round_index)`。

索引：

| 索引 | 列 | 用途 |
| --- | --- | --- |
| `idx_meeting_rounds_status` | `(session_id, status, round_index)` | 按 Session、状态和轮次检索 |

### 6.3 `meeting_participants`

保存参会 Agent 的加入顺序、展示信息和退出状态。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `session_id` | `TEXT` | PK 组成列，FK -> `sessions.session_id`，`ON DELETE CASCADE` | 所属 Meeting Session |
| `participant_id` | `TEXT` | PK 组成列 | 参会者标识 |
| `agent_id` | `TEXT` | NN | 参会 Agent 标识 |
| `agent_ref_json` | `TEXT` | NULL | Agent 引用快照 JSON |
| `display_name` | `TEXT` | NULL | 展示名称 |
| `join_order` | `INTEGER` | NN | 加入顺序 |
| `status` | `TEXT` | NN，默认 `'active'` | 参会者状态 |
| `joined_selection_version` | `INTEGER` | NN | 加入时的 Selection 版本 |
| `removed_selection_version` | `INTEGER` | NULL | 移除时的 Selection 版本 |
| `joined_at` | `TEXT` | NN | 加入时间 |
| `removed_at` | `TEXT` | NULL | 移除时间 |
| `updated_at` | `TEXT` | NN | 最近更新时间 |

主键：`(session_id, participant_id)`。

索引：

| 索引 | 列 | 用途 |
| --- | --- | --- |
| `idx_meeting_participants_join_order` | `(session_id, join_order, participant_id)` | 按加入顺序稳定列出参会者 |

### 6.4 `meeting_invocations`

保存每一轮中各参会者或角色的调度和执行情况。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `invocation_id` | `TEXT` | PK | 调用标识 |
| `session_id` | `TEXT` | NN，FK -> `sessions.session_id`，`ON DELETE CASCADE` | 所属 Meeting Session |
| `run_id` | `TEXT` | NN | 所属 Run |
| `round_index` | `INTEGER` | NN | 所属轮次 |
| `ordinal` | `INTEGER` | NN | 本轮调用顺序号 |
| `participant_id` | `TEXT` | NULL | 被调度参会者标识 |
| `agent_id` | `TEXT` | NULL | 被调度 Agent 标识 |
| `role` | `TEXT` | NULL | 此次调用所用角色 |
| `status` | `TEXT` | NN | 调用状态 |
| `selection_version` | `INTEGER` | NN | 执行时的 Selection 版本 |
| `retry_count` | `INTEGER` | NN，默认 `0` | 已重试次数 |
| `message_id` | `TEXT` | NULL | 调用产生的消息标识 |
| `selector_decision_json` | `TEXT` | NULL | 调度器决策 JSON |
| `scheduled_at` | `TEXT` | NN | 调度时间 |
| `started_at` | `TEXT` | NULL | 开始执行时间 |
| `completed_at` | `TEXT` | NULL | 完成时间 |
| `error_code` | `TEXT` | NULL | 错误代码 |
| `error_message` | `TEXT` | NULL | 错误说明 |

索引：

| 索引 | 列 | 用途 |
| --- | --- | --- |
| `idx_meeting_invocations_session_round_ordinal` | `(session_id, round_index, ordinal)`，唯一 | 保证同一轮内调用顺序唯一 |
| `idx_meeting_invocations_scheduled` | `(session_id, status, scheduled_at)` | 按 Session 查询待执行或已调度调用 |
| `idx_meeting_invocations_run_round` | `(run_id, round_index)` | 查询 Run 中指定轮次的调用 |

## 7. Work 模式

Work 模式使用六张表保存总经理、计划版本、步骤 DAG、尝试记录和后台任务。`work_sessions` 及相关运行表由 Schema 11 引入，计划修订由 Schema 13 引入。

### 7.1 `work_sessions`

一行对应一个 Work 模式 Session，持有当前计划、策略和是否需人工介入的状态。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `session_id` | `TEXT` | PK，FK -> `sessions.session_id`，`ON DELETE CASCADE` | 对应通用 Session |
| `run_id` | `TEXT` | NN | 当前或最近关联 Run |
| `status` | `TEXT` | NN | Work Session 当前状态 |
| `general_manager_id` | `TEXT` | NN | 总经理 Agent 标识 |
| `workflow_policy_json` | `TEXT` | NN，默认 `'{}'` | 工作流策略 JSON |
| `context_policy_json` | `TEXT` | NN，默认 `'{}'` | 上下文策略 JSON |
| `plan_version` | `TEXT` | NN，默认 `'1'` | 当前计划版本 |
| `plan_message_id` | `TEXT` | NULL | 当前计划来源消息标识 |
| `plan_json` | `TEXT` | NULL | 当前计划 JSON |
| `pending_approval_request_id` | `TEXT` | NULL | 待处理同意请求标识 |
| `pending_approval_json` | `TEXT` | NULL | 待处理审批详情 JSON |
| `requires_manual_intervention` | `INTEGER` | NN，默认 `0` | 是否需要人工介入 |
| `created_at` | `TEXT` | NN | 创建时间 |
| `updated_at` | `TEXT` | NN | 最近更新时间 |

索引：

| 索引 | 列 | 用途 |
| --- | --- | --- |
| `idx_work_sessions_status` | `(status, updated_at)` | 按状态查询并按更新时间排序 |

### 7.2 `work_steps`

保存计划中的工作步骤。主键包含 `plan_version`，允许同一逻辑步骤在不同计划版本中保留记录；依赖边与尝试记录分别保存在独立表中。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `step_id` | `TEXT` | PK 组成列 | 步骤标识 |
| `plan_version` | `TEXT` | PK 组成列 | 所属计划版本 |
| `run_id` | `TEXT` | NN | 所属 Run |
| `session_id` | `TEXT` | NN | 所属 Work Session |
| `target_agent_id` | `TEXT` | NULL | 目标执行 Agent |
| `status` | `TEXT` | NULL，默认 `'pending'` | 步骤状态 |
| `step_input_hash` | `TEXT` | NULL | 步骤输入哈希 |
| `result_json` | `TEXT` | NULL | 步骤结果 JSON |
| `created_at` | `TEXT` | NULL | 创建时间 |
| `completed_at` | `TEXT` | NULL | 完成时间 |
| `parent_step_id` | `TEXT` | NULL | 父步骤标识；Schema 11 新增 |
| `invocation_id` | `TEXT` | NULL | 关联调用标识；Schema 11 新增 |
| `depth` | `INTEGER` | NN，默认 `0` | 步骤在计划树中的深度；Schema 11 新增 |
| `step_index` | `INTEGER` | NN，默认 `0` | 同层或计划内排序号；Schema 11 新增 |
| `attempt_count` | `INTEGER` | NN，默认 `0` | 已尝试次数；Schema 11 新增 |
| `error_message` | `TEXT` | NULL | 最近错误说明；Schema 11 新增 |
| `started_at` | `TEXT` | NULL | 开始执行时间；Schema 11 新增 |
| `title` | `TEXT` | NULL | 人类可读标题；Schema 11 新增 |
| `goal` | `TEXT` | NULL | 步骤目标；Schema 11 新增 |
| `is_background` | `INTEGER` | NN，默认 `0` | 是否作为后台步骤执行；Schema 11 新增 |
| `depends_json` | `TEXT` | NULL | 步骤依赖的冗余 JSON 表示；Schema 11 新增 |
| `step_message_id` | `TEXT` | NULL | 描述步骤的消息标识；Schema 13 新增 |
| `reuses_step_id` | `TEXT` | NULL | 复用结果来源步骤标识；Schema 13 新增 |
| `replaces_step_id` | `TEXT` | NULL | 被本步骤替代的步骤标识；Schema 13 新增 |
| `checkpoint_json` | `TEXT` | NULL | 可恢复检查点 JSON；Schema 13 新增 |

主键：`(step_id, plan_version)`。

索引：

| 索引 | 列 | 用途 |
| --- | --- | --- |
| `idx_work_steps_session` | `(session_id, plan_version, status)` | 查询某计划版本内的步骤状态 |
| `idx_work_steps_run` | `(run_id, status)` | 查询 Run 内的步骤 |
| `idx_work_steps_status` | `(status, session_id)` | 按状态选择待调度步骤 |
| `idx_work_steps_agent` | `(target_agent_id, status)` | 按目标 Agent 查询待执行步骤 |

### 7.3 `work_step_dependencies`

保存步骤 DAG 的依赖边。一条记录表达当前计划版本中，一个步骤依赖另一个步骤。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `step_id` | `TEXT` | PK 组成列 | 依赖方步骤标识 |
| `plan_version` | `TEXT` | PK 组成列 | 计划版本 |
| `depends_on_step_id` | `TEXT` | PK 组成列 | 被依赖步骤标识 |

主键：`(step_id, plan_version, depends_on_step_id)`。

索引：

| 索引 | 列 | 用途 |
| --- | --- | --- |
| `idx_work_step_dependencies_step` | `(step_id, plan_version)` | 查询一个步骤依赖哪些步骤 |
| `idx_work_step_dependencies_depends` | `(depends_on_step_id, plan_version)` | 查询哪些步骤依赖指定步骤 |

### 7.4 `work_step_attempts`

保存同一 Work 步骤的每次尝试，避免重试状态覆盖历史尝试信息。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `step_id` | `TEXT` | PK 组成列 | 被尝试的步骤标识 |
| `plan_version` | `TEXT` | PK 组成列 | 计划版本 |
| `attempt_number` | `INTEGER` | PK 组成列 | 第几次尝试 |
| `invocation_id` | `TEXT` | NN | 本次尝试关联的调用标识 |
| `status` | `TEXT` | NN | 尝试状态 |
| `error_message` | `TEXT` | NULL | 本次尝试的错误说明 |
| `started_at` | `TEXT` | NULL | 开始时间 |
| `completed_at` | `TEXT` | NULL | 完成时间 |

主键：`(step_id, plan_version, attempt_number)`。

索引：

| 索引 | 列 | 用途 |
| --- | --- | --- |
| `idx_work_step_attempts_step` | `(step_id, plan_version, attempt_number)` | 按步骤读取重试历史 |

### 7.5 `work_background_jobs`

保存需要在后台继续运行或观察的 Work 步骤任务。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `job_id` | `TEXT` | PK | 后台任务标识 |
| `session_id` | `TEXT` | NN | 所属 Work Session |
| `run_id` | `TEXT` | NN | 所属 Run |
| `step_id` | `TEXT` | NN | 对应步骤标识 |
| `plan_version` | `TEXT` | NN | 对应计划版本 |
| `status` | `TEXT` | NN | 后台任务状态 |
| `error_message` | `TEXT` | NULL | 后台任务错误说明 |
| `created_at` | `TEXT` | NN | 创建时间 |
| `started_at` | `TEXT` | NULL | 开始时间 |
| `completed_at` | `TEXT` | NULL | 完成时间 |

索引：

| 索引 | 列 | 用途 |
| --- | --- | --- |
| `idx_work_background_jobs_session` | `(session_id, status)` | 查询 Session 内后台任务 |
| `idx_work_background_jobs_step` | `(step_id, plan_version)` | 查询步骤关联的后台任务 |

### 7.6 `work_plan_revisions`

保存 Work Session 的不可变计划版本历史；`work_sessions` 保存当前计划，本表保留每次提交后的版本快照。

| 字段 | 类型 | 约束/默认值 | 说明 |
| --- | --- | --- | --- |
| `session_id` | `TEXT` | PK 组成列，FK -> `work_sessions.session_id`，`ON DELETE CASCADE` | 所属 Work Session |
| `plan_version` | `TEXT` | PK 组成列 | 计划版本 |
| `run_id` | `TEXT` | NN | 生成或提交计划的 Run |
| `plan_message_id` | `TEXT` | NN | 承载计划的消息标识 |
| `plan_json` | `TEXT` | NN | 完整计划 JSON |
| `previous_plan_version` | `TEXT` | NULL | 上一个计划版本 |
| `created_at` | `TEXT` | NN | 版本创建时间 |

主键：`(session_id, plan_version)`。

索引：

| 索引 | 列 | 用途 |
| --- | --- | --- |
| `idx_work_plan_revisions_session` | `(session_id, created_at, plan_version)` | 按时间读取计划修订历史 |

## 8. 外键与数据关系

数据库定义的外键仅限下表。许多 `_id` 字段为跨子系统关联键，但没有声明 SQLite 外键，以保持恢复、归档和增量写入的兼容性。

| 子表 | 外键 | 删除行为 |
| --- | --- | --- |
| `runs` | `session_id` -> `sessions.session_id` | 未指定 |
| `session_selections` | `session_id` -> `sessions.session_id` | `CASCADE` |
| `agent_snapshots` | `session_id` -> `sessions.session_id` | `CASCADE` |
| `tool_grants` | `parent_grant_id` -> `tool_grants.grant_id` | 未指定 |
| `meeting_sessions` | `session_id` -> `sessions.session_id` | `CASCADE` |
| `meeting_rounds` | `session_id` -> `sessions.session_id` | `CASCADE` |
| `meeting_participants` | `session_id` -> `sessions.session_id` | `CASCADE` |
| `meeting_invocations` | `session_id` -> `sessions.session_id` | `CASCADE` |
| `work_sessions` | `session_id` -> `sessions.session_id` | `CASCADE` |
| `work_plan_revisions` | `session_id` -> `work_sessions.session_id` | `CASCADE` |

## 9. Schema 迁移历史

每个版本迁移在独立事务中完成后，向 `schema_versions` 写入对应版本。当前 Runtime 要求最终版本为 `16`。

| 版本 | 变更 |
| ---: | --- |
| 1 | 创建 Session、Run、幂等、事件 Outbox 与 GSN 基础表和索引 |
| 2 | 创建 `tool_intents` |
| 3 | 创建 `message_index` 及 Session 内消息顺序唯一索引 |
| 4 | 创建初始 `work_steps` |
| 5 | 为两张幂等表补充 `request_hash` |
| 6 | 为 `runs` 补充 `terminal_text` |
| 7 | 创建 `session_selections` 与 `agent_snapshots` |
| 8 | 扩展 `tool_intents`；创建 Tool Grant、Approval、Audit 表和索引 |
| 9 | 创建 `invocation_snapshots` 及其索引 |
| 10 | 创建全部 Meeting 表及其索引 |
| 11 | 创建 Work Session、依赖、尝试和后台任务表；扩展 Work 步骤与工具意图 |
| 12 | 为 `tool_audit` 增加 Session、调用、Work 和根 Grant 谱系字段 |
| 13 | 创建 `work_plan_revisions`；为 `work_steps` 增加消息、复用、替换和检查点字段 |
| 14 | 为 `sessions` 增加 `title` |
| 15 | 为 `tool_approvals` 增加 `request_kind` |
| 16 | 为 `runs` 增加进度与结构化错误字段，并创建 `idx_runs_progress` |

启动时 Runtime 还会补齐 Schema 8、12 和 15 的兼容字段，随后验证全部必需表、索引和字段。发现版本高于 16、缺少必需对象或版本不匹配时，Runtime 会拒绝正常使用该数据库。

## 10. 运维与查询边界

- 使用 `madorin db check --workspace <path> --verbose` 检查 Schema、完整性和消息索引一致性。
- 使用 `madorin db backup --workspace <path> --include-messages` 获取同时包含数据库和权威消息正文的备份集。
- 使用 `madorin db migrate --workspace <path> --dry-run` 预览升级；迁移只允许向前。
- `madorin db rebuild` 只能从 JSONL 权威消息重建可推导的 Session 元数据与消息索引，无法恢复 Run、Grant、工具意图、审计和调用快照。
- SQL 诊断必须在 Runtime 停止写入、已完成备份且遵守数据脱敏要求的维护窗口中执行；业务代码和运维自动化不应直接写入 `state.db`。

更多维护锁、恢复点、退出码和故障处置约束见 [05-安全与数据运维](./05-安全与数据运维.md)；GSN、会话恢复与检查点语义见 [14-断点恢复与会话重建](./14-断点恢复与会话重建.md)。
