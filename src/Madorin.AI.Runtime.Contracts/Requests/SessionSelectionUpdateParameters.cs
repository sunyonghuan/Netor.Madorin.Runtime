namespace Madorin.AI.Runtime.Contracts;

/// <summary>
/// Selection update for a session.
/// </summary>
public sealed record SessionSelectionUpdateParameters(
    string SessionId,
    int ExpectedSelectionVersion,
    NextTurnSelection Selection,
    IReadOnlyList<HostProviderConfig>? ProviderConfigs = null);
