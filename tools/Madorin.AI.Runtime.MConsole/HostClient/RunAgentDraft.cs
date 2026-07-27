namespace Madorin.AI.Runtime.MConsole.HostClient;

public sealed record RunAgentDraft(
    string AgentId,
    string ProviderId,
    string ModelId,
    string SystemPrompt = "MConsole validation agent.",
    string PromptTemplateVersion = "1.0");
