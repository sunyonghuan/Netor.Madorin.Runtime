namespace Madorin.AI.Runtime.Contracts.Expert;

/// <summary>Expert 模式计划的整体状态</summary>
public enum ExpertPlanStatus
{
    Pending,      // 已创建未开始
    Running,      // 执行中
    Paused,       // 暂停（预留）
    Completed,    // 已完成
    Failed,       // 失败
    Cancelled     // 取消
}

/// <summary>Expert 模式计划步骤状态</summary>
public enum ExpertStepStatus
{
    Pending,      // 待执行
    Running,      // 执行中
    Completed,    // 已完成
    Failed,       // 失败
    Skipped       // 跳过
}
