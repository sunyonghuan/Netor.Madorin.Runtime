namespace Madorin.AI.Runtime.Contracts.Http;

/// <summary>
/// HTTP endpoint request for synchronous non-streaming run.
/// Simplified version of NewSessionRunRequest for lightweight integration.
/// </summary>
public sealed record HttpRunRequest
{
    /// <summary>
    /// Provider ID to use for this run. If not specified, uses default from server config.
    /// </summary>
    public string? ProviderId { get; init; }

    /// <summary>
    /// Model ID to use for this run. Required.
    /// </summary>
    public required string ModelId { get; init; }

    /// <summary>
    /// User input text. Required.
    /// </summary>
    public required string Input { get; init; }

    /// <summary>
    /// Optional system prompt override for the agent.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Optional request control parameters (temperature, max tokens, etc.).
    /// </summary>
    public ExpertRequestControl? RequestControl { get; init; }

    /// <summary>
    /// Optional session ID to continue an existing session.
    /// If not provided, creates a new session.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Optional language code (e.g., "zh-CN", "en-US").
    /// If not specified, uses system default.
    /// </summary>
    public string? Language { get; init; }
}
