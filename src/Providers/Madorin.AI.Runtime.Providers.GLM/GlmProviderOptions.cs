using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Providers.GLM;

public sealed record GlmProviderOptions(
    string ProviderId,
    string ApiKey,
    string BaseUrl,
    ProviderCapabilities? Capabilities = null,
    IReadOnlyList<ProviderModel>? Models = null,
    string ConfigurationVersion = "1",
    string? ProbePath = "models",
    int MaximumProbeResponseBytes = 65_536);
