using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.OpenAICompatible;

namespace Madorin.AI.Runtime.Providers.Aliyun;

public sealed class AliyunProviderAdapter :
    IRuntimeProviderAdapter,
    IRuntimeProviderModelCatalogSource,
    IRuntimeProviderCredentialUpdater,
    IDisposable
{
    private readonly string _configurationVersion;
    private readonly OpenAICompatibleProviderAdapter _innerAdapter;
    private readonly AliyunModelCatalogClient _modelCatalogClient;

    public AliyunProviderAdapter(
        AliyunProviderOptions options,
        IHttpClientFactory? httpClientFactory = null,
        IProviderBlobResolver? blobResolver = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BaseUrl);

        var factory = httpClientFactory ?? ProviderHttpClientFactory.Shared;
        var capabilities = options.Capabilities ?? new ProviderCapabilities(Streaming: true);
        var models = options.Models ?? AliyunModelCatalog.All;
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
        _modelCatalogClient = new AliyunModelCatalogClient(
            options,
            factory.CreateClient($"{options.ProviderId}.models"),
            disposeHttpClient: true);
        _configurationVersion = options.ConfigurationVersion;
    }

    public string ProviderId => _innerAdapter.ProviderId;

    public ProviderCapabilities Capabilities => _innerAdapter.Capabilities;

    public IReadOnlyList<ProviderModel> Models => _innerAdapter.Models;

    public ProviderConfigurationSchema ConfigurationSchema => _innerAdapter.ConfigurationSchema;

    public ValueTask<ProviderTokenEstimate> EstimateTokensAsync(
        RuntimeProviderRequest request,
        CancellationToken ct = default) =>
        ((IRuntimeProviderAdapter)_innerAdapter).EstimateTokensAsync(request, ct);

    public async ValueTask<ProviderCapabilityProbeResult> ProbeCapabilitiesAsync(
        CancellationToken ct = default)
    {
        await _modelCatalogClient.FetchModelsAsync(Models, ct).ConfigureAwait(false);
        return new ProviderCapabilityProbeResult(
            Capabilities,
            DateTimeOffset.UtcNow,
            _configurationVersion,
            "aliyun-models",
            ProviderCapabilityProbe.GetMissingCapabilities(Capabilities));
    }

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
