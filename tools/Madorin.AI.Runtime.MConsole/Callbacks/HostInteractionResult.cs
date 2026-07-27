namespace Madorin.AI.Runtime.MConsole.Callbacks;

public sealed record HostInteractionResult(
    string CallbackId,
    HostCallbackKind Kind,
    string RuntimeInstanceId,
    string? RunId,
    string? CallId,
    string Outcome,
    string? Detail,
    bool WasManual,
    DateTimeOffset ArrivedAt,
    DateTimeOffset CompletedAt)
{
    public TimeSpan Duration => CompletedAt - ArrivedAt;
}
