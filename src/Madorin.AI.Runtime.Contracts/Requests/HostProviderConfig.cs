namespace Madorin.AI.Runtime.Contracts;

/// <summary>
/// Host-mode provider connection snapshot injected by-value over the control channel.
/// Registry key is <see cref="ProviderId"/> (conceptually distinct from host VendorId).
/// </summary>
public sealed record HostProviderConfig(
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
    IReadOnlyList<HostProviderModelConfig>? ModelDescriptors = null);
