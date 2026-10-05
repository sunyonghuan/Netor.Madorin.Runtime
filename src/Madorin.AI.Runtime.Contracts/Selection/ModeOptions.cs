using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Contracts;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "mode")]
[JsonDerivedType(typeof(ExpertModeOptions), "expert")]
public abstract record ModeOptions;

/// <summary>Expert context-window and optional summary policy.</summary>
public sealed record ExpertContextPolicy(
    string Strategy = "TailWindow",
    int? MaxTokens = null,
    int? TailMessageCount = null,
    bool PreserveToolResults = true,
    bool PreserveReasoning = false,
    string? SummaryProviderId = null,
    string? SummaryModelId = null,
    bool SummaryRequired = false);

/// <summary>
/// Expert mode configuration with a primary agent and optional sub-agents for delegation.
/// </summary>
public sealed record ExpertModeOptions : ModeOptions
{
    /// <summary>The primary expert agent.</summary>
    public AgentRef Agent { get; init; }

    /// <summary>
    /// Optional pool of sub-agents available for @agentName delegation.
    /// When null or empty, delegation is disabled and Expert mode runs as a single agent.
    /// </summary>
    public AgentRef[]? AvailableAgents { get; init; }

    public ExpertContextPolicy? ContextPolicy { get; init; }

    /// <summary>
    /// Fine-grained request control parameters. When set, these override agent-level defaults
    /// and provider defaults for sampling, output, tool use, and filtering.
    /// </summary>
    public ExpertRequestControl? RequestControl { get; init; }

    [JsonConstructor]
    public ExpertModeOptions(
        AgentRef agent,
        AgentRef[]? availableAgents = null,
        ExpertContextPolicy? contextPolicy = null,
        ExpertRequestControl? requestControl = null)
    {
        Agent = agent;
        AvailableAgents = availableAgents;
        ContextPolicy = contextPolicy;
        RequestControl = requestControl;
    }
}

