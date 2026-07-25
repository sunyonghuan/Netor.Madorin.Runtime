using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Cli.Config;

public static class ConfigWizard
{
    public static Task<StandaloneConfig> RunInitAsync(
        string configDirectory,
        ICliTerminal terminal,
        CancellationToken ct = default) =>
        RunInitAsync(
            configDirectory,
            terminal,
            StandaloneProviderFactory.CreateAdapter,
            ct);

    internal static async Task<StandaloneConfig> RunInitAsync(
        string configDirectory,
        ICliTerminal terminal,
        Func<StandaloneConfig, string, IRuntimeProviderAdapter> providerAdapterFactory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(providerAdapterFactory);
        var loader = new StandaloneConfigLoader(configDirectory);
        var existing = await loader.LoadAsync(ct).ConfigureAwait(false);
        if (existing is not null
            && !await ConfirmAsync(
                    terminal,
                    $"Configuration already exists at '{loader.ConfigPath}'. Rebuild it? [y/N]: ",
                    defaultValue: false,
                    ct)
                .ConfigureAwait(false))
        {
            return existing;
        }

        terminal.WriteLine("Madorin standalone configuration");
        var config = await ReadConfigAsync(
                terminal,
                loader.ConfigPath,
                existing,
                providerAdapterFactory,
                ct)
            .ConfigureAwait(false);
        var existingAgents = await loader.LoadAgentDocumentsAsync(ct).ConfigureAwait(false);
        var existingDefaultAgent = existingAgents
            .FirstOrDefault(document =>
                document.Agent.Id.Equals(config.DefaultAgent, StringComparison.Ordinal))
            ?.Agent;
        var systemPrompt = await ReadRawValueAsync(
                terminal,
                "System prompt",
                existingDefaultAgent?.SystemPrompt ?? "You are a helpful AI assistant.",
                ct)
            .ConfigureAwait(false);

        var defaultAgent = new AgentConfig
        {
            Id = config.DefaultAgent,
            Name = existingDefaultAgent?.Name ?? "Default",
            Description = existingDefaultAgent?.Description ?? "Default standalone agent.",
            SystemPrompt = systemPrompt,
            Provider = null,
            Model = null,
            Temperature = existingDefaultAgent?.Temperature ?? 0.7f
        };
        var prospectiveAgents = existingAgents
            .Where(document => !document.Agent.Id.Equals(config.DefaultAgent, StringComparison.Ordinal))
            .Append(new AgentConfigDocument(
                Path.Combine(loader.AgentsDirectory, $"{config.DefaultAgent}.json"),
                defaultAgent))
            .ToList();
        var errors = StandaloneConfigValidator.Validate(
            config,
            loader.ConfigPath,
            prospectiveAgents);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }

        if (existing is null)
        {
            config = await loader.UpdateConfigAsync(current => current ?? config, ct)
                .ConfigureAwait(false);
        }
        else
        {
            var backupPath = await loader.ReplaceConfigWithBackupAsync(config, ct)
                .ConfigureAwait(false);
            terminal.WriteLine($"Backup created: {backupPath}");
        }

        await loader.UpdateAgentAsync(
                config.DefaultAgent,
                current => current is null
                    ? defaultAgent
                    : new AgentConfig
                    {
                        Id = config.DefaultAgent,
                        Name = current.Name,
                        Description = current.Description,
                        SystemPrompt = defaultAgent.SystemPrompt,
                        Provider = null,
                        Model = null,
                        Temperature = current.Temperature
                    },
                ct)
            .ConfigureAwait(false);

        return config;
    }

    public static Task<StandaloneConfig> RunEditAsync(
        string configDirectory,
        ICliTerminal terminal,
        CancellationToken ct = default) =>
        RunEditAsync(
            configDirectory,
            terminal,
            StandaloneProviderFactory.CreateAdapter,
            ct);

    internal static async Task<StandaloneConfig> RunEditAsync(
        string configDirectory,
        ICliTerminal terminal,
        Func<StandaloneConfig, string, IRuntimeProviderAdapter> providerAdapterFactory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(providerAdapterFactory);
        var loader = new StandaloneConfigLoader(configDirectory);
        var existing = await loader.LoadAsync(ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException(
                "The standalone configuration does not exist.",
                loader.ConfigPath);
        var agents = await loader.LoadAgentDocumentsAsync(ct).ConfigureAwait(false);
        var target = await ReadEditTargetAsync(terminal, ct).ConfigureAwait(false);
        return target switch
        {
            ConfigEditTarget.Provider => await EditProviderAsync(
                    loader,
                    existing,
                    agents,
                    terminal,
                    providerAdapterFactory,
                    ct)
                .ConfigureAwait(false),
            _ => await EditDefaultsAsync(
                    loader,
                    existing,
                    agents,
                    terminal,
                    ct)
                .ConfigureAwait(false)
        };
    }

    private static async Task<StandaloneConfig> ReadConfigAsync(
        ICliTerminal terminal,
        string configPath,
        StandaloneConfig? existing,
        Func<StandaloneConfig, string, IRuntimeProviderAdapter> providerAdapterFactory,
        CancellationToken ct)
    {
        var existingProvider = existing?.Providers.FirstOrDefault(provider =>
            provider.Name.Equals(existing.DefaultProvider, StringComparison.Ordinal));
        var protocol = await ReadProtocolAsync(
                terminal,
                existingProvider?.Protocol ?? "OpenAI",
                ct)
            .ConfigureAwait(false);
        var protocolDefinition = StandaloneProviderProtocolCatalog.GetRequired(protocol);
        var providerName = await ReadRequiredValueAsync(
                terminal,
                "Provider name",
                existingProvider?.Name ?? "openai",
                ct)
            .ConfigureAwait(false);
        var baseUrl = await ReadBaseUrlAsync(
                terminal,
                existingProvider?.BaseUrl ?? protocolDefinition.DefaultBaseUrl,
                protocolDefinition.IsBaseUrlRequired,
                ct)
            .ConfigureAwait(false);

        var apiKey = await terminal.ReadSecretAsync(
                existingProvider is null
                    ? "API key: "
                    : $"API key [{StandaloneConfigLoader.MaskApiKey(existingProvider.ApiKey)}; Enter to keep]: ",
                ct)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(apiKey))
        {
            apiKey = existingProvider?.ApiKey ?? string.Empty;
        }

        while (protocolDefinition.IsApiKeyRequired && string.IsNullOrWhiteSpace(apiKey))
        {
            terminal.WriteLine("API key is required.");
            apiKey = await terminal.ReadSecretAsync("API key: ", ct).ConfigureAwait(false);
        }

        IReadOnlyList<string> defaultModels = existingProvider?.Models
            ?? [protocolDefinition.DefaultModel];
        var provider = new ProviderEntry
        {
            Name = providerName,
            Protocol = protocol,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            AuthenticationHeader = existingProvider?.AuthenticationHeader
                ?? protocolDefinition.DefaultAuthenticationHeader,
            AuthenticationScheme = existingProvider?.AuthenticationScheme
                ?? protocolDefinition.DefaultAuthenticationScheme,
            ApiVersion = existingProvider?.ApiVersion,
            Capabilities = existingProvider?.Capabilities,
            ConfigurationVersion = existingProvider?.ConfigurationVersion ?? "1",
            ProbePath = existingProvider?.ProbePath ?? protocolDefinition.DefaultProbePath,
            Models = CreateProvisionalModels(defaultModels)
        };
        var models = await ReadModelsAsync(
                terminal,
                provider,
                defaultModels,
                providerAdapterFactory,
                ct)
            .ConfigureAwait(false);
        provider.Models = models;
        var defaultModel = await ReadDefaultModelAsync(
                terminal,
                models,
                existing is not null && models.Contains(existing.DefaultModel, StringComparer.Ordinal)
                    ? existing.DefaultModel
                    : models[0],
                ct)
            .ConfigureAwait(false);

        var providers = existing?.Providers.ToList() ?? [];
        if (existingProvider is null)
        {
            providers.Add(provider);
        }
        else
        {
            providers[providers.IndexOf(existingProvider)] = provider;
        }

        var result = new StandaloneConfig
        {
            DefaultProvider = providerName,
            DefaultModel = defaultModel,
            DefaultAgent = existing?.DefaultAgent ?? "default",
            Providers = providers
        };
        var errors = StandaloneConfigValidator.Validate(result, configPath);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }

        return result;
    }

    private static async Task<StandaloneConfig> EditProviderAsync(
        StandaloneConfigLoader loader,
        StandaloneConfig existing,
        IReadOnlyList<AgentConfigDocument> agents,
        ICliTerminal terminal,
        Func<StandaloneConfig, string, IRuntimeProviderAdapter> providerAdapterFactory,
        CancellationToken ct)
    {
        var selection = await ReadProviderSelectionAsync(existing, terminal, ct)
            .ConfigureAwait(false);
        var editedProvider = await ReadProviderAsync(
                selection.Provider,
                terminal,
                providerAdapterFactory,
                ct)
            .ConfigureAwait(false);

        return await loader.UpdateConfigAsync(latest =>
        {
            if (latest is null)
            {
                throw new FileNotFoundException(
                    "The standalone configuration no longer exists.",
                    loader.ConfigPath);
            }

            var providers = latest.Providers.ToList();
            if (selection.OriginalName is null)
            {
                if (providers.Any(provider =>
                        provider.Name.Equals(editedProvider.Name, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"Provider '{editedProvider.Name}' already exists.");
                }

                providers.Add(editedProvider);
            }
            else
            {
                var index = providers.FindIndex(provider =>
                    provider.Name.Equals(selection.OriginalName, StringComparison.Ordinal));
                if (index < 0)
                {
                    throw new InvalidOperationException(
                        $"Provider '{selection.OriginalName}' no longer exists; configuration was not overwritten.");
                }

                if (!editedProvider.Name.Equals(selection.OriginalName, StringComparison.Ordinal)
                    && providers.Any(provider =>
                        provider.Name.Equals(editedProvider.Name, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"Provider '{editedProvider.Name}' already exists.");
                }

                providers[index] = editedProvider;
            }

            var defaultProvider = latest.DefaultProvider;
            var defaultModel = latest.DefaultModel;
            if (selection.OriginalName is not null
                && latest.DefaultProvider.Equals(selection.OriginalName, StringComparison.Ordinal))
            {
                defaultProvider = editedProvider.Name;
                if (!editedProvider.Models.Contains(defaultModel, StringComparer.Ordinal))
                {
                    defaultModel = editedProvider.Models[0];
                }
            }

            var updated = new StandaloneConfig
            {
                DefaultProvider = defaultProvider,
                DefaultModel = defaultModel,
                DefaultAgent = latest.DefaultAgent,
                Providers = providers
            };
            ValidateOrThrow(updated, loader.ConfigPath, agents);
            return updated;
        }, ct).ConfigureAwait(false);
    }

    private static async Task<StandaloneConfig> EditDefaultsAsync(
        StandaloneConfigLoader loader,
        StandaloneConfig existing,
        IReadOnlyList<AgentConfigDocument> agents,
        ICliTerminal terminal,
        CancellationToken ct)
    {
        var defaultProvider = await ReadDefaultProviderAsync(existing, terminal, ct)
            .ConfigureAwait(false);
        var provider = existing.Providers.First(candidate =>
            candidate.Name.Equals(defaultProvider, StringComparison.Ordinal));
        var currentModel = provider.Models.Contains(existing.DefaultModel, StringComparer.Ordinal)
            ? existing.DefaultModel
            : provider.Models[0];
        var defaultModel = await ReadDefaultModelAsync(
                terminal,
                provider.Models,
                currentModel,
                ct)
            .ConfigureAwait(false);
        var defaultAgent = await ReadDefaultAgentAsync(
                agents,
                existing.DefaultAgent,
                terminal,
                ct)
            .ConfigureAwait(false);

        return await loader.UpdateConfigAsync(latest =>
        {
            if (latest is null)
            {
                throw new FileNotFoundException(
                    "The standalone configuration no longer exists.",
                    loader.ConfigPath);
            }

            var latestProvider = latest.Providers.FirstOrDefault(candidate =>
                candidate.Name.Equals(defaultProvider, StringComparison.Ordinal));
            if (latestProvider is null
                || !latestProvider.Models.Contains(defaultModel, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "The selected default Provider or model changed before the configuration lock was acquired.");
            }

            var updated = new StandaloneConfig
            {
                DefaultProvider = defaultProvider,
                DefaultModel = defaultModel,
                DefaultAgent = defaultAgent,
                Providers = latest.Providers.ToList()
            };
            ValidateOrThrow(updated, loader.ConfigPath, agents);
            return updated;
        }, ct).ConfigureAwait(false);
    }

    private static async Task<ConfigEditTarget> ReadEditTargetAsync(
        ICliTerminal terminal,
        CancellationToken ct)
    {
        terminal.WriteLine("Edit target:");
        terminal.WriteLine("1. Provider");
        terminal.WriteLine("2. Common defaults");
        while (true)
        {
            var input = await ReadValueAsync(terminal, "Edit target", "1", ct)
                .ConfigureAwait(false);
            if (input.Equals("1", StringComparison.Ordinal)
                || input.Equals("provider", StringComparison.OrdinalIgnoreCase))
            {
                return ConfigEditTarget.Provider;
            }

            if (input.Equals("2", StringComparison.Ordinal)
                || input.Equals("defaults", StringComparison.OrdinalIgnoreCase)
                || input.Equals("common defaults", StringComparison.OrdinalIgnoreCase))
            {
                return ConfigEditTarget.Defaults;
            }

            terminal.WriteLine("Choose 1 for Provider or 2 for common defaults.");
        }
    }

    private static async Task<ProviderEditSelection> ReadProviderSelectionAsync(
        StandaloneConfig config,
        ICliTerminal terminal,
        CancellationToken ct)
    {
        terminal.WriteLine("Providers:");
        for (var index = 0; index < config.Providers.Count; index++)
        {
            terminal.WriteLine($"{index + 1}. {config.Providers[index].Name}");
        }

        terminal.WriteLine($"{config.Providers.Count + 1}. Add Provider");
        while (true)
        {
            var input = await ReadValueAsync(terminal, "Provider", "1", ct)
                .ConfigureAwait(false);
            if (int.TryParse(input, out var selectedIndex))
            {
                if (selectedIndex >= 1 && selectedIndex <= config.Providers.Count)
                {
                    var provider = config.Providers[selectedIndex - 1];
                    return new ProviderEditSelection(provider.Name, provider);
                }

                if (selectedIndex == config.Providers.Count + 1)
                {
                    return new ProviderEditSelection(null, null);
                }
            }

            var namedProvider = config.Providers.FirstOrDefault(provider =>
                provider.Name.Equals(input, StringComparison.Ordinal));
            if (namedProvider is not null)
            {
                return new ProviderEditSelection(namedProvider.Name, namedProvider);
            }

            if (input.Equals("new", StringComparison.OrdinalIgnoreCase)
                || input.Equals("add", StringComparison.OrdinalIgnoreCase))
            {
                return new ProviderEditSelection(null, null);
            }

            terminal.WriteLine("Choose a Provider number, exact name, or 'new'.");
        }
    }

    private static async Task<ProviderEntry> ReadProviderAsync(
        ProviderEntry? existing,
        ICliTerminal terminal,
        Func<StandaloneConfig, string, IRuntimeProviderAdapter> providerAdapterFactory,
        CancellationToken ct)
    {
        var protocol = await ReadProtocolAsync(
                terminal,
                existing?.Protocol ?? "OpenAI",
                ct)
            .ConfigureAwait(false);
        var protocolDefinition = StandaloneProviderProtocolCatalog.GetRequired(protocol);
        var providerName = await ReadRequiredValueAsync(
                terminal,
                "Provider name",
                existing?.Name ?? "provider",
                ct)
            .ConfigureAwait(false);
        var baseUrl = await ReadBaseUrlAsync(
                terminal,
                existing?.BaseUrl ?? protocolDefinition.DefaultBaseUrl,
                protocolDefinition.IsBaseUrlRequired,
                ct)
            .ConfigureAwait(false);
        var apiKey = await terminal.ReadSecretAsync(
                existing is null
                    ? "API key: "
                    : $"API key [{StandaloneConfigLoader.MaskApiKey(existing.ApiKey)}; Enter to keep]: ",
                ct)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(apiKey))
        {
            apiKey = existing?.ApiKey ?? string.Empty;
        }

        while (protocolDefinition.IsApiKeyRequired && string.IsNullOrWhiteSpace(apiKey))
        {
            terminal.WriteLine("API key is required.");
            apiKey = await terminal.ReadSecretAsync("API key: ", ct).ConfigureAwait(false);
        }

        IReadOnlyList<string> defaultModels = existing?.Models
            ?? [protocolDefinition.DefaultModel];
        var provider = new ProviderEntry
        {
            Name = providerName,
            Protocol = protocol,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            AuthenticationHeader = existing?.AuthenticationHeader
                ?? protocolDefinition.DefaultAuthenticationHeader,
            AuthenticationScheme = existing?.AuthenticationScheme
                ?? protocolDefinition.DefaultAuthenticationScheme,
            ApiVersion = existing?.ApiVersion,
            Capabilities = existing?.Capabilities,
            ConfigurationVersion = existing?.ConfigurationVersion ?? "1",
            ProbePath = existing?.ProbePath ?? protocolDefinition.DefaultProbePath,
            Models = CreateProvisionalModels(defaultModels)
        };
        provider.Models = await ReadModelsAsync(
                terminal,
                provider,
                defaultModels,
                providerAdapterFactory,
                ct)
            .ConfigureAwait(false);
        return provider;
    }

    private static async Task<string> ReadDefaultProviderAsync(
        StandaloneConfig config,
        ICliTerminal terminal,
        CancellationToken ct)
    {
        terminal.WriteLine("Available Providers:");
        for (var index = 0; index < config.Providers.Count; index++)
        {
            terminal.WriteLine($"{index + 1}. {config.Providers[index].Name}");
        }

        while (true)
        {
            var input = await ReadValueAsync(
                    terminal,
                    "Default Provider",
                    config.DefaultProvider,
                    ct)
                .ConfigureAwait(false);
            if (int.TryParse(input, out var selectedIndex)
                && selectedIndex >= 1
                && selectedIndex <= config.Providers.Count)
            {
                return config.Providers[selectedIndex - 1].Name;
            }

            if (config.Providers.Any(provider =>
                    provider.Name.Equals(input, StringComparison.Ordinal)))
            {
                return input;
            }

            terminal.WriteLine("Choose a Provider number or exact name.");
        }
    }

    private static async Task<string> ReadDefaultAgentAsync(
        IReadOnlyList<AgentConfigDocument> agents,
        string currentAgent,
        ICliTerminal terminal,
        CancellationToken ct)
    {
        if (agents.Count == 0)
        {
            throw new InvalidOperationException("At least one Agent is required before editing defaults.");
        }

        terminal.WriteLine("Available Agents:");
        for (var index = 0; index < agents.Count; index++)
        {
            terminal.WriteLine($"{index + 1}. {agents[index].Agent.Id}");
        }

        while (true)
        {
            var input = await ReadValueAsync(terminal, "Default Agent", currentAgent, ct)
                .ConfigureAwait(false);
            if (int.TryParse(input, out var selectedIndex)
                && selectedIndex >= 1
                && selectedIndex <= agents.Count)
            {
                return agents[selectedIndex - 1].Agent.Id;
            }

            if (agents.Any(document =>
                    document.Agent.Id.Equals(input, StringComparison.Ordinal)))
            {
                return input;
            }

            terminal.WriteLine("Choose an Agent number or exact id.");
        }
    }

    private static void ValidateOrThrow(
        StandaloneConfig config,
        string configPath,
        IReadOnlyList<AgentConfigDocument> agents)
    {
        var errors = StandaloneConfigValidator.Validate(config, configPath, agents);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }
    }

    private static async Task<string> ReadProtocolAsync(
        ICliTerminal terminal,
        string defaultValue,
        CancellationToken ct)
    {
        while (true)
        {
            var options = string.Join(
                ", ",
                StandaloneProviderProtocolCatalog.WizardEntries.Select(
                    static (definition, index) => $"{index + 1}={definition.DisplayName}"));
            terminal.Write($"Protocol ({options}) [{defaultValue}]: ");
            var input = (await terminal.ReadLineAsync(ct).ConfigureAwait(false))?.Trim();
            if (string.IsNullOrEmpty(input))
            {
                return defaultValue;
            }

            var definition = StandaloneProviderProtocolCatalog.ResolveWizardInput(input);
            if (definition is not null)
            {
                return definition.Protocol;
            }

            terminal.WriteLine(
                $"Choose 1 through {StandaloneProviderProtocolCatalog.WizardEntries.Count}, or enter a protocol name.");
        }
    }

    private static Task<string> ReadBaseUrlAsync(
        ICliTerminal terminal,
        string defaultValue,
        bool isRequired,
        CancellationToken ct) =>
        isRequired
            ? ReadRequiredValueAsync(terminal, "Base URL", defaultValue, ct)
            : ReadValueAsync(terminal, "Base URL", defaultValue, ct);

    private static List<string> CreateProvisionalModels(IReadOnlyList<string> defaultValues)
    {
        var models = defaultValues
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (models.Count == 0)
        {
            models.Add("model-discovery-placeholder");
        }

        return models;
    }

    private static async Task<List<string>> ReadModelsAsync(
        ICliTerminal terminal,
        ProviderEntry provider,
        IReadOnlyList<string> defaultValues,
        Func<StandaloneConfig, string, IRuntimeProviderAdapter> providerAdapterFactory,
        CancellationToken ct)
    {
        while (true)
        {
            var input = await ReadValueAsync(
                    terminal,
                    "Models (comma-separated)",
                    string.Join(", ", defaultValues),
                    ct)
                .ConfigureAwait(false);
            if (input.Equals("discover", StringComparison.OrdinalIgnoreCase))
            {
                var discovered = await DiscoverModelsAsync(
                        provider,
                        providerAdapterFactory,
                        terminal,
                        ct)
                    .ConfigureAwait(false);
                if (discovered is not null)
                {
                    return discovered;
                }

                continue;
            }

            var models = input
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (models.Count > 0)
            {
                return models;
            }

            terminal.WriteLine("At least one model is required.");
        }
    }

    private static async Task<List<string>?> DiscoverModelsAsync(
        ProviderEntry provider,
        Func<StandaloneConfig, string, IRuntimeProviderAdapter> providerAdapterFactory,
        ICliTerminal terminal,
        CancellationToken ct)
    {
        IRuntimeProviderAdapter? adapter = null;
        try
        {
            var discoveryConfig = new StandaloneConfig
            {
                DefaultProvider = provider.Name,
                DefaultModel = provider.Models[0],
                Providers = [provider]
            };
            adapter = providerAdapterFactory(discoveryConfig, provider.Name);
            if (adapter is not IRuntimeProviderModelCatalogSource modelCatalogSource)
            {
                terminal.WriteLine(
                    "Model discovery is not supported by this Provider; enter models manually.");
                return null;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            var models = await modelCatalogSource.FetchModelsAsync(timeoutCts.Token)
                .ConfigureAwait(false);
            var modelIds = models
                .Select(static model => model.Id)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (modelIds.Count == 0)
            {
                terminal.WriteLine("Model discovery returned no models; enter models manually.");
                return null;
            }

            terminal.WriteLine($"Discovered {modelIds.Count} model(s).");
            return modelIds;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            terminal.WriteLine(
                $"Model discovery failed: {ProviderFailureDiagnostics.Describe(ex)} Enter models manually.");
            return null;
        }
        finally
        {
            await DisposeAdapterAsync(adapter).ConfigureAwait(false);
        }
    }

    private static async ValueTask DisposeAdapterAsync(IRuntimeProviderAdapter? adapter)
    {
        if (adapter is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (adapter is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static async Task<string> ReadDefaultModelAsync(
        ICliTerminal terminal,
        List<string> models,
        string defaultValue,
        CancellationToken ct)
    {
        terminal.WriteLine("Available models:");
        for (var index = 0; index < models.Count; index++)
        {
            terminal.WriteLine($"{index + 1}. {models[index]}");
        }

        while (true)
        {
            var input = await ReadValueAsync(terminal, "Default model", defaultValue, ct)
                .ConfigureAwait(false);
            if (int.TryParse(input, out var selectedIndex)
                && selectedIndex >= 1
                && selectedIndex <= models.Count)
            {
                return models[selectedIndex - 1];
            }

            if (models.Contains(input, StringComparer.Ordinal))
            {
                return input;
            }

            terminal.WriteLine("Choose a model number or exact model name.");
        }
    }

    private static async Task<string> ReadRequiredValueAsync(
        ICliTerminal terminal,
        string prompt,
        string defaultValue,
        CancellationToken ct)
    {
        while (true)
        {
            var value = await ReadValueAsync(terminal, prompt, defaultValue, ct)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            terminal.WriteLine($"{prompt} is required.");
        }
    }

    private static async Task<string> ReadValueAsync(
        ICliTerminal terminal,
        string prompt,
        string defaultValue,
        CancellationToken ct)
    {
        var defaultPrompt = string.IsNullOrEmpty(defaultValue) ? string.Empty : $" [{defaultValue}]";
        terminal.Write($"{prompt}{defaultPrompt}: ");
        var input = await terminal.ReadLineAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(input) ? defaultValue : input.Trim();
    }

    private static async Task<string> ReadRawValueAsync(
        ICliTerminal terminal,
        string prompt,
        string defaultValue,
        CancellationToken ct)
    {
        terminal.Write($"{prompt} [Enter to keep current]: ");
        var input = await terminal.ReadLineAsync(ct).ConfigureAwait(false);
        return string.IsNullOrEmpty(input) ? defaultValue : input;
    }

    private static async Task<bool> ConfirmAsync(
        ICliTerminal terminal,
        string prompt,
        bool defaultValue,
        CancellationToken ct)
    {
        terminal.Write(prompt);
        var input = (await terminal.ReadLineAsync(ct).ConfigureAwait(false))?.Trim();
        if (string.IsNullOrEmpty(input))
        {
            return defaultValue;
        }

        return input.Equals("y", StringComparison.OrdinalIgnoreCase)
            || input.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private enum ConfigEditTarget
    {
        Provider,
        Defaults
    }

    private sealed record ProviderEditSelection(
        string? OriginalName,
        ProviderEntry? Provider);
}
