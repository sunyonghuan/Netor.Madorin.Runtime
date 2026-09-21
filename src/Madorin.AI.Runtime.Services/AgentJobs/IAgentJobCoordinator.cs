namespace Madorin.AI.Runtime.Services.AgentJobs;

/// <summary>
/// 并行子 Agent 作业协调器：创建、查询、回收结果、等待、取消。
/// 不含定时/cron；长期编排交给 Work 模式。
/// </summary>
public interface IAgentJobCoordinator : IAsyncDisposable
{
    /// <summary>绑定实际执行 Worker（Expert Run 开始时调用）。</summary>
    public void BindWorker(AgentJobWorker worker);

    /// <summary>创建并入队一个作业，立即返回快照（不等待完成）。</summary>
    public AgentJobSnapshot Create(
        AgentJobCreateRequest request,
        string runId,
        string sessionId,
        string parentAgentId);

    /// <summary>
    /// 批量创建并入队。先校验全部请求与容量，再统一入队；中途失败会取消已入队项。
    /// </summary>
    public IReadOnlyList<AgentJobSnapshot> CreateMany(
        IReadOnlyList<AgentJobCreateRequest> requests,
        string runId,
        string sessionId,
        string parentAgentId);

    /// <summary>查询作业状态；不存在返回 null。</summary>
    public AgentJobSnapshot? GetSnapshot(string jobId);

    /// <summary>回收终态结果；未完成（含 cancelling）时 Ready=false。</summary>
    public AgentJobCollectResult Collect(string jobId);

    /// <summary>批量回收；每个 job 一条 Collect 结果。</summary>
    public IReadOnlyList<AgentJobCollectResult> CollectMany(IReadOnlyList<string> jobIds);

    /// <summary>
    /// 请求取消。pending/running → cancelling；真正停住后变为 cancelled。
    /// 已终态返回 false。
    /// </summary>
    public bool TryCancel(string jobId);

    /// <summary>
    /// 等待指定作业全部进入终态或超时。
    /// jobId 不存在时抛出 <see cref="ArgumentException"/>（快速失败，不空等）。
    /// </summary>
    public Task<AgentJobWaitResult> WaitAsync(
        IReadOnlyList<string> jobIds,
        int timeoutMs,
        CancellationToken ct = default);
}

/// <summary>单次结果回收。</summary>
public sealed record AgentJobCollectResult(
    string JobId,
    bool Ready,
    bool Success,
    string State,
    string AgentName,
    string Task,
    string? Result,
    string? Error,
    string Message);

/// <summary>wait 回收结果。</summary>
public sealed record AgentJobWaitResult(
    bool AllTerminal,
    bool TimedOut,
    bool AllSucceeded,
    IReadOnlyList<AgentJobSnapshot> Jobs);
