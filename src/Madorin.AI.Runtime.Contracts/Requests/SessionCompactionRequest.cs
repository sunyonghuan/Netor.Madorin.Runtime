namespace Madorin.AI.Runtime.Contracts;

/// <summary>Requests a rebuildable projection for a persisted Runtime Session.</summary>
public sealed record SessionCompactionRequest(
    string SessionId,
    string Strategy = "summary",
    string? ProviderId = null,
    string? ModelId = null,
    int? KeepLastTokens = null,
    bool DryRun = false,
    bool Force = false,
    string? SelectionSource = null);