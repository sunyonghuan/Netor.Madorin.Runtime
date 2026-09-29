namespace Madorin.AI.Runtime.Contracts;

/// <summary>执行过程中的用户插话，等当前步骤结束后再交给总经理。</summary>
public sealed record WorkInputEnqueueRequest(
    string SessionId,
    string RequestId,
    string Text);

/// <summary>插话是否写入队列。</summary>
public sealed record WorkInputEnqueueResult(
    string SessionId,
    string RequestId,
    bool Accepted,
    string Status);
