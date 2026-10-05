namespace Madorin.AI.Runtime.Contracts.Http;

/// <summary>
/// HTTP endpoint response for synchronous non-streaming run.
/// Contains the final output and metadata.
/// </summary>
public sealed record HttpRunResponse
{
    /// <summary>
    /// Run ID for this execution.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>
    /// Session ID (new or existing).
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Final text output from the model.
    /// </summary>
    public required string Output { get; init; }

    /// <summary>
    /// Run status (Completed, Failed, etc.).
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Error message if Status is Failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Total execution time in milliseconds.
    /// </summary>
    public long ElapsedMs { get; init; }

    /// <summary>
    /// Input token count (if available from provider).
    /// </summary>
    public int? InputTokens { get; init; }

    /// <summary>
    /// Output token count (if available from provider).
    /// </summary>
    public int? OutputTokens { get; init; }
}
