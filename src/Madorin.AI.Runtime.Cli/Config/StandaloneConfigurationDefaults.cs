namespace Madorin.AI.Runtime.Cli.Config;

internal sealed record StandaloneConfigurationDefaults(
    ResolvedConfigurationValue Provider,
    ResolvedConfigurationValue Model,
    ResolvedConfigurationValue Agent);
