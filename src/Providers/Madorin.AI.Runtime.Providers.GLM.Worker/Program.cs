using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.GLM;
using Madorin.AI.Runtime.Providers.WorkerHost;

const string Protocol = "GLM";
const string Slug = "glm";

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
    declaredModels: Madorin.AI.Runtime.Providers.GLM.GlmModelCatalog.All);

static IRuntimeProviderAdapter BuildAdapter(ProviderConnectionConfig config, IProviderBlobResolver blobResolver)
{
    ArgumentNullException.ThrowIfNull(config);
    return new GlmProviderAdapter(
        new GlmProviderOptions(
            config.ProviderId,
            config.ApiKey,
            string.IsNullOrWhiteSpace(config.BaseUrl) ? string.Empty : config.BaseUrl.Trim(),
            config.Capabilities,
            ProviderConnectionModels.Resolve(config, Madorin.AI.Runtime.Providers.GLM.GlmModelCatalog.All),
            string.IsNullOrWhiteSpace(config.ConfigurationVersion)
                ? "1"
                : config.ConfigurationVersion.Trim(),
            config.ProbePath), blobResolver: blobResolver);
}
