using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Server;

/// <summary>
/// Shared provider connection surface used by host Registry and standalone factory.
/// </summary>
public sealed record ProviderConnectionConfig(
    string ProviderId,
    string Protocol,
    string BaseUrl,
    string ApiKey,
    string? AuthenticationHeader = null,
    string? AuthenticationScheme = null,
    string? UserAgent = null,
    string? ApiVersion = null,
    IReadOnlyList<string>? Models = null,
    string? ConfigurationVersion = null,
    ProviderCapabilities? Capabilities = null,
    string? ProbePath = null,
    int? MaxTools = null);
