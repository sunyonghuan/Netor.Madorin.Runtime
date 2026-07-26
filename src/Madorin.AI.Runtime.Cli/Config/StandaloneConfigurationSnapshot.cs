namespace Madorin.AI.Runtime.Cli.Config;

internal sealed record StandaloneConfigurationSnapshot(
    StandaloneConfig GlobalConfig,
    IReadOnlyList<AgentConfig> Agents,
    string GlobalConfigPath,
    string WorkspaceConfigPath,
    bool WorkspaceConfigExists,
    StandaloneConfigurationDefaults GlobalDefaults,
    StandaloneConfigurationDefaults EffectiveDefaults,
    ResolvedStandaloneSelection EffectiveSelection);
