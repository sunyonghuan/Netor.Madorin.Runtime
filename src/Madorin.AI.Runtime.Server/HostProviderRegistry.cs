using System.Collections.Concurrent;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server.ProviderWorkers;

namespace Madorin.AI.Runtime.Server;

/// <summary>
/// In-memory host-mode provider cache keyed by <see cref="HostProviderConfig.ProviderId"/>.
/// 同一连接的模型清单合并，连接和鉴权使用最新配置；进程重启清空缓存。
/// </summary>
public sealed class HostProviderRegistry
{
    private readonly ConcurrentDictionary<string, RegistryEntry> _entries =
        new(StringComparer.Ordinal);
    private readonly IProviderWorkerSupervisor? _workerSupervisor;

    public HostProviderRegistry(IProviderWorkerSupervisor? workerSupervisor = null)
    {
        _workerSupervisor = workerSupervisor;
    }

    public Func<NextTurnSelection, IRuntimeProviderAdapter> CreateResolver() =>
        selection =>
        {
            ArgumentNullException.ThrowIfNull(selection);
            return Resolve(selection.DefaultSelection.ProviderId);
        };

    public void Upsert(IEnumerable<HostProviderConfig> configs)
    {
        ArgumentNullException.ThrowIfNull(configs);
        foreach (var config in configs)
        {
            var normalized = NormalizeAndValidate(config);
            _entries.AddOrUpdate(
                normalized.ProviderId,
                _ => new RegistryEntry(normalized, _workerSupervisor),
                (_, existing) => new RegistryEntry(MergeModels(existing.Config, normalized), _workerSupervisor));
        }
    }

    public bool Contains(string providerId)
    {
        var key = NormalizeKey(providerId);
        return key is not null && _entries.ContainsKey(key);
    }

    public bool TryUpdateCredential(string providerId, string credential)
    {
        var key = NormalizeKey(providerId);
        if (key is null || string.IsNullOrWhiteSpace(credential))
        {
            return false;
        }

        while (_entries.TryGetValue(key, out var existing))
        {
            var updated = existing.Config with { ApiKey = credential.Trim() };
            if (_entries.TryUpdate(key, new RegistryEntry(updated, _workerSupervisor), existing))
            {
                return true;
            }
        }

        return false;
    }

    public IRuntimeProviderAdapter Resolve(string providerId)
    {
        var key = NormalizeKey(providerId)
            ?? throw new ArgumentException("ProviderId is required.", nameof(providerId));

        if (!_entries.TryGetValue(key, out var entry))
        {
            throw new InvalidOperationException(
                $"Provider '{providerId}' is not configured for this Runtime instance.");
        }

        return entry.GetOrCreateAdapter();
    }

    public static HostProviderConfig NormalizeAndValidate(HostProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var providerId = RequireNonEmpty(config.ProviderId, nameof(config.ProviderId));
        var protocol = RequireNonEmpty(config.Protocol, nameof(config.Protocol));
        var baseUrl = RequireNonEmpty(config.BaseUrl, nameof(config.BaseUrl));
        var apiKey = RequireNonEmpty(config.ApiKey, nameof(config.ApiKey));

        if (!RuntimeProviderAdapterFactory.IsKnownProtocol(protocol))
        {
            throw new ArgumentException(
                $"Provider '{providerId}' uses unsupported protocol '{protocol}'.",
                nameof(config));
        }

        return config with
        {
            ProviderId = providerId,
            Protocol = protocol.Trim(),
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            AuthenticationHeader = NormalizeOptional(config.AuthenticationHeader),
            AuthenticationScheme = NormalizeOptional(config.AuthenticationScheme),
            UserAgent = NormalizeOptional(config.UserAgent),
            ApiVersion = NormalizeOptional(config.ApiVersion),
            ConfigurationVersion = NormalizeOptional(config.ConfigurationVersion),
            Models = config.Models?.Select(id => RequireNonEmpty(id, nameof(config.Models)))
                .Distinct(StringComparer.Ordinal).ToArray(),
            ModelDescriptors = NormalizeModels(config.ModelDescriptors)
        };
    }

    private static HostProviderModelConfig[]? NormalizeModels(
        IReadOnlyList<HostProviderModelConfig>? models)
    {
        if (models is null)
        {
            return null;
        }

        var normalized = new Dictionary<string, HostProviderModelConfig>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            ArgumentNullException.ThrowIfNull(model);
            var id = RequireNonEmpty(model.Id, nameof(model.Id));
            if (model.ContextWindow is <= 0 || model.MaxOutputTokens is <= 0)
            {
                throw new ArgumentException($"Model '{id}' limits must be positive when specified.", nameof(models));
            }

            normalized[id] = normalized.TryGetValue(id, out var existing)
                ? MergeModel(existing, model with { Id = id })
                : model with { Id = id };
        }

        return normalized.Values.ToArray();
    }

    private static HostProviderConfig MergeModels(HostProviderConfig existing, HostProviderConfig incoming)
    {
        // 模型 ID 仅在同一连接内有意义，换端点后不能沿用旧端点的限制。
        if (!string.Equals(existing.Protocol, incoming.Protocol, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(existing.BaseUrl, incoming.BaseUrl, StringComparison.Ordinal))
        {
            return incoming;
        }

        var descriptors = new Dictionary<string, HostProviderModelConfig>(StringComparer.Ordinal);
        foreach (var model in existing.ModelDescriptors ?? [])
        {
            descriptors[model.Id] = model;
        }

        foreach (var model in incoming.ModelDescriptors ?? [])
        {
            descriptors[model.Id] = descriptors.TryGetValue(model.Id, out var previous)
                ? MergeModel(previous, model)
                : model;
        }

        return incoming with
        {
            Models = (existing.Models ?? []).Concat(incoming.Models ?? [])
                .Concat(descriptors.Keys).Distinct(StringComparer.Ordinal).ToArray(),
            ModelDescriptors = descriptors.Count == 0 ? null : descriptors.Values.ToArray()
        };
    }

    private static HostProviderModelConfig MergeModel(
        HostProviderModelConfig existing,
        HostProviderModelConfig incoming) => incoming with
        {
            ContextWindow = incoming.ContextWindow ?? existing.ContextWindow,
            MaxOutputTokens = incoming.MaxOutputTokens ?? existing.MaxOutputTokens
        };

    private static string? NormalizeKey(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        return providerId.Trim();
    }

    private static string RequireNonEmpty(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} is required.", name);
        }

        return value.Trim();
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? value : value.Trim();

    private sealed class RegistryEntry(HostProviderConfig config, IProviderWorkerSupervisor? workerSupervisor)
    {
        private readonly object _gate = new();
        private IRuntimeProviderAdapter? _adapter;

        public HostProviderConfig Config { get; } = config;

        public IRuntimeProviderAdapter GetOrCreateAdapter()
        {
            lock (_gate)
            {
                return _adapter ??= CreateAdapter();
            }
        }

        private IRuntimeProviderAdapter CreateAdapter()
        {
            var connection = new ProviderConnectionConfig(
                Config.ProviderId,
                Config.Protocol,
                Config.BaseUrl,
                Config.ApiKey,
                Config.AuthenticationHeader,
                Config.AuthenticationScheme,
                Config.UserAgent,
                Config.ApiVersion,
                Config.Models,
                Config.ConfigurationVersion,
                ModelDescriptors: Config.ModelDescriptors?.Select(static model => new ProviderModel(
                    model.Id, model.Id, ContextWindow: model.ContextWindow,
                    MaxOutputTokens: model.MaxOutputTokens)).ToArray());
            if (!RuntimeProviderAdapterFactory.TryMapProtocolToSlug(connection.Protocol, out _))
            {
                throw new InvalidOperationException(
                    $"Provider '{Config.ProviderId}' uses unsupported protocol '{connection.Protocol}'.");
            }

            if (workerSupervisor is null)
            {
                throw new InvalidOperationException(
                    $"Provider '{Config.ProviderId}' protocol '{connection.Protocol}' requires a provider worker supervisor.");
            }

            return workerSupervisor.CreateAdapter(connection.Protocol, connection);
        }
    }
}
