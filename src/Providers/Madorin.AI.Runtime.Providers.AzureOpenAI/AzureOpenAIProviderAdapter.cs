using System.Runtime.CompilerServices;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.OpenAICompatible;

namespace Madorin.AI.Runtime.Providers.AzureOpenAI;

public sealed class AzureOpenAIProviderAdapter :
    IRuntimeProviderAdapter,
    IRuntimeProviderModelCatalogSource,
    IRuntimeProviderCredentialUpdater,
    IDisposable
{
    private readonly Dictionary<string, OpenAICompatibleProviderAdapter> _adapters =
        new(StringComparer.Ordinal);
    private readonly object _adapterGate = new();
    private readonly string _apiVersion;
    private readonly IProviderBlobResolver? _blobResolver;
    private readonly string _configurationVersion;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly int _maximumErrorResponseBytes;
    private readonly Uri _resourceEndpoint;
    private string _apiKey;
    private bool _isDisposed;

    public AzureOpenAIProviderAdapter(
        AzureOpenAIProviderOptions options,
        IHttpClientFactory? httpClientFactory = null,
        IProviderBlobResolver? blobResolver = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConfigurationVersion);
        ArgumentNullException.ThrowIfNull(options.Models);
        if (options.Models.Count == 0)
        {
            throw new ArgumentException(
                "At least one Azure OpenAI deployment must be configured.",
                nameof(options));
        }

        if (options.MaximumErrorResponseBytes is <= 0
            or > ProviderHttpContentReader.MaximumAllowedResponseBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaximumErrorResponseBytes,
                "Azure OpenAI error responses must be limited to between 1 byte and 1 MiB.");
        }

        foreach (var model in options.Models)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(model.Id);
            if (model.ProviderModelId is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(model.ProviderModelId);
            }
        }

        _resourceEndpoint = ParseResourceEndpoint(options.BaseUrl);
        _apiKey = options.ApiKey;
        _apiVersion = ValidateApiVersion(options.ApiVersion);
        _configurationVersion = options.ConfigurationVersion;
        _maximumErrorResponseBytes = options.MaximumErrorResponseBytes;
        _httpClientFactory = httpClientFactory ?? ProviderHttpClientFactory.Shared;
        _blobResolver = blobResolver;
        ProviderId = options.ProviderId;
        Capabilities = options.Capabilities ?? new ProviderCapabilities(Streaming: true);
        Models = options.Models.ToArray();
    }

    public string ProviderId { get; }

    public ProviderCapabilities Capabilities { get; }

    public IReadOnlyList<ProviderModel> Models { get; }

    public ProviderConfigurationSchema ConfigurationSchema => new(
        ProviderId,
        _configurationVersion,
        [
            new("apiKey", ProviderConfigurationValueKind.Secret, IsRequired: true),
            new("baseUrl", ProviderConfigurationValueKind.Uri, IsRequired: true),
            new("apiVersion", ProviderConfigurationValueKind.Text, IsRequired: true),
            new("models", ProviderConfigurationValueKind.TextArray, IsRequired: true)
        ]);

    public ValueTask<ProviderTokenEstimate> EstimateTokensAsync(
        RuntimeProviderRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ProviderTokenEstimator.Estimate(request));
    }

    public ValueTask<ProviderCapabilityProbeResult> ProbeCapabilitiesAsync(
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            ProviderCapabilityProbe.FromDeclared(Capabilities, _configurationVersion));
    }

    public ValueTask<IReadOnlyList<ProviderModel>> FetchModelsAsync(
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Models);
    }

    public Task ValidateCapabilitiesAsync(ContentBlock[] input, CancellationToken ct = default) =>
        ProviderCapabilityValidator.ValidateAsync(Capabilities, input, ct);

    public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
        RuntimeProviderRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var deploymentName = ResolveDeploymentName(request.ModelId);
        if (deploymentName is null)
        {
            yield return new InvocationFailedProviderEvent(
                request.InvocationId,
                CreateDeploymentNotConfiguredError());
            yield break;
        }

        var adapter = GetOrCreateAdapter(deploymentName);
        await foreach (var providerEvent in adapter.CompleteStreamingAsync(request, ct)
                           .ConfigureAwait(false))
        {
            yield return providerEvent;
        }
    }

    public async Task UpdateCredentialsAsync(
        CredentialsUpdateParameters parameters,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameters.Credential);
        ct.ThrowIfCancellationRequested();
        if (!string.Equals(parameters.ProviderId, ProviderId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Credential update targets another Provider.",
                nameof(parameters));
        }

        OpenAICompatibleProviderAdapter[] adapters;
        lock (_adapterGate)
        {
            ThrowIfDisposed();
            _apiKey = parameters.Credential;
            adapters = _adapters.Values.ToArray();
        }

        foreach (var adapter in adapters)
        {
            await adapter.UpdateCredentialsAsync(parameters, ct).ConfigureAwait(false);
        }
    }

    // === RequestControl note (Azure OpenAI) ===
    // This adapter ultimately delegates to OpenAICompatibleProviderAdapter instances.
    // All sampling / RequestControl parameters (temperature, top_p, max_tokens / max_completion_tokens,
    // stop, frequency_penalty, presence_penalty, seed, stream, etc.) are handled by the centralized
    // MafSkillsAgentFactory.ApplyRequestControl + OpenAI path.
    //
    // Cross-checked against latest Azure OpenAI docs (as of 2025/2026):
    // - https://learn.microsoft.com/azure/ai-services/openai/reference
    // - https://learn.microsoft.com/azure/ai-services/openai/how-to/migrating-from-legacy
    //
    // Important differences vs public OpenAI:
    // - Reasoning models often require max_completion_tokens instead of max_tokens.
    // - Seed support is model/region dependent.
    // - Some older deployments may have different parameter names.

    public void Dispose()
    {
        lock (_adapterGate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            foreach (var adapter in _adapters.Values)
            {
                adapter.Dispose();
            }

            _adapters.Clear();
        }
    }

    private OpenAICompatibleProviderAdapter GetOrCreateAdapter(string deploymentName)
    {
        lock (_adapterGate)
        {
            ThrowIfDisposed();
            if (_adapters.TryGetValue(deploymentName, out var existing))
            {
                return existing;
            }

            var created = CreateAdapter(deploymentName);
            _adapters.Add(deploymentName, created);
            return created;
        }
    }

    private OpenAICompatibleProviderAdapter CreateAdapter(string deploymentName)
    {
        var deploymentBaseUri = AzureOpenAIRequestHandler.BuildDeploymentBaseUri(
            _resourceEndpoint,
            deploymentName);
        var targetClient = _httpClientFactory.CreateClient($"{ProviderId}.azure");
        var forwardingHandler = new HttpClientForwardingHandler(
            targetClient,
            disposeHttpClient: true,
            deploymentBaseUri);
        var azureHandler = new AzureOpenAIRequestHandler(
            _apiVersion,
            _maximumErrorResponseBytes,
            forwardingHandler);
        var azureClient = new HttpClient(azureHandler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var compatibleOptions = new OpenAICompatibleProviderOptions(
            ProviderId,
            deploymentBaseUri.AbsoluteUri,
            _apiKey,
            AuthenticationHeader: "api-key",
            AuthenticationScheme: null,
            Capabilities: Capabilities,
            Models: Models,
            ConfigurationVersion: _configurationVersion,
            ProbePath: null);
        return new OpenAICompatibleProviderAdapter(
            compatibleOptions,
            azureClient,
            disposeHttpClient: true,
            _blobResolver);
    }

    private string? ResolveDeploymentName(string modelId)
    {
        var model = Models.FirstOrDefault(candidate =>
            candidate.Id.Equals(modelId, StringComparison.Ordinal)
            || string.Equals(candidate.ProviderModelId, modelId, StringComparison.Ordinal));
        return model is null ? null : model.ProviderModelId ?? model.Id;
    }

    private static Uri ParseResourceEndpoint(string baseUrl)
    {
        var resourceEndpoint = new Uri(baseUrl.TrimEnd('/') + '/', UriKind.Absolute);
        if (resourceEndpoint.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException(
                "Azure OpenAI BaseURL must use HTTP or HTTPS.",
                nameof(baseUrl));
        }

        if (!string.IsNullOrEmpty(resourceEndpoint.Query)
            || !string.IsNullOrEmpty(resourceEndpoint.Fragment))
        {
            throw new ArgumentException(
                "Azure OpenAI BaseURL cannot contain a query or fragment.",
                nameof(baseUrl));
        }

        return resourceEndpoint;
    }

    private static string ValidateApiVersion(string apiVersion)
    {
        if (!AzureOpenAIProtocol.IsValidApiVersion(apiVersion))
        {
            throw new ArgumentException(
                "Azure OpenAI API Version contains unsupported characters.",
                nameof(apiVersion));
        }

        return apiVersion;
    }

    private static RuntimeError CreateDeploymentNotConfiguredError() =>
        new(
            RuntimeErrorCodes.CapabilityInsufficient,
            "configuration",
            "Azure OpenAI deployment is not configured for this request.",
            IsRetryable: false,
            ProviderDetails: null,
            DiagnosticId: Guid.NewGuid().ToString("N"));

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}
