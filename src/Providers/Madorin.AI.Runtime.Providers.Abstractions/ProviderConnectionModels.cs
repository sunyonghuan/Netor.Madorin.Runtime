namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>合并显式模型限制、兼容 ID 清单和已知目录，不为未知 ID 推测窗口。</summary>
public static class ProviderConnectionModels
{
    public static IReadOnlyList<ProviderModel> Resolve(
        ProviderConnectionConfig connection,
        IReadOnlyList<ProviderModel>? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var models = new Dictionary<string, ProviderModel>(StringComparer.Ordinal);
        foreach (var id in connection.Models ?? [])
        {
            models.TryAdd(id, FindKnown(catalog, id) ?? new ProviderModel(id, id));
        }

        foreach (var model in connection.ModelDescriptors ?? [])
        {
            var fallback = models.GetValueOrDefault(model.Id) ?? FindKnown(catalog, model.Id);
            models[model.Id] = model with
            {
                ContextWindow = model.ContextWindow ?? fallback?.ContextWindow,
                MaxOutputTokens = model.MaxOutputTokens ?? fallback?.MaxOutputTokens,
                Capabilities = model.Capabilities ?? fallback?.Capabilities
            };
        }

        return models.Count == 0 ? catalog ?? [] : models.Values.ToArray();
    }

    private static ProviderModel? FindKnown(IReadOnlyList<ProviderModel>? catalog, string id) =>
        catalog?.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.Ordinal));
}
