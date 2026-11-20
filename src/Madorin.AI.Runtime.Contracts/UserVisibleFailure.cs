namespace Madorin.AI.Runtime.Contracts;

/// <summary>
/// 用户可见的失败说明。不包含 SDK、HTTP 或系统异常原文。
/// </summary>
public static class UserVisibleFailure
{
    /// <summary>
    /// Run 级失败的稳定说明。超时使用固定文案；我们自己抛出的协议错误保留短消息；其余只说明执行失败。
    /// </summary>
    public static string ForRun(Exception? exception, string errorCode)
    {
        if (string.Equals(errorCode, RuntimeErrorCodes.RunTimedOut, StringComparison.Ordinal))
        {
            return "The Run exceeded its configured timeout.";
        }

        if (exception is InvalidDataException && IsStableMessage(exception.Message))
        {
            return exception.Message;
        }

        return "Run execution failed.";
    }

    private static bool IsStableMessage(string? message)
        => !string.IsNullOrWhiteSpace(message)
           && message.Length <= 300
           && message.AsSpan().IndexOfAny('\r', '\n') < 0;
}
