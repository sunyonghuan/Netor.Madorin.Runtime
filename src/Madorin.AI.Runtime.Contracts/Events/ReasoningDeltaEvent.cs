namespace Madorin.AI.Runtime.Contracts;

public sealed record ReasoningDeltaEvent(
    string InvocationId,
    string Delta);

/// <summary>一段思考结束。调用完成、失败、取消或开始输出正文/工具时发出。</summary>
public sealed record ReasoningCompletedEvent(string InvocationId);
