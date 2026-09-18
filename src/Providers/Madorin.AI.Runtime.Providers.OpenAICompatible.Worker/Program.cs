using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.OpenAICompatible;
using Madorin.AI.Runtime.Providers.WorkerHost;

const string Protocol = "OpenAI Compatible";
const string Slug = "openai-compatible";

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
    var baseUrl = string.IsNullOrWhiteSpace(config.BaseUrl)
        ? "https://api.openai.com/v1"
        : config.BaseUrl.Trim();
    var options = new OpenAICompatibleProviderOptions(
        config.ProviderId,
        baseUrl,
        config.ApiKey,
        string.IsNullOrWhiteSpace(config.AuthenticationHeader)
            ? "Authorization"
            : config.AuthenticationHeader.Trim(),
        config.AuthenticationScheme,
        config.Capabilities,
        ToProviderModels(config.Models),
        string.IsNullOrWhiteSpace(config.ConfigurationVersion)
            ? "1"
            : config.ConfigurationVersion.Trim(),
        config.ProbePath,
        UserAgent: config.UserAgent);
    return new OpenAICompatibleProviderAdapter(
        options,
        ProviderHttpClientFactory.Shared.CreateClient(config.ProviderId),
        disposeHttpClient: true);
}

static List<ProviderModel> ToProviderModels(IReadOnlyList<string>? ids) =>
    ids is null || ids.Count == 0
        ? []
        : ids.Select(static id => new ProviderModel(id, id)).ToList();
