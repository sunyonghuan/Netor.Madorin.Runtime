using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.OpenAI;
using Madorin.AI.Runtime.Providers.WorkerHost;

const string Protocol = "OpenAI";
const string Slug = "openai";

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
    declaredModels: Madorin.AI.Runtime.Providers.OpenAI.Models.OpenAIModelCatalog.All);

static IRuntimeProviderAdapter BuildAdapter(ProviderConnectionConfig config)
{
    ArgumentNullException.ThrowIfNull(config);
    return new OpenAIProviderAdapter(
        config.ProviderId,
        config.ApiKey,
        string.IsNullOrWhiteSpace(config.BaseUrl)
            ? "https://api.openai.com/v1"
            : config.BaseUrl.Trim(),
        ProviderConnectionModels.Resolve(config, Madorin.AI.Runtime.Providers.OpenAI.Models.OpenAIModelCatalog.All));
}
