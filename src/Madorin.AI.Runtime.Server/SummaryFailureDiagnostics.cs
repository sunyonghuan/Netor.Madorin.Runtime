using System.Text.Json;
using Madorin.AI.Runtime.Modes.Expert;

namespace Madorin.AI.Runtime.Server;

/// <summary>
/// 把摘要 Provider 失败的上下文写入运行时诊断日志。
/// 压缩失败（CompactionFailed）和 Expert 自动压缩导致的 Run 失败（RunFailed）共用这套字段。
/// </summary>
internal static class SummaryFailureDiagnostics
{
    /// <summary>沿异常链查找摘要 Provider 失败异常；找不到时返回 null。</summary>
    public static SummaryProviderFailedException? Find(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SummaryProviderFailedException failure)
            {
                return failure;
            }
        }

        return null;
    }

    /// <summary>写出 summaryRequest 对象：模型、预算、分批位置、接收进度和耗时，不含对话正文。</summary>
    public static void WriteSummaryRequest(Utf8JsonWriter writer, SummaryRequestDiagnostics request)
    {
        writer.WriteStartObject("summaryRequest");
        writer.WriteString("providerId", request.ProviderId);
        writer.WriteString("modelId", request.ModelId);
        WriteOptionalNumber(writer, "contextWindow", request.ContextWindow);
        writer.WriteNumber("inputLimit", request.InputLimit);
        writer.WriteNumber("outputLimit", request.OutputLimit);
        writer.WriteNumber("mergeLevel", request.MergeLevel);
        writer.WriteNumber("batchIndex", request.BatchIndex);
        writer.WriteNumber("batchCount", request.BatchCount);
        writer.WriteNumber("requestCount", request.RequestCount);
        writer.WriteNumber("batchMessageCount", request.BatchMessageCount);
        WriteOptionalNumber(writer, "estimatedInputTokens", request.EstimatedInputTokens);
        writer.WriteBoolean("isCorrectionRetry", request.IsCorrectionRetry);
        writer.WriteString("responseMode", request.ResponseMode);
        writer.WriteNumber("receivedEvents", request.ReceivedEvents);
        writer.WriteNumber("receivedTextLength", request.ReceivedTextLength);
        writer.WriteNumber("elapsedMs", request.ElapsedMilliseconds);
        writer.WriteEndObject();
    }

    /// <summary>
    /// 写出 summaryProviderError 对象和 summaryRequest 对象。
    /// 用于外层错误码不是 Provider 错误的场景（例如 RunFailed），保留原始 Provider 错误码与 ProviderDetails。
    /// </summary>
    public static void WriteProviderFailure(Utf8JsonWriter writer, SummaryProviderFailedException failure)
    {
        var error = failure.ProviderError;
        writer.WriteStartObject("summaryProviderError");
        writer.WriteString("code", error.Code);
        writer.WriteString("category", error.Category);
        writer.WriteString("message", error.Message);
        writer.WriteBoolean("isRetryable", error.IsRetryable);
        writer.WriteString("diagnosticId", error.DiagnosticId);
        if (error.ProviderDetails is { ValueKind: not JsonValueKind.Undefined } details)
        {
            writer.WritePropertyName("providerDetails");
            details.WriteTo(writer);
        }

        writer.WriteEndObject();
        WriteSummaryRequest(writer, failure.Request);
    }

    private static void WriteOptionalNumber(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
    }
}
