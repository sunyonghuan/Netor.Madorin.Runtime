using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.Aliyun;
using Madorin.AI.Runtime.Providers.WorkerHost;

const string Protocol = "Aliyun";
const string Slug = "aliyun";

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
    declaredModels: Madorin.AI.Runtime.Providers.Aliyun.AliyunModelCatalog.All);

static IRuntimeProviderAdapter BuildAdapter(ProviderConnectionConfig config)
{
    ArgumentNullException.ThrowIfNull(config);
    return new AliyunProviderAdapter(
        new AliyunProviderOptions(
            config.ProviderId,
            config.ApiKey,
            string.IsNullOrWhiteSpace(config.BaseUrl)
                ? AliyunModelCatalog.DefaultBaseUrl
                : config.BaseUrl.Trim(),
            config.Capabilities,
            ProviderConnectionModels.Resolve(config, Madorin.AI.Runtime.Providers.Aliyun.AliyunModelCatalog.All),
            string.IsNullOrWhiteSpace(config.ConfigurationVersion)
                ? "1"
                : config.ConfigurationVersion.Trim()));
}
