using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.MConsole.HostClient;

public sealed record RunSubmission(
    string RunId,
    string? SessionId,
    RuntimeMode Mode,
    string RuntimeInstanceId,
    bool IsExistingSession,
    DateTimeOffset SubmittedAt);
