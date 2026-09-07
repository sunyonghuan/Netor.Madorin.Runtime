namespace Madorin.AI.Runtime.Contracts;

public sealed record AgentRef(
    string AgentId,
    string PromptTemplateVersion,
    string SystemPrompt,
    string? ProviderId = null,
    string? ModelId = null,
    string[]? AllowedToolIds = null,
    string[]? SkillIds = null,
    /// <summary>
    /// Host-mode skill discovery strategy: <c>progressive</c> (default) or <c>direct</c>.
    /// Null / unknown values resolve to progressive. Independent mode ignores this field.
    /// </summary>
    string? SkillDiscoveryMode = null);
