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
    return new GlmProviderAdapter(
        new GlmProviderOptions(
            config.ProviderId,
            config.ApiKey,
            string.IsNullOrWhiteSpace(config.BaseUrl) ? string.Empty : config.BaseUrl.Trim(),
            config.Capabilities,
            ToProviderModels(config.Models),
            string.IsNullOrWhiteSpace(config.ConfigurationVersion)
                ? "1"
                : config.ConfigurationVersion.Trim(),
            config.ProbePath));
}

static List<ProviderModel> ToProviderModels(IReadOnlyList<string>? ids) =>
    ids is null || ids.Count == 0
        ? []
        : ids.Select(static id => new ProviderModel(id, id)).ToList();
