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
return await ProviderWorkerHost.RunAsync(
    Protocol,
    Slug,
    BuildAdapter,
    streams.Stdin,
    streams.Stdout,
    streams.Stderr,
    declaredCapabilities);

static IRuntimeProviderAdapter BuildAdapter(ProviderConnectionConfig config)
{
    ArgumentNullException.ThrowIfNull(config);
    return new DeepSeekProviderAdapter(
        string.IsNullOrWhiteSpace(config.BaseUrl)
            ? "https://api.deepseek.com/v1"
            : config.BaseUrl.Trim(),
        config.ApiKey,
        models: ToProviderModels(config.Models),
        providerId: config.ProviderId);
}

static List<ProviderModel> ToProviderModels(IReadOnlyList<string>? ids) =>
    ids is null || ids.Count == 0
        ? []
        : ids.Select(static id => new ProviderModel(id, id)).ToList();
