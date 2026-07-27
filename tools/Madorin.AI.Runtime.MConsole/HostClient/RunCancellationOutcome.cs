using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.MConsole.HostClient;

public sealed record RunCancellationOutcome(
    string RunId,
    bool Accepted,
    RunStatus? ObservedStatus,
    bool IsTerminal);
