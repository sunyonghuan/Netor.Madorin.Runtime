using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.AzureOpenAI;
using Madorin.AI.Runtime.Providers.WorkerHost;

const string Protocol = "Azure OpenAI";
const string Slug = "azure-openai";

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
    return new AzureOpenAIProviderAdapter(
        new AzureOpenAIProviderOptions(
            config.ProviderId,
            config.ApiKey,
            string.IsNullOrWhiteSpace(config.BaseUrl) ? string.Empty : config.BaseUrl.Trim(),
            ProviderConnectionModels.Resolve(config),
            config.ApiVersion ?? AzureOpenAIProtocol.DefaultApiVersion,
            config.Capabilities,
            string.IsNullOrWhiteSpace(config.ConfigurationVersion)
                ? "1"
                : config.ConfigurationVersion.Trim()));
}
