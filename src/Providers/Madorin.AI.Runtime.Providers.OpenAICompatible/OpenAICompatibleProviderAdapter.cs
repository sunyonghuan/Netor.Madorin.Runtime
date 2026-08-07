using System.Collections.Concurrent;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.OpenAI;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace Madorin.AI.Runtime.Providers.OpenAICompatible;

public sealed class OpenAICompatibleProviderAdapter :
    IRuntimeProviderAdapter,
    IRuntimeProviderModelCatalogSource,
    IRuntimeProviderCredentialUpdater,
    IDisposable
{
    private const string NoAuthenticationTransportCredential = "madorin-no-auth";
    public const string DefaultUserAgent = "Madorin-AI-Runtime/1.0";

    private readonly OpenAIProviderAdapter _innerAdapter;
    private readonly ProviderAuthenticationHandler _authenticationHandler;
    private readonly Uri _baseAddress;
    private readonly string _configurationVersion;
    private readonly HttpClient _httpClient;
    private readonly int _maximumProbeResponseBytes;
    private readonly string? _probePath;
    private readonly ProviderAuthenticationMode _authenticationMode;

    /// <summary>Standalone constructor — creates its own <see cref="HttpClient"/>.</summary>
    public OpenAICompatibleProviderAdapter(
        string providerId,
        string baseUrl,
        string apiKey,
        IReadOnlyList<ProviderModel>? models = null,
        IProviderBlobResolver? blobResolver = null)
        : this(
            new OpenAICompatibleProviderOptions(providerId, baseUrl, apiKey, Models: models),
            CreateFactoryHandler(ProviderHttpClientFactory.Shared, providerId, baseUrl),
            disposeInnerHandler: true,
            blobResolver)
    {
    }

    public OpenAICompatibleProviderAdapter(
        string providerId,
        string baseUrl,
        string apiKey,
        IHttpClientFactory httpClientFactory,
        IReadOnlyList<ProviderModel>? models = null,
        IProviderBlobResolver? blobResolver = null)
        : this(
            new OpenAICompatibleProviderOptions(providerId, baseUrl, apiKey, Models: models),
            CreateFactoryHandler(httpClientFactory, providerId, baseUrl),
            disposeInnerHandler: true,
            blobResolver)
    {
    }

    public OpenAICompatibleProviderAdapter(
        OpenAICompatibleProviderOptions options,
        HttpClient httpClient,
        bool disposeHttpClient = false,
        IProviderBlobResolver? blobResolver = null)
        : this(
            options,
            new HttpClientForwardingHandler(
                httpClient,
                disposeHttpClient,
                new Uri(options.BaseUrl.TrimEnd('/') + '/', UriKind.Absolute)),
            disposeInnerHandler: true,
            blobResolver)
    {
    }

    private OpenAICompatibleProviderAdapter(
        OpenAICompatibleProviderOptions options,
        HttpMessageHandler innerHandler,
        bool disposeInnerHandler,
        IProviderBlobResolver? blobResolver)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BaseUrl);
        if (options.AuthenticationMode is ProviderAuthenticationMode.Required)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.AuthenticationHeader);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConfigurationVersion);
        ArgumentNullException.ThrowIfNull(innerHandler);
        if (options.MaximumProbeResponseBytes is <= 0 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaximumProbeResponseBytes,
                "Probe responses must be limited to between 1 byte and 1 MiB.");
        }

        ProviderId = options.ProviderId;
        Capabilities = options.Capabilities ?? new ProviderCapabilities(Streaming: true);
        Models = options.Models ?? [];
        _baseAddress = new Uri(options.BaseUrl.TrimEnd('/') + '/', UriKind.Absolute);
        _configurationVersion = options.ConfigurationVersion;
        _maximumProbeResponseBytes = options.MaximumProbeResponseBytes;
        _probePath = options.ProbePath;
        _authenticationMode = options.AuthenticationMode;

        var userAgentHandler = new ProviderUserAgentHandler(
            string.IsNullOrWhiteSpace(options.UserAgent)
                ? DefaultUserAgent
                : options.UserAgent,
            innerHandler);

        _authenticationHandler = new ProviderAuthenticationHandler(
            options.AuthenticationHeader,
            options.AuthenticationScheme,
            options.ApiKey,
            userAgentHandler,
            options.AuthenticationMode);
        _httpClient = new HttpClient(
            new ProviderHttpResponseHandler(_authenticationHandler),
            disposeHandler: disposeInnerHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(options.BaseUrl.TrimEnd('/'), UriKind.Absolute),
            NetworkTimeout = TimeSpan.FromMinutes(10),
            RetryPolicy = new ClientRetryPolicy(0),
            Transport = new HttpClientPipelineTransport(_httpClient),
            ClientLoggingOptions = new ClientLoggingOptions
            {
                EnableLogging = false,
                EnableMessageLogging = false,
                EnableMessageContentLogging = false,
                MessageContentSizeLimit = 1024
            }
        };
        var transportCredential = options.AuthenticationMode is ProviderAuthenticationMode.None
            ? NoAuthenticationTransportCredential
            : options.ApiKey;
        var openAIClient = new OpenAIClient(new ApiKeyCredential(transportCredential), clientOptions);
        var chatClients = new ConcurrentDictionary<string, IChatClient>(StringComparer.Ordinal);

        _innerAdapter = new OpenAIProviderAdapter(
            modelId => chatClients.GetOrAdd(
                modelId,
                id => openAIClient.GetChatClient(id).AsIChatClient()),
            Models,
            blobResolver);
    }

    public string ProviderId { get; }

    public ProviderCapabilities Capabilities { get; }

    public IReadOnlyList<ProviderModel> Models { get; }

    public ProviderConfigurationSchema ConfigurationSchema => new(
        ProviderId,
        _configurationVersion,
        [
            new(
                "apiKey",
                ProviderConfigurationValueKind.Secret,
                IsRequired: _authenticationMode is ProviderAuthenticationMode.Required),
            new("baseUrl", ProviderConfigurationValueKind.Uri, IsRequired: true),
            new("authenticationHeader", ProviderConfigurationValueKind.Text, IsRequired: true),
            new("authenticationScheme", ProviderConfigurationValueKind.Text, IsRequired: false),
            new("capabilities", ProviderConfigurationValueKind.Text, IsRequired: true),
            new("models", ProviderConfigurationValueKind.TextArray, IsRequired: true),
            new("probePath", ProviderConfigurationValueKind.Text, IsRequired: false),
            new("userAgent", ProviderConfigurationValueKind.Text, IsRequired: false)
        ]);

    public Task ValidateCapabilitiesAsync(ContentBlock[] input, CancellationToken ct = default)
    {
        return ProviderCapabilityValidator.ValidateAsync(Capabilities, input, ct);
    }

    public async ValueTask<ProviderCapabilityProbeResult> ProbeCapabilitiesAsync(
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_probePath))
        {
            return ProviderCapabilityProbe.FromDeclared(Capabilities, _configurationVersion);
        }

        using var document = await FetchProbeDocumentAsync(ct).ConfigureAwait(false);
        var capabilities = Capabilities;
        var source = "declared";
        if (document.RootElement.TryGetProperty("madorinCapabilities", out var capabilityDocument)
            && capabilityDocument.ValueKind == JsonValueKind.Object)
        {
            capabilities = ParseCapabilities(capabilityDocument);
            source = "endpoint";
        }

        ProviderExtensionData[] extensions =
        [
            ProviderExtensionPolicy.CreateRedactedJson(
                ProviderId,
                "raw.response",
                document.RootElement)
        ];
        ProviderExtensionData.ValidateCollection(extensions);
        return new ProviderCapabilityProbeResult(
            capabilities,
            DateTimeOffset.UtcNow,
            _configurationVersion,
            source,
            ProviderCapabilityProbe.GetMissingCapabilities(capabilities),
            extensions);
    }

    public async ValueTask<IReadOnlyList<ProviderModel>> FetchModelsAsync(
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_probePath))
        {
            return Models;
        }

        using var document = await FetchProbeDocumentAsync(ct).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidDataException("Provider model catalog response must contain a data array.");
        }

        var models = new Dictionary<string, ProviderModel>(StringComparer.Ordinal);
        foreach (var item in data.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (item.ValueKind is not JsonValueKind.Object
                || !item.TryGetProperty("id", out var idProperty)
                || idProperty.ValueKind is not JsonValueKind.String
                || string.IsNullOrWhiteSpace(idProperty.GetString()))
            {
                continue;
            }

            var id = idProperty.GetString()!;
            if (models.ContainsKey(id))
            {
                continue;
            }

            var configured = Models.FirstOrDefault(model =>
                model.Id.Equals(id, StringComparison.Ordinal));
            var displayName = GetOptionalString(item, "display_name") ?? configured?.DisplayName ?? id;
            models.Add(
                id,
                new ProviderModel(
                    id,
                    displayName,
                    id,
                    configured?.ContextWindow,
                    configured?.MaxOutputTokens,
                    configured?.Capabilities,
                    configured?.IsDeprecated ?? false));
        }

        return models.Values.OrderBy(static model => model.Id, StringComparer.Ordinal).ToArray();
    }

    public Task UpdateCredentialsAsync(
        CredentialsUpdateParameters parameters,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ct.ThrowIfCancellationRequested();
        if (!string.Equals(parameters.ProviderId, ProviderId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Credential update targets another Provider.", nameof(parameters));
        }

        _authenticationHandler.Update(parameters.Credential);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
        RuntimeProviderRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ProviderCapabilityValidator.ValidateSkillsSupport(Capabilities, request.Skills, ct);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            request.CancellationToken);
        var effectiveCt = linkedCts.Token;

        RuntimeError? validationFailure = null;
        try
        {
            await ProviderCapabilityValidator.ValidateAsync(
                Capabilities,
                request,
                Models,
                effectiveCt).ConfigureAwait(false);
        }
        catch (RuntimeProviderException ex)
        {
            validationFailure = ex.Error;
        }

        if (validationFailure is not null)
        {
            yield return new InvocationFailedProviderEvent(request.InvocationId, validationFailure);
            yield break;
        }

        await foreach (var providerEvent in _innerAdapter.CompleteStreamingAsync(
                           request,
                           effectiveCt).ConfigureAwait(false))
        {
            yield return providerEvent;
        }
    }

    public void Dispose()
    {
        _innerAdapter.Dispose();
        _httpClient.Dispose();
    }

    private async Task<JsonDocument> FetchProbeDocumentAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseAddress, _probePath));
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8_192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > _maximumProbeResponseBytes)
            {
                throw new InvalidDataException(
                    $"Provider probe response exceeded {_maximumProbeResponseBytes} bytes.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: ct).ConfigureAwait(false);
    }

    private static string? GetOptionalString(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var property)
        && property.ValueKind is JsonValueKind.String
        && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()
            : null;

    private static ProviderCapabilities ParseCapabilities(JsonElement value) => new(
        Streaming: GetBoolean(value, "streaming"),
        ToolCalling: GetBoolean(value, "toolCalling"),
        Vision: GetBoolean(value, "vision"),
        Audio: GetBoolean(value, "audio"),
        StructuredOutput: GetBoolean(value, "structuredOutput"),
        Reasoning: GetBoolean(value, "reasoning"),
        PromptCaching: GetBoolean(value, "promptCaching"),
        Files: GetBoolean(value, "files"),
        ComputerUse: GetBoolean(value, "computerUse"),
        Embeddings: GetBoolean(value, "embeddings"),
        Usage: GetBoolean(value, "usage"),
        RemoteCancellation: GetBoolean(value, "remoteCancellation"));

    private static bool GetBoolean(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property)
        && property.ValueKind is JsonValueKind.True;

    private static HttpClientForwardingHandler CreateFactoryHandler(
        IHttpClientFactory httpClientFactory,
        string providerId,
        string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        return new HttpClientForwardingHandler(
            httpClientFactory.CreateClient(providerId),
            disposeHttpClient: true,
            new Uri(baseUrl.TrimEnd('/') + '/', UriKind.Absolute));
    }
}
