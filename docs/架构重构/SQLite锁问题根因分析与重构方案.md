# SQLite 锁问题根因分析与重构方案

## 问题背景

**故障现象**
- 错误：`SQLite Error 6: 'database table is locked'` (SQLITE_LOCKED_SHAREDCACHE)
- 发生位置：`SqliteEventOutbox.MarkSentAsync` 更新 `event_outbox` 表
- 触发场景：长时间 reasoning 流式输出期间

**数据规模**
- 数据库：182 MB (`state.db`)
- JSONL 文件：10 MB (51 个文件)
- `.evt` 文件：20 KB (46 个文件)
- `event_outbox` 表：537,179 行
  - 已确认未清理：535,292 条 (99.6%)
  - Delta 事件占比：97.9% (525,750 条)
    - `reasoning.delta`: 约 40%
    - `output.delta`: 约 40%
    - `tool.call.delta`: 约 18%

**峰值流量**
- 单 Run 单秒最高：350 条 delta 事件
- 单条 payload：平均 32 KiB
- 四次故障均发生在模型长时间 reasoning 流式输出期间

---

## 根本问题分析

### 1. 架构设计错误

**流式 Delta 事件错误地进入了持久化路径**

当前设计让每个 `reasoning.delta`、`output.delta`、`tool.call.delta` 都要：
1. 分配全局序号 (GSN)
2. 写入数据库 `event_outbox` 表
3. 写镜像 `.evt` 文件到磁盘
4. 回写 `sent` 状态
5. 等待宿主 ACK 后更新 `acknowledged`

这导致：
- **高频写入**：峰值每秒 350 次数据库事务
- **无意义持久化**：临时展示片段占据 183 MB 数据库空间
- **职责倒置**：流式展示失败导致 `run.failed`

### 2. SQLite 表级锁冲突不可避免

**锁冲突机制**
```
连接模式: Cache=Shared (表级锁)
├── 宿主批量 ACK (每 10 条) → 占写锁 持续时间长
└── Run 进程 MarkSentAsync → 尝试写锁 → 5秒重试窗口 → 撞锁
```

**已验证**
- 内存数据库复现：两连接操作不同行仍触发 `SQLITE_LOCKED_SHAREDCACHE`
- 问题根源：架构层面让 SQLite 承担了不该承担的高频流式写入

### 3. 数据冗余与职责混乱

**三层数据存储重复**
1. 数据库 `event_outbox` 表：183 MB (97.9% 是 delta)
2. JSONL 文件 (最终消息)：10 MB
3. `.evt` 镜像文件：20 KB

**职责混乱**
- 临时展示数据 (delta) 混入持久化存储
- 数据库既存事务状态又存流式片段
- JSONL 已有完整消息但仍依赖数据库索引

---

## 重构方案

### 核心原则

1. **流式 Delta 完全退出数据库写入路径**
2. **SQLite 只保留关键状态的事务化存储**
3. **JSONL 文件作为最终消息的唯一权威源**
4. **宿主重连改为状态恢复而非全量重放**

### 数据分层设计

```
┌─────────────────────────────────────────────────────────┐
│ 层级           │ 存储介质      │ 数据类型              │
├─────────────────────────────────────────────────────────┤
│ 临时展示层     │ 内存流        │ reasoning.delta       │
│                │               │ output.delta          │
│                │               │ tool.call.delta       │
├─────────────────────────────────────────────────────────┤
│ 持久化层       │ JSONL 文件    │ 最终完整消息          │
│                │               │ 工具调用结果          │
│                │               │ 错误记录              │
├─────────────────────────────────────────────────────────┤
│ 事务状态层     │ SQLite        │ Run 状态转换          │
│                │               │ 工具执行意图与审批    │
│                │               │ 幂等键与去重          │
│                │               │ 关键事件投递状态      │
└─────────────────────────────────────────────────────────┘
```

---

## 实施计划

### 第一阶段：流式 Delta 退出数据库

**目标**
- 移除 `AppendDeltaAsync` 方法
- Delta 事件不再分配 GSN
- Delta 不写 `event_outbox` 表
- Delta 不写 `.evt` 镜像文件

**变更文件**
- `src/Madorin.AI.Runtime.Persistence.Abstractions/IEventOutbox.cs`
- `src/Madorin.AI.Runtime.Persistence.Sqlite/SqliteEventOutbox.cs`
- `src/Madorin.AI.Runtime.Server/RuntimeExecutionCore.cs`
- `src/Madorin.AI.Runtime.Server/RuntimeServer.cs`

**实现方式**
```csharp
// 新增：直接推送 Delta 到宿主连接
private async Task EmitDeltaAsync(
    string runId,
    long runSequence,
    string messageType,
    string payloadJson,
    CancellationToken ct)
{
    var envelope = new RuntimeEventEnvelope(
        InstanceId,
        gsn: 0, // Delta 不再有全局序号
        runId,
        runSequence,
        messageType,
        DateTimeOffset.UtcNow,
        JsonDocument.Parse(payloadJson).RootElement);
    
    await _eventChannel.SendAsync(envelope, ct);
    // 不写 event_outbox，不写 .evt，不调用 MarkSentAsync
}
```

### 第二阶段：保留关键状态入库

**保留在 event_outbox 中的事件类型**
```
关键 Run 状态：
  - run.accepted
  - run.status.changed
  - run.completed
  - run.failed
  - run.cancelled

工具执行审批：
  - tool.consent.required
  - tool.approval.granted
  - tool.approval.denied

凭证管理：
  - credentials.refresh.requested
```

**验证方式**
```sql
-- 重构后检查数据库，应该只有这些类型
SELECT DISTINCT message_type 
FROM event_outbox 
ORDER BY message_type;
```

**预期效果**
- 从 53 万行降至数千行 (每个 Run 约 5-10 条关键事件)
- 数据库从 183 MB 降至 < 10 MB

### 第三阶段：宿主重连协议改造

**当前协议 (按 GSN 重放)**
```csharp
// 旧逻辑：宿主传 lastConfirmedGsn，Runtime 重放所有未确认事件
await outbox.LoadPendingAsync(ct); // 可能返回 53 万条
```

**新协议 (状态恢复 + 实时流)**
```csharp
// 新逻辑：宿主重连时
// 1. 获取 Run 当前状态
var runSnapshot = await repository.GetRunSnapshotAsync(runId, ct);

// 2. 加载最近 N 条完整消息 (从 JSONL)
var recentMessages = await conversationStore.ReadPageAsync(
    sessionId,
    lastN: 10, // 可配置
    ct);

// 3. 发送状态快照
await EmitRunSnapshotAsync(runSnapshot, recentMessages, ct);

// 4. 继续接收实时流
// 不再重放历史 Delta
```

**宿主侧变更**
- 重连后清空界面
- 显示最近 10 条消息
- 接收实时流继续渲染
- 支持 `delta.dropped` 通知 (协议已支持，无需修改)

### 第四阶段：数据库清理与优化

**删除历史 Delta 数据 (可选)**
```sql
-- 谨慎操作：删除所有历史 Delta 事件
DELETE FROM event_outbox 
WHERE message_type IN (
    'reasoning.delta',
    'output.delta',
    'tool.call.delta'
);

VACUUM; -- 回收空间
```

**性能监控**
```powershell
# 监控数据库写入频率
# 应该从每秒 350 次降至每 Run 约 2 次/秒
```

### 第五阶段：测试与验证

**功能测试**
1. 正常 Run 流程
2. 宿主断连重连
3. Runtime 进程重启恢复
4. 长时间 reasoning 流式输出
5. 并发多 Run 场景

**压力测试**
- 单 Run 持续 5 分钟 reasoning
- 3 个并发 Run
- 预期：不再出现 `SQLITE_LOCKED_SHAREDCACHE` 错误

**性能验证**
```
指标                  | 重构前      | 重构后目标
---------------------|------------|-------------
数据库写入频率        | 350 次/秒   | 2 次/秒
数据库体积           | 183 MB     | < 10 MB
锁冲突概率           | 高频       | 几乎为 0
JSONL 文件体积       | 10 MB      | 10 MB (不变)
```

---

## 风险控制

### 兼容性保证

**旧宿主支持**
- 检测宿主协议版本
- 旧版本继续支持 GSN 重放 (标记为 deprecated)

**配置开关**
```json
{
  "Runtime": {
    "UseLegacyDeltaPersistence": false  // 默认新逻辑
  }
}
```

### 回滚方案

- 保留 `AppendDeltaAsync` 方法签名 (标记 Obsolete)
- 通过配置开关切换新旧逻辑
- 保留 SQL schema 向后兼容

### 数据迁移

**无需额外迁移**
- JSONL 文件已有完整消息
- `ConversationStore.RepairCoreAsync` 已能重建索引
- 数据库清理为可选操作

---

## 预期收益

### 性能提升

- **数据库写入降低 99.4%**：从每秒 350 次降至每 Run 约 2 次/秒
- **锁冲突消除**：概率从高频降至几乎为 0
- **数据库体积减少 94.5%**：从 183 MB 降至 < 10 MB
- **I/O 负载降低**：移除高频磁盘写入

### 架构清晰度

```
临时展示数据 (Delta)     → 内存流
持久化数据 (最终消息)    → JSONL
事务状态 (Run/审批)      → SQLite
```

### 可靠性提升

- 不再因流式展示失败导致 Run 失败
- 断电最多丢失当前 Run 的未完成输出
- 历史记录完整保留在 JSONL
- 关键状态仍有事务保证

---

## 技术决策依据

### 为什么 JSONL 是最终消息的权威源？

1. **已有恢复能力**：`ConversationStore.RepairCoreAsync` 已实现
2. **数据完整性**：10 MB JSONL 完整保留所有最终消息
3. **顺序 I/O**：追加写入比数据库事务快
4. **简单可靠**：纯文本，易于备份、迁移、审计

### 为什么 Delta 不需要入库？

1. **协议已支持 `delta.dropped`**：宿主可跳过丢失的流式增量
2. **临时展示数据**：只用于实时渲染，不影响最终结果
3. **高频无价值**：53 万条 delta 占 183 MB，使用价值为 0
4. **断电可接受**：最多丢失当前 Run 的未完成输出

### 为什么保留 SQLite？

1. **事务保证**：Run 状态转换需要原子性
2. **幂等性**：工具执行去重需要数据库约束
3. **审批记录**：工具同意/拒绝需要可靠存储
4. **低频写入**：关键事件每 Run 约 5-10 条，不会锁冲突

---

## 参考资料

### 相关文件

**代码文件**
- `src/Madorin.AI.Runtime.Persistence.Sqlite/SqliteEventOutbox.cs` - 事件 Outbox 实现
- `src/Madorin.AI.Runtime.Persistence.Files/ConversationStore.cs` - JSONL 存储与恢复
- `src/Madorin.AI.Runtime.Server/RuntimeServer.cs` - 事件投递与重连
- `src/Madorin.AI.Runtime.Persistence.Sqlite/DataDirectoryInitializer.cs` - 数据库连接配置

**协议文档**
- `docs/开发文档/14-断点恢复与会话重建.md` - 重连协议

**数据库**
- `E:\Workspace\多媒体处理\.madorin\state.db` - 故障现场数据库 (183 MB)

### 关键统计数据

```sql
-- Delta 事件占比
SELECT 
    message_type,
    COUNT(*) as cnt,
    ROUND(COUNT(*) * 100.0 / (SELECT COUNT(*) FROM event_outbox), 2) as percentage
FROM event_outbox
WHERE message_type LIKE '%.delta'
GROUP BY message_type
ORDER BY cnt DESC;

-- 结果：
-- reasoning.delta: 40%
-- output.delta: 40%
-- tool.call.delta: 18%
-- 合计: 97.9%
```

---

## 审批记录

- **方案提出**：2026-11-18
- **用户确认**：2026-11-18 已确认
- **实施状态**：
  - 第一阶段 ✅ 已提交（`7f0f6cb`）。注意：该阶段遗漏了服务端直发路径，Delta 实际未送达宿主，已在第三阶段修复。
  - 第二阶段 ✅ 已验证：`AppendAsync` 仅剩 9 处关键事件调用（Run 状态、工具完成、计划变更、凭证刷新）。
  - 第三阶段 ✅ 已实施：见 `第三阶段-流式直发与重连协议.md`。额外定位并修复了第二个结构性根因 `Cache=Shared`。
  - 第四阶段 📄 文档就绪、数据库已备份，待执行：见 `第四阶段-数据库清理与优化.md`。
  - 第五阶段 ✅ 已完成：见 `第五阶段-测试与验证报告.md`。3 并发 × 5 分钟 soak 通过（233,490 条 Delta 100% 送达，`event_outbox` 12 行，无锁错误）；与 `1bf0324` 对照：流式吞吐 ×4.3、Outbox 写入 −99.6%、重连回放 1112 → 2 条。剩余：用户在真实工作目录跑一次长 reasoning 做最终确认。

---

## 附录：错误日志示例

```
SQLite Error 6: 'database table is locked'
位置: SqliteEventOutbox.MarkSentAsync:283
连接模式: Cache=Shared
锁类型: SQLITE_LOCKED_SHAREDCACHE
重试窗口: 5 秒
```

**锁冲突时间窗口**
```
时间戳                  | 操作
----------------------|---------------------------
21:45:28.123          | 宿主开始批量 ACK (10 条)
21:45:28.150          | Run 进程尝试 MarkSentAsync
21:45:28.155          | 撞锁，开始重试
21:45:33.160          | 5 秒后超时，抛出异常
21:45:33.165          | Run 转为 failed 状态
```
