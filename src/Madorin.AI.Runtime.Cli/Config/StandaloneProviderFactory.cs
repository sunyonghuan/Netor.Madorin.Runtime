using System.Collections.Concurrent;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server.ProviderWorkers;

namespace Madorin.AI.Runtime.Cli.Config;

/// <summary>
/// Builds a <see cref="Func{NextTurnSelection, IRuntimeProviderAdapter}"/> resolver
/// from the standalone config file so the Runtime server can dispatch to
/// the correct provider without requiring a DI container.
/// </summary>
internal static class StandaloneProviderFactory
{
    public static Func<NextTurnSelection, IRuntimeProviderAdapter> Create(StandaloneConfig config) =>
        Create(config, workerSupervisor: null);

    public static Func<NextTurnSelection, IRuntimeProviderAdapter> Create(
        StandaloneConfig config,
        IProviderWorkerSupervisor? workerSupervisor)
    {
        ArgumentNullException.ThrowIfNull(config);
        var cache = new ConcurrentDictionary<string, IRuntimeProviderAdapter>(StringComparer.Ordinal);
        return selection =>
        {
            var providerId = selection.DefaultSelection.ProviderId;
            return cache.GetOrAdd(providerId, id => BuildAdapter(config, id, workerSupervisor));
        };
    }

    public static IRuntimeProviderAdapter CreateAdapter(
        StandaloneConfig config,
        string providerId) =>
        CreateAdapter(config, providerId, workerSupervisor: null);

    public static IRuntimeProviderAdapter CreateAdapter(
        StandaloneConfig config,
        string providerId,
        IProviderWorkerSupervisor? workerSupervisor)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return BuildAdapter(config, providerId, workerSupervisor);
    }

    private static IRuntimeProviderAdapter BuildAdapter(
        StandaloneConfig config,
        string providerId,
        IProviderWorkerSupervisor? workerSupervisor)
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

        var connection = new ProviderConnectionConfig(
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
            entry.MaxTools);

        if (!Server.RuntimeProviderAdapterFactory.TryMapProtocolToSlug(connection.Protocol, out _))
        {
            throw new InvalidOperationException(
                $"Provider '{providerId}' uses unsupported protocol '{connection.Protocol}'.");
        }

        if (workerSupervisor is null)
        {
            throw new InvalidOperationException(
                $"Provider '{providerId}' protocol '{connection.Protocol}' requires a provider worker supervisor.");
        }

        return workerSupervisor.CreateAdapter(connection.Protocol, connection);
    }
}
