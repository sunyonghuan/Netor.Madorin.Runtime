namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>Optionally discovers a Provider's current remote model catalog.</summary>
public interface IRuntimeProviderModelCatalogSource
{
    /// <summary>Fetches a bounded, validated model snapshot without changing active invocations.</summary>
    public ValueTask<IReadOnlyList<ProviderModel>> FetchModelsAsync(
        CancellationToken ct = default);
}
