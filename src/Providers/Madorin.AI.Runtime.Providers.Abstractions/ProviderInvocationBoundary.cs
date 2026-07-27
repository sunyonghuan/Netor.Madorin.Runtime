using System.Runtime.CompilerServices;

namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>Applies request compatibility rules before entering a Provider adapter.</summary>
public static class ProviderInvocationBoundary
{
    /// <summary>Projects and validates a request before streaming it through an adapter.</summary>
    public static async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
        IRuntimeProviderAdapter adapter,
        RuntimeProviderRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var projection = ProviderRequestCompatibilityProjector.ProjectWithDiagnostics(
            request,
            adapter);

        await foreach (var providerEvent in adapter.CompleteStreamingAsync(
                projection.Request,
                ct)
            .ConfigureAwait(false))
        {
            yield return providerEvent;
        }
    }
}
