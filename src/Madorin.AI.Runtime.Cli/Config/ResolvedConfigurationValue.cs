namespace Madorin.AI.Runtime.Cli.Config;

internal sealed record ResolvedConfigurationValue(
    string Value,
    ConfigurationValueSource Source);
