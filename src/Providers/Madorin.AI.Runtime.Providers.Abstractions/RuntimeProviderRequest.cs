using System.Text.Json;
using System.Text.Json.Serialization;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Providers.Abstractions;

#pragma warning disable CA1068 // CancellationToken remains last for source compatibility; Skills is an ignored context value.
public sealed record RuntimeProviderRequest(
    string InvocationId,
    string AgentId,
    string ProviderId,
    string ModelId,
    RuntimeProviderMessage[] Messages,
    ToolDescriptor[]? Tools = null,
    float? Temperature = null,
    int? MaxTokens = null,
    RuntimeStructuredOutput? StructuredOutput = null,
    string? InternalRequestId = null,
    int AttemptNumber = 0,
    bool IsIdempotent = true,
    bool HasIrreversibleToolSideEffects = false,
    [property: JsonIgnore] CancellationToken CancellationToken = default,
    [property: JsonIgnore] SkillsInvocationContext? Skills = null,
    ExpertRequestControl? RequestControl = null)
{
    /// <summary>仅由 Runtime 注入的受控 Blob 读取入口，不跨 JSON 传递委托或磁盘路径。</summary>
    [JsonIgnore]
    public Func<BlobReference, CancellationToken, Task<Stream>>? OpenBlobRead { get; init; }

    /// <summary>Invocation-scoped output writer; stream ownership remains with the caller.</summary>
    [JsonIgnore]
    public Func<Stream, string, long, string, CancellationToken, Task<BlobReference>>? StoreOutputBlob { get; init; }

    /// <summary>SDK 需要完整缓冲时的单次调用附件总字节上限。</summary>
    [JsonIgnore]
    public int MaximumBufferedBlobBytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>Gets the number of leading messages that belong to canonical history.</summary>
    [JsonIgnore]
    public int HistoryMessageCount { get; init; }

    /// <summary>
    /// Resolved streaming preference for this provider request.
    /// True (default) = streaming responses are preferred.
    /// False = the caller prefers a single non-streaming response for this generation step.
    /// This is a hint; the orchestrator may still consume via streaming boundary for eventing.
    /// </summary>
    [JsonIgnore]
    public bool Stream => RequestControl?.Stream ?? true;

    public RuntimeProviderRequest CreateAttempt(int attemptNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attemptNumber);

        if (attemptNumber > 0 && (!IsIdempotent || HasIrreversibleToolSideEffects))
        {
            throw new InvalidOperationException(
                "The Provider request cannot be retried after irreversible or non-idempotent work.");
        }

        return this with
        {
            InternalRequestId = Guid.NewGuid().ToString("N"),
            AttemptNumber = attemptNumber
        };
    }
}
#pragma warning restore CA1068

/// <summary>Describes a provider-independent JSON response schema.</summary>
public sealed record RuntimeStructuredOutput(
    string Name,
    JsonElement Schema,
    string? Description = null);
