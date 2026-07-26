namespace Madorin.AI.Runtime.Cli.Config;

internal sealed record ResolvedStandaloneSelection(
    ProviderEntry Provider,
    string Model,
    AgentConfig Agent,
    ConfigurationValueSource ProviderSource,
    ConfigurationValueSource ModelSource,
    ConfigurationValueSource AgentSource);
