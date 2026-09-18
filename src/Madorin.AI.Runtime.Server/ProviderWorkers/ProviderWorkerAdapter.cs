using System.Runtime.CompilerServices;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Server.ProviderWorkers;

public sealed class ProviderWorkerAdapter(
    ProviderWorkerProcess process,
    ProviderConnectionConfig connection) : IRuntimeProviderAdapter, IRuntimeProviderModelCatalogSource
{
    private readonly ProviderWorkerProcess _process =
        process ?? throw new ArgumentNullException(nameof(process));
    private readonly ProviderConnectionConfig _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    public string ProviderId => _connection.ProviderId;

    public ProviderCapabilities Capabilities =>
        _connection.Capabilities ?? new ProviderCapabilities();

    public IReadOnlyList<ProviderModel> Models =>
        _connection.Models is null || _connection.Models.Count == 0
            ? []
            : _connection.Models.Select(static id => new ProviderModel(id, id)).ToList();

    public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
        RuntimeProviderRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await foreach (var providerEvent in _process
            .InvokeAsync(_connection, request, ct)
            .ConfigureAwait(false))
        {
            yield return providerEvent;
        }
    }

    public async ValueTask<ProviderCapabilityProbeResult> ProbeCapabilitiesAsync(
        CancellationToken ct = default)
    {
        await _process.EnsureReadyAsync(ct).ConfigureAwait(false);
        return ProviderCapabilityProbe.FromDeclared(
            Capabilities,
            _connection.ConfigurationVersion ?? "1");
    }

    public async ValueTask<IReadOnlyList<ProviderModel>> FetchModelsAsync(
        CancellationToken ct = default)
    {
        await _process.EnsureReadyAsync(ct).ConfigureAwait(false);
        return Models;
    }

    public Task ValidateCapabilitiesAsync(ContentBlock[] input, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(input);
        return Task.CompletedTask;
    }
}
