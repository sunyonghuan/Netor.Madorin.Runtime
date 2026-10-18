using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.Gemini;
using Madorin.AI.Runtime.Providers.WorkerHost;

const string Protocol = "Gemini";
const string Slug = "gemini";

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
return await ProviderWorkerHost.RunAsync(
    Protocol,
    Slug,
    BuildAdapter,
    streams.Stdin,
    streams.Stdout,
    streams.Stderr,
    declaredCapabilities: null,
    declaredModels: Madorin.AI.Runtime.Providers.Gemini.GeminiModelCatalog.All);

static IRuntimeProviderAdapter BuildAdapter(ProviderConnectionConfig config)
{
    ArgumentNullException.ThrowIfNull(config);
    return new GeminiProviderAdapter(
        new GeminiProviderOptions(
            config.ProviderId,
            config.ApiKey,
            string.IsNullOrWhiteSpace(config.BaseUrl)
                ? GeminiModelCatalog.DefaultBaseUrl
                : config.BaseUrl.Trim(),
            config.Capabilities,
            ProviderConnectionModels.Resolve(config, Madorin.AI.Runtime.Providers.Gemini.GeminiModelCatalog.All),
            string.IsNullOrWhiteSpace(config.ConfigurationVersion)
                ? "1"
                : config.ConfigurationVersion.Trim()));
}
