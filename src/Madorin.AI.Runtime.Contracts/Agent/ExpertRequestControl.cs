namespace Madorin.AI.Runtime.Contracts;

/// <summary>
/// Fine-grained control parameters for Expert mode requests, enabling upper-layer orchestration
/// frameworks to precisely control AI behavior without modifying agent prompts or system configuration.
/// </summary>
public sealed record ExpertRequestControl
{
    /// <summary>
    /// Maximum tokens the model should generate in the response.
    /// Null = provider default. Overrides any agent-level setting.
    /// Valid range: 1 to provider maximum (typically 4096-200000).
    /// </summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// Temperature for sampling (0.0-2.0). Lower = more deterministic, higher = more creative.
    /// Null = provider default (typically 1.0).
    /// Valid range: 0.0-2.0 (some providers support up to 2.0, others cap at 1.0).
    /// </summary>
    public double? Temperature { get; init; }

    /// <summary>
    /// Top-p nucleus sampling threshold (0.0-1.0).
    /// Only consider tokens in the top p cumulative probability mass.
    /// Null = provider default (typically 1.0 = disabled).
    /// Valid range: 0.0-1.0 (exclusive of 0.0, inclusive of 1.0).
    /// </summary>
    public double? TopP { get; init; }

    /// <summary>
    /// Top-k sampling: only consider the k most likely tokens.
    /// Null = provider default (provider-specific, often disabled).
    /// Valid range: 1-500 (provider-dependent, some support higher).
    /// </summary>
    public int? TopK { get; init; }

    /// <summary>
    /// Stop sequences: the model will stop generating when it encounters any of these strings.
    /// Null or empty = provider default stop sequences only.
    /// Maximum count: typically 4-16 sequences, provider-dependent.
    /// Maximum length per sequence: typically 64-256 characters.
    /// </summary>
    public string[]? StopSequences { get; init; }

    /// <summary>
    /// Tool use mode override for this request.
    /// - <c>auto</c>: model decides when to use tools (default)
    /// - <c>any</c>: model must use at least one tool
    /// - <c>none</c>: disable all tools for this request
    /// - <c>required:[toolName]</c>: force specific tool usage
    /// Null = agent's default tool policy.
    /// </summary>
    public string? ToolUseMode { get; init; }

    /// <summary>
    /// Content filtering policy for this request.
    /// - <c>default</c>: use provider's standard filtering
    /// - <c>strict</c>: apply stricter content filtering
    /// - <c>permissive</c>: relax content filtering (if provider supports)
    /// Null = provider default.
    /// </summary>
    public string? ContentFilter { get; init; }

    /// <summary>
    /// Response format constraint.
    /// - <c>text</c>: plain text response (default)
    /// - <c>json</c>: response must be valid JSON
    /// - <c>json_schema:{schemaName}</c>: response must match named schema
    /// Null = text.
    /// </summary>
    public string? ResponseFormat { get; init; }

    /// <summary>
    /// Metadata tags attached to this request for tracking/analytics.
    /// Not sent to the model. Stored in run metadata and event logs.
    /// </summary>
    public Dictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Stream response in real-time chunks (true) or wait for complete response (false).
    /// Null = runtime default (true).
    /// </summary>
    public bool? Stream { get; init; }

    /// <summary>
    /// Enable thinking/reasoning tags in the response.
    /// When true, model may emit &lt;thinking&gt; tags with internal reasoning.
    /// Null = provider default (typically false).
    /// </summary>
    public bool? EnableThinking { get; init; }

    /// <summary>
    /// Presence penalty (-2.0 to 2.0): penalize tokens that have appeared in the conversation.
    /// Positive = encourage new topics, negative = allow repetition.
    /// Null = provider default (typically 0).
    /// Valid range: -2.0 to 2.0.
    /// </summary>
    public double? PresencePenalty { get; init; }

    /// <summary>
    /// Frequency penalty (-2.0 to 2.0): penalize tokens based on their frequency in the conversation.
    /// Positive = reduce repetition, negative = allow repetition.
    /// Null = provider default (typically 0).
    /// Valid range: -2.0 to 2.0.
    /// </summary>
    public double? FrequencyPenalty { get; init; }

    /// <summary>
    /// Random seed for deterministic sampling (when the provider supports it).
    /// Null = non-deterministic (default).
    /// </summary>
    public int? Seed { get; init; }

    /// <summary>
    /// Request timeout in milliseconds. Overrides runtime default.
    /// Null = runtime default timeout.
    /// Valid range: 1000-600000 (1s-10min, provider-dependent).
    /// </summary>
    public int? TimeoutMs { get; init; }

    /// <summary>
    /// Retry policy override for this request.
    /// - <c>default</c>: use runtime default retry policy
    /// - <c>none</c>: no retries on failure
    /// - <c>aggressive</c>: retry more aggressively
    /// Null = runtime default.
    /// </summary>
    public string? RetryPolicy { get; init; }

    /// <summary>
    /// Validates all parameters against provider constraints and throws ArgumentException if invalid.
    /// Call this before passing to RuntimeProviderRequest to fail fast with clear error messages.
    /// </summary>
    public void Validate()
    {
        if (MaxTokens.HasValue && MaxTokens.Value < 1)
        {
            throw new ArgumentException($"MaxTokens must be >= 1, got {MaxTokens.Value}", nameof(MaxTokens));
        }

        if (Temperature.HasValue && (!double.IsFinite(Temperature.Value) || Temperature.Value < 0.0 || Temperature.Value > 2.0))
        {
            throw new ArgumentException($"Temperature must be in [0.0, 2.0], got {Temperature.Value}", nameof(Temperature));
        }

        if (TopP.HasValue && (!double.IsFinite(TopP.Value) || TopP.Value <= 0.0 || TopP.Value > 1.0))
        {
            throw new ArgumentException($"TopP must be in (0.0, 1.0], got {TopP.Value}", nameof(TopP));
        }

        if (TopK.HasValue && TopK.Value < 1)
        {
            throw new ArgumentException($"TopK must be >= 1, got {TopK.Value}", nameof(TopK));
        }

        if (StopSequences is { Length: > 0 })
        {
            for (int i = 0; i < StopSequences.Length; i++)
            {
                if (string.IsNullOrEmpty(StopSequences[i]))
                {
                    throw new ArgumentException($"StopSequences[{i}] cannot be null or empty", nameof(StopSequences));
                }
            }
        }

        if (PresencePenalty.HasValue && (!double.IsFinite(PresencePenalty.Value) || PresencePenalty.Value < -2.0 || PresencePenalty.Value > 2.0))
        {
            throw new ArgumentException($"PresencePenalty must be in [-2.0, 2.0], got {PresencePenalty.Value}", nameof(PresencePenalty));
        }

        if (FrequencyPenalty.HasValue && (!double.IsFinite(FrequencyPenalty.Value) || FrequencyPenalty.Value < -2.0 || FrequencyPenalty.Value > 2.0))
        {
            throw new ArgumentException($"FrequencyPenalty must be in [-2.0, 2.0], got {FrequencyPenalty.Value}", nameof(FrequencyPenalty));
        }

        if (TimeoutMs.HasValue && TimeoutMs.Value < 1000)
        {
            throw new ArgumentException($"TimeoutMs must be >= 1000, got {TimeoutMs.Value}", nameof(TimeoutMs));
        }
    }
}
