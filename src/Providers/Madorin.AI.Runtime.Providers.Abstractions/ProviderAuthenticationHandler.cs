using System.Net.Http.Headers;

namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>Applies an updateable Provider credential without recreating the HTTP pipeline.</summary>
public sealed class ProviderAuthenticationHandler : DelegatingHandler
{
    private readonly string _headerName;
    private readonly string? _scheme;
    private readonly ProviderAuthenticationMode _mode;
    private string _credential;

    public ProviderAuthenticationHandler(
        string headerName,
        string? scheme,
        string credential,
        HttpMessageHandler innerHandler,
        ProviderAuthenticationMode mode = ProviderAuthenticationMode.Required)
        : base(innerHandler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headerName);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown authentication mode.");
        }

        if (mode is ProviderAuthenticationMode.Required)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        }

        _headerName = headerName;
        _scheme = scheme;
        _credential = credential;
        _mode = mode;
    }

    public void Update(string credential)
    {
        if (_mode is ProviderAuthenticationMode.None)
        {
            throw new InvalidOperationException("Credentials cannot be updated when authentication is disabled.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        Volatile.Write(ref _credential, credential);
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Remove(_headerName);
        if (_mode is ProviderAuthenticationMode.None)
        {
            request.Headers.Remove("Authorization");
            return base.SendAsync(request, cancellationToken);
        }

        var credential = Volatile.Read(ref _credential);
        if (string.Equals(_headerName, "Authorization", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(_scheme ?? "Bearer", credential);
        }
        else
        {
            request.Headers.Remove("Authorization");
            var value = _scheme is null ? credential : $"{_scheme} {credential}";
            request.Headers.TryAddWithoutValidation(_headerName, value);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
