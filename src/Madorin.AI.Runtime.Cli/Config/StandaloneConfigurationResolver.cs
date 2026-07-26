using System.Text.Json;

namespace Madorin.AI.Runtime.Cli.Config;

internal sealed class StandaloneConfigurationResolver
{
    internal const string ConfigurationNotFoundCode = "ConfigurationNotFound";
    internal const string ConfigurationInvalidCode = "ConfigurationInvalid";
    internal const string WorkspaceConfigurationInvalidCode = "WorkspaceConfigurationInvalid";
    internal const string WorkspaceSelectionNotFoundCode = "WorkspaceSelectionNotFound";
    internal const string SelectionNotFoundCode = "SelectionNotFound";

    private readonly StandaloneConfigLoader _globalLoader;

    public StandaloneConfigurationResolver(StandaloneConfigLoader globalLoader)
    {
        _globalLoader = globalLoader ?? throw new ArgumentNullException(nameof(globalLoader));
    }

    public async Task<StandaloneConfigurationSnapshot> LoadAsync(
        string workspaceRoot,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        StandaloneConfig globalConfig;
        IReadOnlyList<AgentConfigDocument> agentDocuments;
        try
        {
            globalConfig = await _globalLoader.LoadAsync(ct).ConfigureAwait(false)
                ?? throw new StandaloneConfigurationException(
                    ConfigurationNotFoundCode,
                    $"Configuration not found: {_globalLoader.ConfigPath}");
            agentDocuments = await _globalLoader.LoadAgentDocumentsAsync(ct).ConfigureAwait(false);
        }
        catch (StandaloneConfigurationException)
        {
            throw;
        }
        catch (Exception ex) when (IsGlobalConfigurationException(ex))
        {
            throw new StandaloneConfigurationException(
                ConfigurationInvalidCode,
                $"Global configuration '{_globalLoader.ConfigPath}' could not be loaded: {ex.Message}",
                ex);
        }

        var validationErrors = StandaloneConfigValidator.Validate(
            globalConfig,
            _globalLoader.ConfigPath,
            agentDocuments);
        if (validationErrors.Count > 0)
        {
            throw new StandaloneConfigurationException(
                ConfigurationInvalidCode,
                RedactSecrets(string.Join(Environment.NewLine, validationErrors), globalConfig));
        }

        var agents = agentDocuments.Select(static document => document.Agent).ToArray();
        var globalDefaults = CreateGlobalDefaults(globalConfig);
        var workspaceLoader = new WorkspaceConfigLoader(workspaceRoot);
        var workspaceConfig = await workspaceLoader.LoadAsync(ct).ConfigureAwait(false);
        var workspaceSelection = workspaceConfig?.Selection;

        ValidateWorkspaceReferences(
            workspaceLoader.ConfigPath,
            workspaceSelection,
            globalConfig,
            agents,
            globalDefaults);

        var effectiveDefaults = new StandaloneConfigurationDefaults(
            CreateEffectiveValue(
                workspaceSelection?.Provider,
                globalDefaults.Provider),
            CreateEffectiveValue(
                workspaceSelection?.Model,
                globalDefaults.Model),
            CreateEffectiveValue(
                workspaceSelection?.Agent,
                globalDefaults.Agent));

        ResolvedStandaloneSelection effectiveSelection;
        try
        {
            effectiveSelection = ResolveSelectionCore(
                globalConfig,
                agents,
                effectiveDefaults,
                providerOverride: null,
                modelOverride: null,
                agentOverride: null,
                WorkspaceSelectionNotFoundCode,
                workspaceLoader.ConfigPath);
        }
        catch (StandaloneConfigurationException ex)
            when (workspaceConfig is null && ex.Code == WorkspaceSelectionNotFoundCode)
        {
            throw new StandaloneConfigurationException(
                ConfigurationInvalidCode,
                ex.Message,
                ex);
        }

        return new StandaloneConfigurationSnapshot(
            globalConfig,
            Array.AsReadOnly(agents),
            _globalLoader.ConfigPath,
            workspaceLoader.ConfigPath,
            workspaceLoader.Exists,
            globalDefaults,
            effectiveDefaults,
            effectiveSelection);
    }

    public static ResolvedStandaloneSelection ResolveSelection(
        StandaloneConfigurationSnapshot snapshot,
        string? providerOverride,
        string? modelOverride,
        string? agentOverride)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return ResolveSelectionCore(
            snapshot.GlobalConfig,
            snapshot.Agents,
            snapshot.EffectiveDefaults,
            providerOverride,
            modelOverride,
            agentOverride,
            SelectionNotFoundCode,
            snapshot.WorkspaceConfigPath);
    }

    public static bool TryResolveGlobalSelection(
        StandaloneConfig config,
        IReadOnlyList<AgentConfig> agents,
        string? providerOverride,
        string? modelOverride,
        string? agentOverride,
        out ResolvedStandaloneSelection selection)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(agents);
        try
        {
            selection = ResolveSelectionCore(
                config,
                agents,
                CreateGlobalDefaults(config),
                providerOverride,
                modelOverride,
                agentOverride,
                SelectionNotFoundCode,
                "global configuration");
            return true;
        }
        catch (StandaloneConfigurationException)
        {
            selection = default!;
            return false;
        }
    }

    public static string GetSourceName(ConfigurationValueSource source) => source switch
    {
        ConfigurationValueSource.CommandLine => "commandLine",
        ConfigurationValueSource.Agent => "agent",
        ConfigurationValueSource.Workspace => "workspace",
        ConfigurationValueSource.Global => "global",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null)
    };

    private static StandaloneConfigurationDefaults CreateGlobalDefaults(StandaloneConfig config) =>
        new(
            new ResolvedConfigurationValue(
                config.DefaultProvider,
                ConfigurationValueSource.Global),
            new ResolvedConfigurationValue(
                config.DefaultModel,
                ConfigurationValueSource.Global),
            new ResolvedConfigurationValue(
                config.DefaultAgent,
                ConfigurationValueSource.Global));

    private static ResolvedConfigurationValue CreateEffectiveValue(
        string? workspaceValue,
        ResolvedConfigurationValue globalValue) =>
        workspaceValue is null
            ? globalValue
            : new ResolvedConfigurationValue(
                workspaceValue,
                ConfigurationValueSource.Workspace);

    private static void ValidateWorkspaceReferences(
        string workspaceConfigPath,
        WorkspaceSelectionConfig? workspaceSelection,
        StandaloneConfig globalConfig,
        IReadOnlyList<AgentConfig> agents,
        StandaloneConfigurationDefaults globalDefaults)
    {
        if (workspaceSelection is null)
        {
            return;
        }

        if (workspaceSelection.Agent is not null
            && !agents.Any(agent => agent.Id.Equals(
                workspaceSelection.Agent,
                StringComparison.Ordinal)))
        {
            throw WorkspaceSelectionError(
                workspaceConfigPath,
                "selection.agent",
                $"Agent '{workspaceSelection.Agent}' does not exist.");
        }

        if (workspaceSelection.Provider is not null
            && !globalConfig.Providers.Any(provider => provider.Name.Equals(
                workspaceSelection.Provider,
                StringComparison.Ordinal)))
        {
            throw WorkspaceSelectionError(
                workspaceConfigPath,
                "selection.provider",
                $"Provider '{workspaceSelection.Provider}' does not exist.");
        }

        var agentId = workspaceSelection.Agent ?? globalDefaults.Agent.Value;
        var agent = agents.First(candidate => candidate.Id.Equals(agentId, StringComparison.Ordinal));
        if (workspaceSelection.Model is null || agent.Model is not null)
        {
            return;
        }

        var providerId = agent.Provider
            ?? workspaceSelection.Provider
            ?? globalDefaults.Provider.Value;
        var provider = globalConfig.Providers.First(candidate => candidate.Name.Equals(
            providerId,
            StringComparison.Ordinal));
        if (!provider.Models.Contains(workspaceSelection.Model, StringComparer.Ordinal))
        {
            throw WorkspaceSelectionError(
                workspaceConfigPath,
                "selection.model",
                $"Model '{workspaceSelection.Model}' is not provided by Provider '{providerId}'.");
        }
    }

    private static ResolvedStandaloneSelection ResolveSelectionCore(
        StandaloneConfig config,
        IReadOnlyList<AgentConfig> agents,
        StandaloneConfigurationDefaults defaults,
        string? providerOverride,
        string? modelOverride,
        string? agentOverride,
        string errorCode,
        string sourcePath)
    {
        var agentId = agentOverride ?? defaults.Agent.Value;
        var agentSource = agentOverride is null
            ? defaults.Agent.Source
            : ConfigurationValueSource.CommandLine;
        var agent = agents.FirstOrDefault(candidate => candidate.Id.Equals(
            agentId,
            StringComparison.Ordinal));
        if (agent is null)
        {
            throw SelectionError(
                errorCode,
                sourcePath,
                $"Agent '{agentId}' does not exist.");
        }

        var providerId = providerOverride ?? agent.Provider ?? defaults.Provider.Value;
        var providerSource = providerOverride is not null
            ? ConfigurationValueSource.CommandLine
            : agent.Provider is not null
                ? ConfigurationValueSource.Agent
                : defaults.Provider.Source;
        var provider = config.Providers.FirstOrDefault(candidate => candidate.Name.Equals(
            providerId,
            StringComparison.Ordinal));
        if (provider is null)
        {
            throw SelectionError(
                errorCode,
                sourcePath,
                $"Provider '{providerId}' does not exist.");
        }

        var model = modelOverride ?? agent.Model ?? defaults.Model.Value;
        var modelSource = modelOverride is not null
            ? ConfigurationValueSource.CommandLine
            : agent.Model is not null
                ? ConfigurationValueSource.Agent
                : defaults.Model.Source;
        if (!provider.Models.Contains(model, StringComparer.Ordinal))
        {
            throw SelectionError(
                errorCode,
                sourcePath,
                $"Model '{model}' is not provided by Provider '{providerId}'.");
        }

        return new ResolvedStandaloneSelection(
            provider,
            model,
            agent,
            providerSource,
            modelSource,
            agentSource);
    }

    private static StandaloneConfigurationException WorkspaceSelectionError(
        string path,
        string field,
        string message) =>
        new(
            WorkspaceSelectionNotFoundCode,
            $"Workspace configuration '{path}': {field} {message}");

    private static StandaloneConfigurationException SelectionError(
        string code,
        string sourcePath,
        string message) =>
        new(code, $"Configuration selection from '{sourcePath}': {message}");

    private static bool IsGlobalConfigurationException(Exception exception) =>
        exception is ArgumentException
            or ConfigBusyException
            or FileNotFoundException
            or InvalidOperationException
            or IOException
            or JsonException
            or NotSupportedException
            or UnauthorizedAccessException;

    private static string RedactSecrets(string message, StandaloneConfig config)
    {
        foreach (var apiKey in config.Providers
                     .Select(static provider => provider.ApiKey)
                     .Where(static apiKey => !string.IsNullOrEmpty(apiKey))
                     .Distinct(StringComparer.Ordinal))
        {
            message = message.Replace(apiKey, "***", StringComparison.Ordinal);
        }

        return message;
    }
}
