using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Providers.Ollama;

public sealed record OllamaProviderOptions(
    string ProviderId,
    string BaseUrl = OllamaModelCatalog.DefaultBaseUrl,
    string ApiKey = "",
    string AuthenticationHeader = "Authorization",
    string? AuthenticationScheme = "Bearer",
    ProviderAuthenticationMode AuthenticationMode = ProviderAuthenticationMode.None,
    ProviderCapabilities? Capabilities = null,
    IReadOnlyList<ProviderModel>? Models = null,
    string ConfigurationVersion = "1",
    int MaximumModelCatalogResponseBytes = 65_536);
