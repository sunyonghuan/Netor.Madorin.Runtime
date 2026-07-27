using System.Text.Json;

using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.MConsole.Serialization;

namespace Madorin.AI.Runtime.MConsole.Callbacks;

public sealed class ToolCatalogService(IRuntimeClient client)
{
    private readonly IRuntimeClient _client = client;

    public static async Task<ToolCatalogDraft> LoadDraftAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        await using var stream = new FileStream(fullPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });
        var draft = await JsonSerializer.DeserializeAsync(
                stream,
                MConsoleJsonContext.Default.ToolCatalogDraft,
                cancellationToken)
            ?? throw new InvalidDataException($"Tool catalog draft '{fullPath}' is empty.");
        Validate(draft);
        return draft;
    }

    public async ValueTask<ToolCatalogUpdateResponse> ReplaceAsync(
        ToolCatalogDraft draft,
        CancellationToken cancellationToken = default)
    {
        Validate(draft);
        return await _client.ReplaceToolCatalogAsync(
            new ToolCatalogReplaceRequest(draft.CatalogVersion, draft.Tools),
            cancellationToken);
    }

    public async ValueTask<ToolCatalogUpdateResponse> PatchAsync(
        string baseVersion,
        ToolCatalogDraft next,
        IEnumerable<string> removals,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseVersion);
        ArgumentNullException.ThrowIfNull(removals);
        Validate(next);
        var normalizedRemovals = removals
            .Select(static item => item?.Trim())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .Select(static item => item!)
            .ToArray();
        return await _client.PatchToolCatalogAsync(
            new ToolCatalogPatchRequest(
                baseVersion,
                next.CatalogVersion,
                next.Tools,
                normalizedRemovals),
            cancellationToken);
    }

    public ValueTask<GrantRevokeResponse> RevokeGrantAsync(
        string grantId,
        string? reason = null,
        bool revokeDescendants = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grantId);
        return _client.RevokeGrantAsync(
            new GrantRevokeRequest(grantId, revokeDescendants, reason),
            cancellationToken);
    }

    public static ToolCatalogDraft Upsert(
        ToolCatalogDraft draft,
        ToolCatalogItem tool,
        string newVersion)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentException.ThrowIfNullOrWhiteSpace(newVersion);
        var tools = draft.Tools
            .Where(item => !string.Equals(item.ToolId, tool.ToolId, StringComparison.Ordinal))
            .Append(tool)
            .OrderBy(static item => item.ToolId, StringComparer.Ordinal)
            .ToArray();
        var result = new ToolCatalogDraft(newVersion, tools);
        Validate(result);
        return result;
    }

    public static ToolCatalogDraft Remove(
        ToolCatalogDraft draft,
        string toolId,
        string newVersion)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newVersion);
        var result = new ToolCatalogDraft(
            newVersion,
            draft.Tools
                .Where(item => !string.Equals(item.ToolId, toolId, StringComparison.Ordinal))
                .ToArray());
        Validate(result);
        return result;
    }

    public static void Validate(ToolCatalogDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.CatalogVersion);
        ArgumentNullException.ThrowIfNull(draft.Tools);
        var duplicate = draft.Tools
            .GroupBy(static item => item.ToolId, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"Tool catalog contains duplicate toolId '{duplicate.Key}'.");
        }

        foreach (var tool in draft.Tools)
        {
            if (string.IsNullOrWhiteSpace(tool.ToolId)
                || string.IsNullOrWhiteSpace(tool.DisplayName)
                || tool.InputSchema.ValueKind is not JsonValueKind.Object
                || tool.OutputSchema.ValueKind is not JsonValueKind.Object
                || tool.TimeoutSeconds <= 0)
            {
                throw new InvalidDataException(
                    $"Tool catalog entry '{tool.ToolId}' is incomplete or invalid.");
            }
        }
    }
}
