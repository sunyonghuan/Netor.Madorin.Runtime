using Madorin.AI.Runtime.Contracts.Expert;

namespace Madorin.AI.Runtime.Services.AgentJobs;

/// <summary>并行子 Agent 作业状态（无定时调度）。</summary>
public static class AgentJobStates
{
    public const string Pending = "pending";
    public const string Running = "running";
    /// <summary>已请求取消，Worker 尚未完全停下。</summary>
    public const string Cancelling = "cancelling";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string TimedOut = "timed_out";

    public static bool IsTerminal(string state) =>
        string.Equals(state, Completed, StringComparison.Ordinal)
        || string.Equals(state, Failed, StringComparison.Ordinal)
        || string.Equals(state, Cancelled, StringComparison.Ordinal)
        || string.Equals(state, TimedOut, StringComparison.Ordinal);
}

/// <summary>创建并行作业时的输入。</summary>
public sealed record AgentJobCreateRequest(
    string AgentName,
    string Task,
    int? TimeoutMs = null,
    bool CreateIfMissing = false,
    bool Temporary = false,
    string? Role = null,
    string[]? Capabilities = null,
    string[]? AllowedToolIds = null);

/// <summary>作业对外快照，用于 status/result/wait 回收。</summary>
public sealed record AgentJobSnapshot(
    string JobId,
    string State,
    string AgentName,
    string Task,
    string? Result,
    string? Error,
    string RunId,
    string SessionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    ExpertToolEvidence[]? Evidence = null);

/// <summary>Worker 执行一次子 Agent 任务的结果。</summary>
public sealed record AgentJobWorkerResult(
    bool Success,
    string? ResultText,
    string? Error,
    ExpertToolEvidence[]? Evidence = null);

/// <summary>
/// 由 Expert 编排器绑定的实际执行器。
/// 协调器只负责入队/并发/状态；真正调用子 Agent 由编排器完成。
/// </summary>
public delegate Task<AgentJobWorkerResult> AgentJobWorker(
    string jobId,
    string agentName,
    string task,
    string runId,
    string sessionId,
    string parentAgentId,
    CancellationToken ct);

public delegate Task<AgentJobWorkerResult> AgentJobWorkerWithRequest(
    string jobId,
    AgentJobCreateRequest request,
    string runId,
    string sessionId,
    string parentAgentId,
    CancellationToken ct);
