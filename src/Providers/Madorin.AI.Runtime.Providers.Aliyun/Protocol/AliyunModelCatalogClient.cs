using System.Text.Json;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Providers.Aliyun;

internal sealed class AliyunModelCatalogClient : IDisposable
{
    private readonly ProviderAuthenticationHandler _authenticationHandler;
    private readonly HttpClient _httpClient;
    private readonly int _maximumResponseBytes;
    private readonly Uri _modelsUri;

    public AliyunModelCatalogClient(
        AliyunProviderOptions options,
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
        _modelsUri = new Uri(baseAddress, "models");
        _maximumResponseBytes = options.MaximumModelCatalogResponseBytes;
        var forwardingHandler = new HttpClientForwardingHandler(
            httpClient,
            disposeHttpClient,
            baseAddress);
        _authenticationHandler = new ProviderAuthenticationHandler(
            "Authorization",
            "Bearer",
            options.ApiKey,
            forwardingHandler);
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
        using var request = new HttpRequestMessage(HttpMethod.Get, _modelsUri);
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
            AliyunJsonContext.Default.AliyunModelCatalogResponse)
            ?? throw new InvalidDataException("Aliyun model catalog response was empty.");
        if (payload.Data is null)
        {
            throw new InvalidDataException(
                "Aliyun model catalog response must contain a data array.");
        }

        var models = new Dictionary<string, ProviderModel>(StringComparer.Ordinal);
        foreach (var item in payload.Data)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(item.Id) || models.ContainsKey(item.Id))
            {
                continue;
            }

            var configured = configuredModels.FirstOrDefault(model =>
                model.Id.Equals(item.Id, StringComparison.Ordinal));
            models.Add(
                item.Id,
                new ProviderModel(
                    item.Id,
                    configured?.DisplayName ?? item.Id,
                    item.Id,
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
}
