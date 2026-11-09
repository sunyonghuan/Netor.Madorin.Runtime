using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Modes.Expert;

/// <summary>
/// 摘要模型返回失败事件时抛出的异常。
/// 消息文本与原有的 <see cref="InvalidOperationException"/> 保持一致，
/// 额外携带 Provider 错误和本次摘要请求的上下文，供运行时诊断日志记录。
/// </summary>
public sealed class SummaryProviderFailedException : InvalidOperationException
{
    /// <summary>创建摘要 Provider 失败异常。</summary>
    /// <param name="providerError">Provider 返回的脱敏错误，含 ProviderDetails。</param>
    /// <param name="request">失败时摘要请求的上下文。</param>
    public SummaryProviderFailedException(RuntimeError providerError, SummaryRequestDiagnostics request)
        : base($"Summary provider failed ({providerError.Code}): {providerError.Message}")
    {
        ProviderError = providerError;
        Request = request;
    }

    /// <summary>Provider 返回的脱敏错误。</summary>
    public RuntimeError ProviderError { get; }

    /// <summary>失败时摘要请求的上下文。</summary>
    public SummaryRequestDiagnostics Request { get; }
}

/// <summary>
/// 单次摘要请求的诊断上下文。只包含模型、预算、分批位置、计数和耗时，不包含对话正文。
/// </summary>
/// <param name="ProviderId">摘要使用的 Provider。</param>
/// <param name="ModelId">摘要使用的模型。</param>
/// <param name="ContextWindow">模型声明的上下文窗口；未声明时为空。</param>
/// <param name="InputLimit">摘要请求允许的输入 token 上限。</param>
/// <param name="OutputLimit">摘要请求的输出 token 上限。</param>
/// <param name="MergeLevel">分批合并的层级，0 表示直接摘要历史记录。</param>
/// <param name="BatchIndex">当前批次序号，从 1 开始。</param>
/// <param name="BatchCount">当前层级的批次总数。</param>
/// <param name="RequestCount">本次压缩累计发出的摘要请求数（含本次）。</param>
/// <param name="BatchMessageCount">当前批次包含的历史消息条数。</param>
/// <param name="EstimatedInputTokens">按 UTF-8 字节数估算的输入 token。</param>
/// <param name="IsCorrectionRetry">是否为格式纠正后的重试请求。</param>
/// <param name="ResponseMode">请求模式：non_streaming（默认）或 streaming（服务端拒绝非流式后的回退）。</param>
/// <param name="ReceivedEvents">失败前收到的 Provider 事件条数。</param>
/// <param name="ReceivedTextLength">失败前收到的摘要文本长度（字符）。</param>
/// <param name="ElapsedMilliseconds">本次请求从发出到失败的耗时（毫秒）。</param>
public sealed record SummaryRequestDiagnostics(
    string ProviderId,
    string ModelId,
    int? ContextWindow,
    int InputLimit,
    int OutputLimit,
    int MergeLevel,
    int BatchIndex,
    int BatchCount,
    int RequestCount,
    int BatchMessageCount,
    int? EstimatedInputTokens,
    bool IsCorrectionRetry,
    string ResponseMode,
    int ReceivedEvents,
    int ReceivedTextLength,
    long ElapsedMilliseconds);
