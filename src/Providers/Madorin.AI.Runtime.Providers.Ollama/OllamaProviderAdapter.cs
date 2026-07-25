using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.OpenAICompatible;

namespace Madorin.AI.Runtime.Providers.Ollama;

public sealed class OllamaProviderAdapter :
    IRuntimeProviderAdapter,
    IRuntimeProviderModelCatalogSource,
    IRuntimeProviderCredentialUpdater,
    IDisposable
{
    private readonly string _configurationVersion;
    private readonly OpenAICompatibleProviderAdapter _innerAdapter;
    private readonly OllamaModelCatalogSource _modelCatalogSource;

    public OllamaProviderAdapter(
        OllamaProviderOptions options,
        IHttpClientFactory? httpClientFactory = null,
        IProviderBlobResolver? blobResolver = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BaseUrl);
        if (options.AuthenticationMode is ProviderAuthenticationMode.Required)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);
        }

        var factory = httpClientFactory ?? ProviderHttpClientFactory.Shared;
        var capabilities = options.Capabilities ?? new ProviderCapabilities(Streaming: true);
        var models = options.Models
            ?? [new ProviderModel(OllamaModelCatalog.DefaultModelId, "Llama 3.2")];
        var compatibleOptions = new OpenAICompatibleProviderOptions(
            options.ProviderId,
            options.BaseUrl,
            options.ApiKey,
            options.AuthenticationHeader,
            options.AuthenticationScheme,
            capabilities,
            models,
            options.ConfigurationVersion,
            ProbePath: null,
            AuthenticationMode: options.AuthenticationMode);
        _innerAdapter = new OpenAICompatibleProviderAdapter(
            compatibleOptions,
            factory.CreateClient($"{options.ProviderId}.chat"),
            disposeHttpClient: true,
            blobResolver);
        _modelCatalogSource = new OllamaModelCatalogSource(
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
        await _modelCatalogSource.FetchModelsAsync(Models, ct).ConfigureAwait(false);
        return new ProviderCapabilityProbeResult(
            Capabilities,
            DateTimeOffset.UtcNow,
            _configurationVersion,
            "ollama-tags",
            ProviderCapabilityProbe.GetMissingCapabilities(Capabilities));
    }

    public ValueTask<IReadOnlyList<ProviderModel>> FetchModelsAsync(
        CancellationToken ct = default) =>
        _modelCatalogSource.FetchModelsAsync(Models, ct);

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
        _modelCatalogSource.UpdateCredentials(parameters.Credential);
    }

    public void Dispose()
    {
        _modelCatalogSource.Dispose();
        _innerAdapter.Dispose();
    }
}
