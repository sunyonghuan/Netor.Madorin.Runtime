using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.Anthropic;
using Madorin.AI.Runtime.Providers.WorkerHost;

const string Protocol = "Anthropic";
const string Slug = "anthropic";

var slug = Slug;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--slug")
    {
        slug = args[i + 1];
        break;
    }
}

if (!string.Equals(slug, Slug, StringComparison.Ordinal))
{
    Console.Error.WriteLine($"slug '{slug}' is not '{Slug}'.");
    return 1;
}

var streams = ProviderWorkerStreams.Bind();
return await ProviderWorkerHost.RunWithBlobResolverAsync(
    Protocol,
    Slug,
    BuildAdapter,
    streams.Stdin,
    streams.Stdout,
    streams.Stderr,
    declaredCapabilities: null,
    declaredModels: Madorin.AI.Runtime.Providers.Anthropic.Models.AnthropicModelCatalog.All);

static IRuntimeProviderAdapter BuildAdapter(ProviderConnectionConfig config, IProviderBlobResolver blobResolver)
{
    ArgumentNullException.ThrowIfNull(config);
    return new AnthropicProviderAdapter(
        config.ProviderId,
        config.ApiKey,
        string.IsNullOrWhiteSpace(config.BaseUrl)
            ? "https://api.anthropic.com"
            : config.BaseUrl.Trim(),
        ProviderConnectionModels.Resolve(config, Madorin.AI.Runtime.Providers.Anthropic.Models.AnthropicModelCatalog.All), blobResolver);
}
