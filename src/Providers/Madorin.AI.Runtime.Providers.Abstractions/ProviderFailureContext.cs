using System.Diagnostics;

namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>
/// Provider 调用失败时的安全诊断上下文。
/// 只记录失败阶段、已收到的更新条数和耗时，不包含请求或响应正文，
/// 因此可以随 <c>RuntimeError.ProviderDetails</c> 跨 Worker 进程传给宿主和日志。
/// </summary>
/// <param name="Stage">失败阶段，取值见 <see cref="ProviderFailureStages"/>。</param>
/// <param name="ReceivedUpdates">失败前已收到的流式更新条数。</param>
/// <param name="ElapsedMilliseconds">从开始请求到判定失败的耗时（毫秒）。</param>
/// <param name="NonStreaming">是否为非流式请求（<see cref="RuntimeProviderRequest.UseNonStreamingResponse"/>）。</param>
public sealed record ProviderFailureContext(
    string Stage,
    int ReceivedUpdates,
    long ElapsedMilliseconds,
    bool NonStreaming = false)
{
    /// <summary>按 <see cref="Stopwatch.GetTimestamp"/> 记录的开始时间计算耗时并创建上下文。</summary>
    /// <param name="stage">失败阶段。</param>
    /// <param name="receivedUpdates">失败前已收到的流式更新条数。</param>
    /// <param name="startTimestamp">请求开始时的 <see cref="Stopwatch.GetTimestamp"/> 值。</param>
    /// <param name="nonStreaming">是否为非流式请求。</param>
    public static ProviderFailureContext Since(
        string stage,
        int receivedUpdates,
        long startTimestamp,
        bool nonStreaming = false) =>
        new(
            stage,
            receivedUpdates,
            (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
            nonStreaming);
}

/// <summary>Provider 调用失败阶段的稳定取值，写入 ProviderDetails 的 <c>stage</c> 字段。</summary>
public static class ProviderFailureStages
{
    /// <summary>建立请求或转换消息时失败，尚未开始读取响应。</summary>
    public const string RequestStart = "request_start";

    /// <summary>读取响应的过程中抛出异常，例如连接中断、HTTP 错误或数据无法解析；非流式请求的失败也归入此阶段。</summary>
    public const string StreamRead = "stream_read";

    /// <summary>响应正常结束，但始终没有收到 finish_reason 终止标记。</summary>
    public const string StreamEndedWithoutFinishReason = "stream_ended_without_finish_reason";
}
