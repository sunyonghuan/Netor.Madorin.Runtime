using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Providers.Gemini;

public sealed record GeminiProviderOptions(
    string ProviderId,
    string ApiKey,
    string BaseUrl = GeminiModelCatalog.DefaultBaseUrl,
    ProviderCapabilities? Capabilities = null,
    IReadOnlyList<ProviderModel>? Models = null,
    string ConfigurationVersion = "1",
    int MaximumModelCatalogResponseBytes = 65_536);
