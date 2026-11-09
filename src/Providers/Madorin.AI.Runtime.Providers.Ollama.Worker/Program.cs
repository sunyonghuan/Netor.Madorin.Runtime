using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.Ollama;
using Madorin.AI.Runtime.Providers.WorkerHost;

const string Protocol = "Ollama";
const string Slug = "ollama";

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
    streams.Stderr);

static IRuntimeProviderAdapter BuildAdapter(ProviderConnectionConfig config, IProviderBlobResolver blobResolver)
{
    ArgumentNullException.ThrowIfNull(config);
    return new OllamaProviderAdapter(
        new OllamaProviderOptions(
            config.ProviderId,
            string.IsNullOrWhiteSpace(config.BaseUrl)
                ? OllamaModelCatalog.DefaultBaseUrl
                : config.BaseUrl.Trim(),
            config.ApiKey ?? string.Empty,
            string.IsNullOrWhiteSpace(config.AuthenticationHeader)
                ? "Authorization"
                : config.AuthenticationHeader.Trim(),
            config.AuthenticationScheme,
            string.IsNullOrWhiteSpace(config.ApiKey)
                ? ProviderAuthenticationMode.None
                : ProviderAuthenticationMode.Required,
            config.Capabilities,
            ProviderConnectionModels.Resolve(config),
            string.IsNullOrWhiteSpace(config.ConfigurationVersion)
                ? "1"
                : config.ConfigurationVersion.Trim()), blobResolver: blobResolver);
}
