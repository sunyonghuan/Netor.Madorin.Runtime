using System.Collections.Concurrent;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Cli.Config;

/// <summary>
/// Builds a <see cref="Func{NextTurnSelection, IRuntimeProviderAdapter}"/> resolver
/// from the standalone config file so the Runtime server can dispatch to
/// the correct provider without requiring a DI container.
/// </summary>
internal static class StandaloneProviderFactory
{
    public static Func<NextTurnSelection, IRuntimeProviderAdapter> Create(StandaloneConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var cache = new ConcurrentDictionary<string, IRuntimeProviderAdapter>(StringComparer.Ordinal);
        return selection =>
        {
            var providerId = selection.DefaultSelection.ProviderId;
            return cache.GetOrAdd(providerId, id => BuildAdapter(config, id));
        };
    }

    public static IRuntimeProviderAdapter CreateAdapter(
        StandaloneConfig config,
        string providerId)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return BuildAdapter(config, providerId);
    }

    private static IRuntimeProviderAdapter BuildAdapter(StandaloneConfig config, string providerId)
    {
        var entry = config.Providers.FirstOrDefault(p =>
            p.Name.Equals(providerId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Provider '{providerId}' is not configured in ~/.madorin/config.json.");

        if (!StandaloneProviderProtocolCatalog.TryGet(entry.Protocol, out _))
        {
            throw new InvalidOperationException(
                $"Provider '{providerId}' uses unsupported protocol '{entry.Protocol}'.");
        }

        // Shared Protocol → Adapter mapping (host Registry uses the same factory).
        return Server.RuntimeProviderAdapterFactory.Create(
            new Server.ProviderConnectionConfig(
                entry.Name,
                entry.Protocol,
                entry.BaseUrl,
                entry.ApiKey,
                entry.AuthenticationHeader,
                entry.AuthenticationScheme,
                entry.UserAgent,
                entry.ApiVersion,
                entry.Models,
                entry.ConfigurationVersion,
                entry.Capabilities,
                entry.ProbePath,
                entry.MaxTools));
    }
}
