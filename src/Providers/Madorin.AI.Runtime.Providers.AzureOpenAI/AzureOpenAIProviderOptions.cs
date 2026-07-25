using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Providers.AzureOpenAI;

public sealed record AzureOpenAIProviderOptions(
    string ProviderId,
    string ApiKey,
    string BaseUrl,
    IReadOnlyList<ProviderModel> Models,
    string ApiVersion = AzureOpenAIProtocol.DefaultApiVersion,
    ProviderCapabilities? Capabilities = null,
    string ConfigurationVersion = "1",
    int MaximumErrorResponseBytes = 65_536);
