using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Cli.Commands;

internal sealed record ReplWelcomeSnapshot(
    string Version,
    RuntimeMode Mode,
    string AgentId,
    string? AgentName,
    string Provider,
    string Model,
    string Workspace);
