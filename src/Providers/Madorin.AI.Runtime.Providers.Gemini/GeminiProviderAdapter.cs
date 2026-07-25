using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.OpenAICompatible;

namespace Madorin.AI.Runtime.Providers.Gemini;

public sealed class GeminiProviderAdapter :
    IRuntimeProviderAdapter,
    IRuntimeProviderModelCatalogSource,
    IRuntimeProviderCredentialUpdater,
    IDisposable
{
    private readonly GeminiModelCatalogClient _modelCatalogClient;
    private readonly OpenAICompatibleProviderAdapter _innerAdapter;

    public GeminiProviderAdapter(
        GeminiProviderOptions options,
        IHttpClientFactory? httpClientFactory = null,
        IProviderBlobResolver? blobResolver = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BaseUrl);
        var factory = httpClientFactory ?? ProviderHttpClientFactory.Shared;
        var capabilities = options.Capabilities ?? new ProviderCapabilities(Streaming: true);
        var models = options.Models ?? GeminiModelCatalog.All;
        var compatibleOptions = new OpenAICompatibleProviderOptions(
            options.ProviderId,
            options.BaseUrl,
            options.ApiKey,
            Capabilities: capabilities,
            Models: models,
            ConfigurationVersion: options.ConfigurationVersion,
            ProbePath: null,
            MaximumProbeResponseBytes: options.MaximumModelCatalogResponseBytes);
        _innerAdapter = new OpenAICompatibleProviderAdapter(
            compatibleOptions,
            factory.CreateClient($"{options.ProviderId}.chat"),
            disposeHttpClient: true,
            blobResolver);
        _modelCatalogClient = new GeminiModelCatalogClient(
            options,
            factory.CreateClient($"{options.ProviderId}.models"),
            disposeHttpClient: true);
    }

    public string ProviderId => _innerAdapter.ProviderId;

    public ProviderCapabilities Capabilities => _innerAdapter.Capabilities;

    public IReadOnlyList<ProviderModel> Models => _innerAdapter.Models;

    public ProviderConfigurationSchema ConfigurationSchema => _innerAdapter.ConfigurationSchema;

    public ValueTask<ProviderTokenEstimate> EstimateTokensAsync(
        RuntimeProviderRequest request,
        CancellationToken ct = default) =>
        ((IRuntimeProviderAdapter)_innerAdapter).EstimateTokensAsync(request, ct);

    public ValueTask<ProviderCapabilityProbeResult> ProbeCapabilitiesAsync(
        CancellationToken ct = default) =>
        _innerAdapter.ProbeCapabilitiesAsync(ct);

    public ValueTask<IReadOnlyList<ProviderModel>> FetchModelsAsync(
        CancellationToken ct = default) =>
        _modelCatalogClient.FetchModelsAsync(Models, ct);

    public Task ValidateCapabilitiesAsync(ContentBlock[] input, CancellationToken ct = default) =>
        _innerAdapter.ValidateCapabilitiesAsync(input, ct);

    public IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
        RuntimeProviderRequest request,
        CancellationToken ct = default) =>
        _innerAdapter.CompleteStreamingAsync(request, ct);

    public async Task UpdateCredentialsAsync(
        CredentialsUpdateParameters parameters,
        CancellationToken ct = default)
    {
        await _innerAdapter.UpdateCredentialsAsync(parameters, ct).ConfigureAwait(false);
        _modelCatalogClient.UpdateCredentials(parameters.Credential);
    }

    public void Dispose()
    {
        _modelCatalogClient.Dispose();
        _innerAdapter.Dispose();
    }
}
