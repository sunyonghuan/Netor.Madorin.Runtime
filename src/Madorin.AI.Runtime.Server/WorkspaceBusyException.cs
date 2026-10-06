using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Server;

/// <summary>
/// 工作区写锁被另一个仍在运行的 Runtime 持有。
/// 调用方可以等待后重试，不应当成普通配置错误。
/// </summary>
public sealed class WorkspaceBusyException : InvalidOperationException
{
    public WorkspaceBusyException(
        string lockPath,
        string? runtimeInstanceId,
        int? holderProcessId,
        DateTimeOffset? startedAt,
        string message)
        : base(message)
    {
        LockPath = lockPath;
        RuntimeInstanceId = runtimeInstanceId;
        HolderProcessId = holderProcessId;
        StartedAt = startedAt;
    }

    public string LockPath { get; }

    public string? RuntimeInstanceId { get; }

    public int? HolderProcessId { get; }

    public DateTimeOffset? StartedAt { get; }

    public RuntimeError ToError() =>
        new(
            RuntimeErrorCodes.WorkspaceBusy,
            "runtime",
            Message,
            IsRetryable: true,
            ProviderDetails: null,
            DiagnosticId: Guid.NewGuid().ToString("N"));
}
