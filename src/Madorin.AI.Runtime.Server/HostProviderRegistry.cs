using System.Collections.Concurrent;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Server;

/// <summary>
/// In-memory host-mode provider cache keyed by <see cref="HostProviderConfig.ProviderId"/>.
/// Same Id upsert overwrites; process restart clears all entries.
/// </summary>
public sealed class HostProviderRegistry
{
    private readonly ConcurrentDictionary<string, RegistryEntry> _entries =
        new(StringComparer.Ordinal);

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
            _entries[normalized.ProviderId] = new RegistryEntry(normalized);
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

        if (!_entries.TryGetValue(key, out var existing))
        {
            return false;
        }

        var updated = existing.Config with { ApiKey = credential.Trim() };
        _entries[key] = new RegistryEntry(updated);
        return true;
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
            Models = config.Models
        };
    }

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

    private sealed class RegistryEntry(HostProviderConfig config)
    {
        private readonly object _gate = new();
        private IRuntimeProviderAdapter? _adapter;

        public HostProviderConfig Config { get; } = config;

        public IRuntimeProviderAdapter GetOrCreateAdapter()
        {
            lock (_gate)
            {
                return _adapter ??= RuntimeProviderAdapterFactory.Create(
                    new ProviderConnectionConfig(
                        Config.ProviderId,
                        Config.Protocol,
                        Config.BaseUrl,
                        Config.ApiKey,
                        Config.AuthenticationHeader,
                        Config.AuthenticationScheme,
                        Config.UserAgent,
                        Config.ApiVersion,
                        Config.Models,
                        Config.ConfigurationVersion));
            }
        }
    }
}
