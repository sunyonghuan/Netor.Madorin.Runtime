using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.DeepSeek;
using Madorin.AI.Runtime.Providers.WorkerHost;

const string Protocol = "DeepSeek";
const string Slug = "deepseek";

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
// 宿主侧投影在 hello 前后都可能读取能力；声明与 DeepSeekProviderAdapter 一致，避免配置缺 capabilities 时误杀 reasoning。
var declaredCapabilities = new ProviderCapabilities(
    Streaming: true,
    ToolCalling: true,
    Reasoning: true,
    Usage: true);
return await ProviderWorkerHost.RunWithBlobResolverAsync(
    Protocol,
    Slug,
    BuildAdapter,
    streams.Stdin,
    streams.Stdout,
    streams.Stderr,
    declaredCapabilities: declaredCapabilities,
    declaredModels: Madorin.AI.Runtime.Providers.DeepSeek.Models.DeepSeekModelCatalog.All);

static IRuntimeProviderAdapter BuildAdapter(ProviderConnectionConfig config, IProviderBlobResolver blobResolver)
{
    ArgumentNullException.ThrowIfNull(config);
    return new DeepSeekProviderAdapter(
        string.IsNullOrWhiteSpace(config.BaseUrl)
            ? "https://api.deepseek.com/v1"
            : config.BaseUrl.Trim(),
        config.ApiKey,
        models: ProviderConnectionModels.Resolve(config, Madorin.AI.Runtime.Providers.DeepSeek.Models.DeepSeekModelCatalog.All),
        providerId: config.ProviderId, blobResolver: blobResolver);
}
