using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Modes.Expert;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Server;

internal sealed partial class SessionCompactionService
{
    public async Task<SessionContextStatusDto?> GetStatusAsync(
        string sessionId, string? operationId = null, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_databaseConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        var snapshot = await new SqliteSessionRepository(connection).GetSessionSnapshotAsync(sessionId, ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            return null;
        }
        var selection = snapshot.SelectionJson is null ? null
            : JsonSerializer.Deserialize(snapshot.SelectionJson, RuntimeJsonContext.Default.NextTurnSelection);
        var expert = selection?.ModeOptions as ExpertModeOptions;
        var promptHash = expert is null ? null : CompactionCacheKeyFactory.ExtractAgentPromptHashFromSnapshot(snapshot, expert.Agent.AgentId)
            ?? CompactionCacheKeyFactory.ComputeAgentPromptHash(expert.Agent.SystemPrompt);
        var store = new ConversationStore(_dataDirectory);
        var source = await store.ReadCompactionSourceAsync(sessionId, ct).ConfigureAwait(false);
        var cache = await store.ReadCompactCacheAsync(sessionId, ct).ConfigureAwait(false);
        var resolution = SummaryProjectionResolver.Resolve(source, cache, promptHash, selection?.ToolCatalogVersion);
        SessionContextStatusDto? operation = null;
        operationId ??= _operations.Values.FirstOrDefault(entry => entry.SessionId == sessionId
            && entry.Task.IsValueCreated && !entry.Task.Value.IsCompleted)?.OperationId;
        if (operationId is null)
        {
            var operationDirectory = Path.GetDirectoryName(OperationPath(sessionId, "latest"))!;
            if (Directory.Exists(operationDirectory))
            {
                var latest = Directory.EnumerateFiles(operationDirectory, "*.json")
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (latest is not null)
                {
                    await using var stream = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var saved = await JsonSerializer.DeserializeAsync(stream, RuntimeJsonContext.Default.SessionContextStatusDto, ct)
                        .ConfigureAwait(false);
                    operationId = saved?.OperationId;
                }
            }
        }
        if (operationId is not null)
        {
            operation = await ReadOperationAsync(sessionId, operationId, ct).ConfigureAwait(false);
            if (operation is not null && (!_operations.TryGetValue(OperationPath(sessionId, operationId), out var pending)
                || !pending.Task.IsValueCreated || pending.Task.Value.IsCompleted))
            {
                operation = await RecoverOperationAsync(operation, ct).ConfigureAwait(false);
            }
        }
        int? tokens = null;
        int? window = null;
        var estimateSource = "unknown";
        if (selection is not null)
        {
            var modelId = expert?.Agent.ModelId ?? selection.DefaultSelection.ModelId;
            var providerId = expert?.Agent.ProviderId ?? selection.DefaultSelection.ProviderId;
            try
            {
                var provider = _providerResolver(selection with { DefaultSelection = new DefaultSelection(providerId, modelId) });
                window = provider.Models.FirstOrDefault(model => model.Id == modelId || model.ProviderModelId == modelId)?.ContextWindow;
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
            {
                // A restarted Runtime may not have received host model registrations yet.
            }
            var estimate = ProviderTokenEstimator.Estimate(new RuntimeProviderRequest("context-status", "session-compactor",
                providerId, modelId, resolution.Messages));
            tokens = estimate.InputTokens;
            estimateSource = "runtime.utf8-bytes/4";
        }
        // Persisted selections intentionally omit full prompts, skills and memory (D-66).
        // A history-only estimate must never masquerade as a complete request estimate.
        var isStarting = operationId is not null && _operations.TryGetValue(OperationPath(sessionId, operationId), out var active)
            && active.Task.IsValueCreated && !active.Task.Value.IsCompleted;
        return new SessionContextStatusDto(sessionId, operationId, operation?.OperationState ?? (isStarting ? "Running" : operationId is null ? "Idle" : "NotFound"),
            resolution.Cache?.ProjectionRevision, resolution.Cache?.CoveredThroughSequence,
            tokens, null, window, estimateSource, true,
            operation?.ReasonCode ?? resolution.CacheMissReason ?? "full_context_material_unavailable",
            DateTimeOffset.UtcNow, operation?.Result);
    }

    private async Task<SessionContextStatusDto> RecoverOperationAsync(SessionContextStatusDto state, CancellationToken ct)
    {
        if (state.OperationState != "Running")
        {
            return state;
        }
        var store = new ConversationStore(_dataDirectory);
        var cache = await store.ReadCompactCacheAsync(state.SessionId, ct).ConfigureAwait(false);
        var source = await store.ReadCompactionSourceAsync(state.SessionId, ct).ConfigureAwait(false);
        var resolution = SummaryProjectionResolver.Resolve(source, cache);
        var matchingNonSummary = cache is { Strategy: not "summary" }
            && cache.Schema == ConversationCompactCacheV1.CurrentSchema
            && cache.SourceHistorySha256 == source.HistorySha256;
        if ((resolution.IsApplied || matchingNonSummary) && cache!.OperationId == state.OperationId)
        {
            var result = ToCachedResult(cache, "recovered") with
            {
                Outcome = "Applied", OperationId = state.OperationId,
                ProjectionRevision = cache.ProjectionRevision, CoveredThroughSequence = cache.CoveredThroughSequence
            };
            state = state with { OperationState = "Succeeded", Result = ToDto(result), ReasonCode = null };
        }
        else
        {
            state = state with { OperationState = "Interrupted", ReasonCode = "runtime_restarted_before_compaction_completed" };
        }
        state = state with { UpdatedAt = DateTimeOffset.UtcNow };
        await WriteOperationAsync(state, ct).ConfigureAwait(false);
        return state;
    }
}
