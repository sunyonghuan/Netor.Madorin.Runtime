using System.Text.Json;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Providers.AzureOpenAI;

internal sealed class AzureOpenAIRequestHandler : DelegatingHandler
{
    private readonly string _apiVersion;
    private readonly int _maximumErrorResponseBytes;

    public AzureOpenAIRequestHandler(
        string apiVersion,
        int maximumErrorResponseBytes,
        HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiVersion);
        if (maximumErrorResponseBytes is <= 0
            or > ProviderHttpContentReader.MaximumAllowedResponseBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumErrorResponseBytes),
                maximumErrorResponseBytes,
                "Azure OpenAI error responses must be limited to between 1 byte and 1 MiB.");
        }

        _apiVersion = apiVersion;
        _maximumErrorResponseBytes = maximumErrorResponseBytes;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.RequestUri);
        request.RequestUri = AddApiVersion(request.RequestUri, _apiVersion);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var statusCode = response.StatusCode;
        var retryAfter = GetFirstHeader(response, "retry-after");
        var requestId = GetFirstHeader(response, "request-id")
            ?? GetFirstHeader(response, "x-request-id")
            ?? GetFirstHeader(response, "apim-request-id");
        string? providerCode = null;
        try
        {
            var content = await ProviderHttpContentReader.ReadBoundedAsync(
                response.Content,
                _maximumErrorResponseBytes,
                cancellationToken).ConfigureAwait(false);
            var payload = JsonSerializer.Deserialize(
                content,
                AzureOpenAIJsonContext.Default.AzureOpenAIErrorResponse);
            requestId ??= payload?.RequestId;
            providerCode = SanitizeProviderCode(payload?.Error?.Code);
        }
        catch (InvalidDataException)
        {
            // HTTP status and safe headers remain sufficient when the error body is oversized.
        }
        catch (JsonException)
        {
            // Error bodies are optional metadata and never replace the stable HTTP mapping.
        }
        finally
        {
            response.Dispose();
        }

        throw new ProviderHttpException(
            statusCode,
            retryAfter,
            requestId,
            providerCode: providerCode);
    }

    internal static Uri BuildDeploymentBaseUri(Uri resourceEndpoint, string deploymentName)
    {
        ArgumentNullException.ThrowIfNull(resourceEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);
        var builder = new UriBuilder(resourceEndpoint)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };
        var path = builder.Path.TrimEnd('/');
        builder.Path = $"{path}/openai/deployments/{Uri.EscapeDataString(deploymentName)}";
        return builder.Uri;
    }

    private static Uri AddApiVersion(Uri requestUri, string apiVersion)
    {
        var builder = new UriBuilder(requestUri);
        var queryParts = builder.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(static part =>
                !part.StartsWith("api-version=", StringComparison.OrdinalIgnoreCase))
            .Append($"api-version={Uri.EscapeDataString(apiVersion)}");
        builder.Query = string.Join('&', queryParts);
        return builder.Uri;
    }

    private static string? GetFirstHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static string? SanitizeProviderCode(string? providerCode)
    {
        if (string.IsNullOrWhiteSpace(providerCode) || providerCode.Length > 128)
        {
            return null;
        }

        return providerCode.All(static character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or '-')
            ? providerCode
            : null;
    }
}
