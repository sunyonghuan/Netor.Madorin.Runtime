using System.Text.Json;

using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.MConsole.Callbacks;

public sealed record StoredToolCallResult(
    string RuntimeInstanceId,
    string CallId,
    ToolCallStatus Status,
    JsonElement? Result,
    string? ResultHash,
    BlobReference? ResultBlob,
    string? ErrorCode,
    string? ErrorMessage,
    string? DiagnosticId,
    DateTimeOffset CompletedAt)
{
    public ToolCallResponse ToCallResponse(string correlationId) => new(
        correlationId,
        CallId,
        Status,
        Result,
        ResultHash,
        ResultBlob,
        ErrorCode,
        ErrorMessage,
        DiagnosticId);

    public ToolResultQueryResponse ToQueryResponse(string correlationId) => new(
        correlationId,
        CallId,
        Status,
        Result,
        ResultHash,
        ResultBlob,
        ErrorCode,
        ErrorMessage);

    public static StoredToolCallResult FromResponse(
        string runtimeInstanceId,
        ToolCallResponse response) => new(
        runtimeInstanceId,
        response.CallId,
        response.Status,
        response.Result?.Clone(),
        response.ResultHash,
        response.ResultBlob,
        response.ErrorCode,
        response.ErrorMessage,
        response.DiagnosticId,
        DateTimeOffset.UtcNow);
}
