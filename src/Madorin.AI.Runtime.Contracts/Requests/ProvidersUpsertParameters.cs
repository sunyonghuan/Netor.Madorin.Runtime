namespace Madorin.AI.Runtime.Contracts;

/// <summary>Optional instance-level provider pre-registration for host mode.</summary>
public sealed record ProvidersUpsertParameters(
    IReadOnlyList<HostProviderConfig> Providers);
