using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>Creates stable, redacted Runtime errors from Provider failures.</summary>
public static class ProviderErrorFactory
{
    /// <summary>ProviderDetails 中异常类型链的最大层数，避免异常嵌套过深时详情无限增长。</summary>
    private const int MaximumExceptionTypeDepth = 6;

    /// <summary>
    /// 生成脱敏后的 Provider 错误。
    /// ProviderDetails 只写入状态码、请求标识、异常类型链和可选的失败上下文，
    /// 不写入异常消息，因为 SDK 的异常消息可能包含响应正文或提示词。
    /// </summary>
    public static RuntimeError Create(
        string providerName,
        Exception exception,
        int? statusCode = null,
        string? retryAfter = null,
        string? requestId = null,
        bool isProtocolError = false,
        string? providerCode = null,
        ProviderFailureContext? failureContext = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(exception);

        var metadata = InspectExceptionChain(exception);
        statusCode ??= metadata.StatusCode;
        retryAfter ??= metadata.RetryAfter;
        requestId ??= metadata.RequestId;
        providerCode ??= metadata.ProviderCode;

        var isTimeout = metadata.IsTimeout;
        isProtocolError |= metadata.IsProtocolError;
        var code = statusCode switch
        {
            401 or 403 => RuntimeErrorCodes.AuthenticationFailed,
            429 => RuntimeErrorCodes.ProviderRateLimited,
            _ when isTimeout => RuntimeErrorCodes.ProviderTimeout,
            _ when isProtocolError || exception is JsonException => RuntimeErrorCodes.ProviderProtocolError,
            _ => RuntimeErrorCodes.ProviderRequestFailed
        };
        var category = statusCode switch
        {
            401 or 403 => "authentication",
            429 => "rate_limit",
            _ when isTimeout => "timeout",
            _ when isProtocolError || exception is JsonException => "protocol",
            _ => "provider"
        };
        var isRetryable = statusCode is 408 or 409 or 429 or >= 500
            || isTimeout
            || (metadata.IsHttpFailure && statusCode is null);

        return new RuntimeError(
            code,
            category,
            $"{providerName} request failed with code '{GetProviderCode(statusCode, providerCode, exception)}'.",
            isRetryable,
            CreateDetails(statusCode, retryAfter, requestId, providerCode, exception, failureContext),
            Guid.NewGuid().ToString("N"));
    }

    private static FailureMetadata InspectExceptionChain(Exception exception)
    {
        int? statusCode = null;
        string? retryAfter = null;
        string? requestId = null;
        string? providerCode = null;
        var isHttpFailure = false;
        var isProtocolError = false;
        var isTimeout = false;

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is ProviderHttpException providerHttpException)
            {
                statusCode ??= (int?)providerHttpException.StatusCode;
                retryAfter ??= providerHttpException.RetryAfter;
                requestId ??= providerHttpException.RequestId;
                providerCode ??= providerHttpException.ProviderCode;
                isHttpFailure = true;
            }
            else if (current is HttpRequestException httpRequestException)
            {
                statusCode ??= (int?)httpRequestException.StatusCode;
                isHttpFailure = true;
            }

            isProtocolError |= current is JsonException;
            isTimeout |= current is TimeoutException
                or TaskCanceledException { CancellationToken.IsCancellationRequested: false };
        }

        return new FailureMetadata(
            statusCode,
            retryAfter,
            requestId,
            providerCode,
            isHttpFailure,
            isProtocolError,
            isTimeout);
    }

    private static string GetProviderCode(
        int? statusCode,
        string? providerCode,
        Exception exception) =>
        providerCode
        ?? statusCode?.ToString(CultureInfo.InvariantCulture)
        ?? exception.GetType().Name;

    /// <summary>
    /// 构造 ProviderDetails。
    /// 异常类型链总会写入，使「没有 HTTP 状态码」的协议类失败也能区分具体原因。
    /// </summary>
    private static JsonElement? CreateDetails(
        int? statusCode,
        string? retryAfter,
        string? requestId,
        string? providerCode,
        Exception exception,
        ProviderFailureContext? failureContext)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (statusCode is not null)
            {
                writer.WriteNumber("statusCode", statusCode.Value);
            }

            if (retryAfter is not null)
            {
                writer.WriteString("retryAfter", retryAfter);
            }

            if (requestId is not null)
            {
                writer.WriteString("requestId", requestId);
            }

            if (providerCode is not null)
            {
                writer.WriteString("providerCode", providerCode);
            }

            // 只记录类型名，不记录消息：类型名足以区分 JsonException、IOException、
            // HttpRequestException 等原因，且不会带出响应正文或凭据。
            writer.WriteStartArray("exceptionTypes");
            var depth = 0;
            for (var current = exception;
                 current is not null && depth < MaximumExceptionTypeDepth;
                 current = current.InnerException, depth++)
            {
                writer.WriteStringValue(current.GetType().FullName ?? current.GetType().Name);
            }

            writer.WriteEndArray();

            if (failureContext is not null)
            {
                writer.WriteString("stage", failureContext.Stage);
                writer.WriteString("responseMode", failureContext.NonStreaming ? "non_streaming" : "streaming");
                writer.WriteNumber("receivedUpdates", failureContext.ReceivedUpdates);
                writer.WriteNumber("elapsedMs", failureContext.ElapsedMilliseconds);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private sealed record FailureMetadata(
        int? StatusCode,
        string? RetryAfter,
        string? RequestId,
        string? ProviderCode,
        bool IsHttpFailure,
        bool IsProtocolError,
        bool IsTimeout);
}
