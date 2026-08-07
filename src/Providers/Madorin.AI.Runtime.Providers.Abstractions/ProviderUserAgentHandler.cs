namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>Replaces SDK-generated user-agent values with a provider-specific value.</summary>
public sealed class ProviderUserAgentHandler : DelegatingHandler
{
    private readonly string _userAgent;

    public ProviderUserAgentHandler(string userAgent, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        _userAgent = userAgent;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd(_userAgent);
        return base.SendAsync(request, cancellationToken);
    }
}
