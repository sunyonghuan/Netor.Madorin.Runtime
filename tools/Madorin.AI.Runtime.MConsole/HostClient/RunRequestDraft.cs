using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.MConsole.HostClient;

public sealed record RunRequestDraft(
    RuntimeMode Mode,
    string Input,
    RunAgentDraft PrimaryAgent,
    IReadOnlyList<RunAgentDraft>? AdditionalAgents = null,
    string? WorkspaceId = null,
    string? SessionInstructions = null,
    int SelectionVersion = 1,
    string ToolCatalogVersion = "0");
