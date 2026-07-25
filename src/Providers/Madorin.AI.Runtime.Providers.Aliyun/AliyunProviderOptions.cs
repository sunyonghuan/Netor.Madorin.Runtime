using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Providers.Aliyun;

public sealed record AliyunProviderOptions(
    string ProviderId,
    string ApiKey,
    string BaseUrl = AliyunModelCatalog.DefaultBaseUrl,
    ProviderCapabilities? Capabilities = null,
    IReadOnlyList<ProviderModel>? Models = null,
    string ConfigurationVersion = "1",
    int MaximumModelCatalogResponseBytes = 65_536);
