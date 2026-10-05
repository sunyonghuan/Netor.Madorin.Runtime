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
    [property: JsonIgnore] ExpertRequestControl? RequestControl = null)
{
    /// <summary>Gets the number of leading messages that belong to canonical history.</summary>
    [JsonIgnore]
    public int HistoryMessageCount { get; init; }

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
