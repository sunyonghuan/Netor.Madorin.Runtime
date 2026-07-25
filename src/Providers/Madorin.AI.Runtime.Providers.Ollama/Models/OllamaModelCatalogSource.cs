using System.Text.Json;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Providers.Ollama;

internal sealed class OllamaModelCatalogSource : IDisposable
{
    private readonly ProviderAuthenticationHandler _authenticationHandler;
    private readonly HttpClient _httpClient;
    private readonly int _maximumResponseBytes;
    private readonly Uri _tagsUri;

    public OllamaModelCatalogSource(
        OllamaProviderOptions options,
        HttpClient httpClient,
        bool disposeHttpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        if (options.MaximumModelCatalogResponseBytes is <= 0
            or > ProviderHttpContentReader.MaximumAllowedResponseBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaximumModelCatalogResponseBytes,
                "Model catalog responses must be limited to between 1 byte and 1 MiB.");
        }

        var baseAddress = new Uri(options.BaseUrl.TrimEnd('/') + '/', UriKind.Absolute);
        _tagsUri = BuildTagsUri(baseAddress);
        _maximumResponseBytes = options.MaximumModelCatalogResponseBytes;
        var forwardingHandler = new HttpClientForwardingHandler(
            httpClient,
            disposeHttpClient,
            baseAddress);
        _authenticationHandler = new ProviderAuthenticationHandler(
            options.AuthenticationHeader,
            options.AuthenticationScheme,
            options.ApiKey,
            forwardingHandler,
            options.AuthenticationMode);
        _httpClient = new HttpClient(
            new ProviderHttpResponseHandler(_authenticationHandler),
            disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async ValueTask<IReadOnlyList<ProviderModel>> FetchModelsAsync(
        IReadOnlyList<ProviderModel> configuredModels,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _tagsUri);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
        var content = await ProviderHttpContentReader.ReadBoundedAsync(
            response.Content,
            _maximumResponseBytes,
            ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Deserialize(
            content,
            OllamaJsonContext.Default.OllamaTagsResponse)
            ?? throw new InvalidDataException("Ollama tags response was empty.");
        if (payload.Models is null)
        {
            throw new InvalidDataException("Ollama tags response must contain a models array.");
        }

        var models = new Dictionary<string, ProviderModel>(StringComparer.Ordinal);
        foreach (var item in payload.Models)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(item.Name) || models.ContainsKey(item.Name))
            {
                continue;
            }

            var configured = configuredModels.FirstOrDefault(model =>
                model.Id.Equals(item.Name, StringComparison.Ordinal));
            var displayName = configured?.DisplayName ?? FormatDisplayName(item.Name, item.Details?.Family);
            models.Add(
                item.Name,
                new ProviderModel(
                    item.Name,
                    displayName,
                    item.Name,
                    configured?.ContextWindow,
                    configured?.MaxOutputTokens,
                    configured?.Capabilities,
                    configured?.IsDeprecated ?? false));
        }

        return models.Values.OrderBy(static model => model.Id, StringComparer.Ordinal).ToArray();
    }

    public void UpdateCredentials(string credential)
    {
        _authenticationHandler.Update(credential);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private static Uri BuildTagsUri(Uri baseAddress)
    {
        var builder = new UriBuilder(baseAddress)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };
        var path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^3].TrimEnd('/');
        }

        builder.Path = $"{path}/api/tags";
        return builder.Uri;
    }

    private static string FormatDisplayName(string name, string? family) =>
        string.IsNullOrWhiteSpace(family) ? name : $"{name} ({family})";
}
