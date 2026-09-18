using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.Kimi;
using Madorin.AI.Runtime.Providers.WorkerHost;

const string Protocol = "Kimi";
const string Slug = "kimi";

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
    streams.Stderr);

static IRuntimeProviderAdapter BuildAdapter(ProviderConnectionConfig config)
{
    ArgumentNullException.ThrowIfNull(config);
    return new KimiProviderAdapter(
        config.ApiKey,
        baseUrl: string.IsNullOrWhiteSpace(config.BaseUrl)
            ? "https://api.moonshot.cn/v1"
            : config.BaseUrl.Trim(),
        models: ToProviderModels(config.Models),
        providerId: config.ProviderId,
        maxTools: config.MaxTools);
}

static List<ProviderModel> ToProviderModels(IReadOnlyList<string>? ids) =>
    ids is null || ids.Count == 0
        ? []
        : ids.Select(static id => new ProviderModel(id, id)).ToList();
