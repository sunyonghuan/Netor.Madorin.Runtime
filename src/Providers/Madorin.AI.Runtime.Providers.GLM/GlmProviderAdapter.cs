using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.OpenAICompatible;

namespace Madorin.AI.Runtime.Providers.GLM;

public sealed class GlmProviderAdapter :
    IRuntimeProviderAdapter,
    IRuntimeProviderModelCatalogSource,
    IRuntimeProviderCredentialUpdater,
    IDisposable
{
    private readonly OpenAICompatibleProviderAdapter _innerAdapter;

    public GlmProviderAdapter(
        GlmProviderOptions options,
        IHttpClientFactory? httpClientFactory = null,
        IProviderBlobResolver? blobResolver = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BaseUrl);
        var factory = httpClientFactory ?? ProviderHttpClientFactory.Shared;
        var compatibleOptions = new OpenAICompatibleProviderOptions(
            options.ProviderId,
            options.BaseUrl,
            options.ApiKey,
            Capabilities: options.Capabilities ?? new ProviderCapabilities(Streaming: true),
            Models: options.Models ?? GlmModelCatalog.All,
            ConfigurationVersion: options.ConfigurationVersion,
            ProbePath: options.ProbePath,
            MaximumProbeResponseBytes: options.MaximumProbeResponseBytes);
        _innerAdapter = new OpenAICompatibleProviderAdapter(
            compatibleOptions,
            factory.CreateClient(options.ProviderId),
            disposeHttpClient: true,
            blobResolver);
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
        _innerAdapter.FetchModelsAsync(ct);

    public Task ValidateCapabilitiesAsync(ContentBlock[] input, CancellationToken ct = default) =>
        _innerAdapter.ValidateCapabilitiesAsync(input, ct);

    public IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
        RuntimeProviderRequest request,
        CancellationToken ct = default) =>
        _innerAdapter.CompleteStreamingAsync(request, ct);

    // === RequestControl mapping (GLM / Zhipu AI) ===
    // This adapter uses the OpenAI-compatible endpoint.
    // All sampling parameters flow through OpenAIProviderAdapter + MafSkillsAgentFactory.ApplyRequestControl.
    //
    // Latest official docs (as of 2025/2026):
    // - GLM OpenAI-compatible chat completions:
    //   https://open.bigmodel.cn/dev/api#http
    //   https://open.bigmodel.cn/dev/howuse/introduction
    //
    // Supported (glm-4* series):
    //   temperature, top_p, max_tokens, stop (array), stream
    // Limited / model-dependent: seed, frequency_penalty, presence_penalty.
    // TopK is generally not exposed in the OpenAI-compatible interface.

    public Task UpdateCredentialsAsync(
        CredentialsUpdateParameters parameters,
        CancellationToken ct = default) =>
        _innerAdapter.UpdateCredentialsAsync(parameters, ct);

    public void Dispose()
    {
        _innerAdapter.Dispose();
    }
}
