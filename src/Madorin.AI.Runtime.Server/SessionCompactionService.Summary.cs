using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Modes.Expert;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Server;

internal sealed partial class SessionCompactionService
{
    private static async Task<SessionCompactionResult> CompactSummaryAsync(
        string sessionId, SessionCompactionOptions options, ConversationCompactionSource source,
        ConversationStore store, IRuntimeProviderAdapter chatProvider, string chatProviderId, string chatModelId,
        IRuntimeProviderAdapter summaryProvider, string summaryProviderId, string summaryModelId,
        ExpertContextPolicy policy, string agentPromptHash, string toolCatalogVersion,
        string selectionSource, int? requestedMaxOutputTokens, CancellationToken ct)
    {
        var cache = await store.ReadCompactCacheAsync(sessionId, ct).ConfigureAwait(false);
        var resolution = SummaryProjectionResolver.Resolve(source, cache, agentPromptHash, toolCatalogVersion);
        var prefixCount = SummaryProjectionResolver.GetCompressiblePrefixCount(source, options.RawTailSize ?? policy.TailMessageCount ?? 8);
        var before = await EstimateHistoryAsync(chatProvider, chatProviderId, chatModelId, resolution.Messages, ct).ConfigureAwait(false);
        var boundary = prefixCount == 0 ? 0 : source.Records[prefixCount - 1].Sequence;

        SessionCompactionResult NotNeeded(string reason, string outcome = "NotNeeded") => new(
            sessionId, options.Strategy, summaryProviderId, summaryModelId, null, source.Records.Count,
            resolution.Messages.Length, resolution.Cache?.DroppedMessageCount ?? 0,
            before.InputTokens ?? 0, before.InputTokens, before.Source, options.DryRun,
            resolution.IsApplied, false, reason, selectionSource, false, outcome,
            options.OperationId, resolution.Cache?.ProjectionRevision, resolution.Cache?.CoveredThroughSequence);

        if (prefixCount == 0)
        {
            return NotNeeded("no_compressible_prefix");
        }
        if (resolution.IsApplied && boundary == resolution.Cache!.CoveredThroughSequence && !options.Force)
        {
            return NotNeeded("prefix_already_applied", "AlreadyApplied");
        }
        if (options.DryRun)
        {
            return NotNeeded("dry_run");
        }

        var chatModel = chatProvider.Models.FirstOrDefault(model => model.Id == chatModelId || model.ProviderModelId == chatModelId);
        if (chatModel?.ContextWindow is not > 0 && chatProvider is IRuntimeProviderModelCatalogSource catalog)
        {
            chatModel = (await catalog.FetchModelsAsync(ct).ConfigureAwait(false))
                .FirstOrDefault(model => model.Id == chatModelId || model.ProviderModelId == chatModelId) ?? chatModel;
        }
        var chatBudget = ContextTokenBudget.Resolve(chatModel, policy.MaxTokens, requestedMaxOutputTokens);

        var input = options.Force || boundary < resolution.Cache?.CoveredThroughSequence
            ? source.Records.Take(prefixCount).Select(SummaryProjectionResolver.ToMessage).ToArray()
            : SummaryProjectionResolver.CreateSummaryInput(source, resolution, prefixCount);
        var summary = await StrictConversationSummarizer.GenerateAsync(
            summaryProvider, summaryProviderId, summaryModelId, input, ct: ct).ConfigureAwait(false);
        RuntimeProviderMessage[] projected = [summary, .. source.Records.Skip(prefixCount).Select(SummaryProjectionResolver.ToMessage)];
        var after = await EstimateHistoryAsync(chatProvider, chatProviderId, chatModelId, projected, ct).ConfigureAwait(false);
        if (after.InputTokens >= before.InputTokens)
        {
            return NotNeeded("summary_did_not_reduce_tokens");
        }
        if (after.InputTokens is null || after.InputTokens > chatBudget.InputLimit)
        {
            throw new InvalidDataException("context_budget_exhausted: Summary and retained history exceed the chat model input budget.");
        }

        // The canonical file can be appended by maintenance code outside this operation's session lease.
        var latest = await store.ReadCompactionSourceAsync(sessionId, ct).ConfigureAwait(false);
        if (!string.Equals(latest.HistorySha256, source.HistorySha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("history_changed_during_compaction");
        }
        var revision = Guid.NewGuid().ToString("N");
        var newCache = new ConversationCompactCacheV1(
            ConversationCompactCacheV1.CurrentSchema, sessionId, source.HistorySha256, "summary",
            summaryProviderId, summaryModelId, null, source.Records.Count, prefixCount,
            before.InputTokens ?? 0, after.InputTokens ?? 0, after.Source, [ToCompactMessage(summary)],
            DateTimeOffset.UtcNow, AgentPromptHash: agentPromptHash, ToolCatalogVersion: toolCatalogVersion,
            SummaryModelVersion: ResolveModelVersion(summaryProvider, summaryModelId),
            CacheMissReason: resolution.CacheMissReason, CoveredThroughSequence: boundary,
            PrefixHistorySha256: source.PrefixHistoryHashes![boundary], ProjectionRevision: revision,
            OperationId: options.OperationId, SummaryValidated: true);
        ct.ThrowIfCancellationRequested();
        await store.WriteCompactCacheAsync(sessionId, newCache, ct).ConfigureAwait(false);
        return new SessionCompactionResult(sessionId, options.Strategy, summaryProviderId, summaryModelId, null,
            source.Records.Count, projected.Length, prefixCount, before.InputTokens ?? 0, after.InputTokens,
            after.Source, false, false, true, resolution.CacheMissReason, selectionSource, true,
            "Applied", options.OperationId, revision, boundary);
    }

    private static async Task<HistoryEstimate> EstimateHistoryAsync(IRuntimeProviderAdapter provider,
        string providerId, string modelId, RuntimeProviderMessage[] messages, CancellationToken ct)
    {
        var request = new RuntimeProviderRequest("context-estimate-" + Guid.NewGuid().ToString("N"),
            "session-compactor", providerId, modelId, messages, CancellationToken: ct);
        var estimate = await provider.EstimateTokensAsync(request, ct).ConfigureAwait(false);
        return estimate.InputTokens is null
            ? new HistoryEstimate(ProviderTokenEstimator.Estimate(request).InputTokens, "runtime.utf8-bytes/4")
            : new HistoryEstimate(estimate.InputTokens, $"provider.{estimate.Accuracy.ToString().ToLowerInvariant()}");
    }

    private sealed record HistoryEstimate(int? InputTokens, string Source);
}
