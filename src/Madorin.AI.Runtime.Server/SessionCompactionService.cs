using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Modes.Expert;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Server;

/// <summary>Shared compaction implementation used by the connected Runtime protocol.</summary>
internal sealed partial class SessionCompactionService : IDisposable, IAsyncDisposable
{
    private readonly string _dataDirectory;
    private readonly string _databaseConnectionString;
    private readonly Func<NextTurnSelection, IRuntimeProviderAdapter> _providerResolver;
    /// <summary>运行时诊断日志；为空时不写压缩事件（例如单元测试直接构造服务）。</summary>
    private readonly RuntimeDiagnosticLog? _diagnosticLog;

    /// <summary>创建压缩服务。</summary>
    /// <param name="dataDirectory">会话数据目录。</param>
    /// <param name="databaseConnectionString">SQLite 连接串。</param>
    /// <param name="providerResolver">按 Selection 解析 Provider。</param>
    /// <param name="diagnosticLog">可选的运行时诊断日志，用于记录压缩成功与失败事件。</param>
    public SessionCompactionService(
        string dataDirectory,
        string databaseConnectionString,
        Func<NextTurnSelection, IRuntimeProviderAdapter> providerResolver,
        RuntimeDiagnosticLog? diagnosticLog = null)
    {
        _dataDirectory = dataDirectory;
        _databaseConnectionString = databaseConnectionString;
        _providerResolver = providerResolver;
        _diagnosticLog = diagnosticLog;
    }

    private async Task<SessionCompactionResult?> CompactCoreAsync(
        string sessionId,
        SessionCompactionOptions options,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        await using var connection = new SqliteConnection(_databaseConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        var repository = new SqliteSessionRepository(connection);
        var snapshot = await repository.GetSessionSnapshotAsync(sessionId, ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(snapshot.SelectionJson))
        {
            throw new InvalidDataException($"Session '{sessionId}' has no persisted Selection.");
        }

        var selection = JsonSerializer.Deserialize(
                snapshot.SelectionJson,
                RuntimeJsonContext.Default.NextTurnSelection)
            ?? throw new InvalidDataException($"Session '{sessionId}' has an invalid Selection snapshot.");
        var runtimeMode = snapshot.Mode.ToString();
        var isExpertMode = selection.ModeOptions is ExpertModeOptions;
        var expertOptions = isExpertMode ? (ExpertModeOptions)selection.ModeOptions : null;
        var contextPolicy = expertOptions?.ContextPolicy ?? new ExpertContextPolicy();
        var contextPolicyHash = isExpertMode
            ? CompactionCacheKeyFactory.ComputeContextPolicyHash(contextPolicy)
            : string.Empty;
        var agentPromptHash = isExpertMode
            ? CompactionCacheKeyFactory.ExtractAgentPromptHashFromSnapshot(
                snapshot,
                expertOptions!.Agent.AgentId)
                ?? CompactionCacheKeyFactory.ComputeAgentPromptHash(
                    expertOptions.Agent.SystemPrompt)
            : string.Empty;
        var toolCatalogVersion = selection.ToolCatalogVersion;
        var providerId = expertOptions?.Agent.ProviderId ?? selection.DefaultSelection.ProviderId;
        var modelId = expertOptions?.Agent.ModelId ?? selection.DefaultSelection.ModelId;
        if (options.Strategy is not SessionCompactionStrategy.Summary)
        {
            providerId = options.ProviderId ?? providerId;
            modelId = options.ModelId ?? modelId;
        }
        var effectiveSelection = selection with
        {
            DefaultSelection = new DefaultSelection(providerId, modelId)
        };
        var provider = _providerResolver(effectiveSelection)
            ?? throw new InvalidOperationException("The Provider resolver returned null.");
        ValidateModel(provider, modelId);

        // 解析压缩模型（与 ExpertModeOrchestrator 保持一致）
        var compactionProviderId = options.ProviderId ?? contextPolicy.SummaryProviderId ?? providerId;
        var compactionModelId = options.ModelId ?? contextPolicy.SummaryModelId ?? modelId;
        var compactionProvider = string.Equals(compactionProviderId, providerId, StringComparison.Ordinal)
            ? provider
            : _providerResolver(effectiveSelection with
            {
                DefaultSelection = new DefaultSelection(compactionProviderId, compactionModelId)
            });

        int? keepLastTokens = options.Strategy is SessionCompactionStrategy.SlidingWindow
            ? options.KeepLastTokens ?? ResolveDefaultWindow(provider, modelId)
            : null;
        var strategyName = ToWireValue(options.Strategy);
        var summaryModelVersion = ResolveModelVersion(compactionProvider, compactionModelId);
        var selectionSource = options.SelectionSource
            ?? (options.ProviderId is not null || options.ModelId is not null
                ? "cli-override"
                : "session-default");
        var store = new ConversationStore(_dataDirectory);
        var source = await store.ReadCompactionSourceAsync(sessionId, ct).ConfigureAwait(false);
        if (options.Strategy is SessionCompactionStrategy.Summary)
        {
            return await CompactSummaryAsync(sessionId, options, source, store, provider, providerId, modelId,
                compactionProvider, compactionProviderId, compactionModelId, contextPolicy,
                agentPromptHash, toolCatalogVersion, selectionSource, expertOptions?.RequestControl?.MaxTokens, ct).ConfigureAwait(false);
        }
        string? cacheMissReason = null;

        if (!options.DryRun && !options.Force)
        {
            var cached = await store.ReadCompactCacheAsync(sessionId, ct).ConfigureAwait(false);
            if (CompactionCacheKeyFactory.IsMatchingCache(
                cached,
                source.HistorySha256,
                strategyName,
                providerId,
                modelId,
                keepLastTokens,
                isExpertMode ? contextPolicyHash : null,
                isExpertMode ? agentPromptHash : null,
                isExpertMode ? toolCatalogVersion : null,
                summaryModelVersion))
            {
                return ToCachedResult(cached!, selectionSource) with { OperationId = options.OperationId, Outcome = "AlreadyApplied" };
            }

            cacheMissReason = CompactionCacheKeyFactory.AnalyzeCacheMismatch(
                cached,
                source.HistorySha256,
                strategyName,
                providerId,
                modelId,
                keepLastTokens,
                isExpertMode ? contextPolicyHash : null,
                isExpertMode ? agentPromptHash : null,
                isExpertMode ? toolCatalogVersion : null,
                summaryModelVersion);
        }
        else if (options.Force)
        {
            cacheMissReason = "forced-regeneration";
        }

        var estimateInvocationId = "compact-estimate-" + Guid.NewGuid().ToString("N");
        ValueTask<ProviderTokenEstimate> EstimateAsync(
            RuntimeProviderMessage[] messages,
            CancellationToken token) => provider.EstimateTokensAsync(
                new RuntimeProviderRequest(
                    estimateInvocationId,
                    "session-compactor",
                    providerId,
                    modelId,
                    messages,
                    Tools: null,
                    CancellationToken: token),
                token);
        var fullProjection = await ContextProjectionBuilder.BuildAsync(
                source.Records,
                int.MaxValue,
                EstimateAsync,
                ct)
            .ConfigureAwait(false);

        if (options.DryRun)
        {
            var planned = options.Strategy switch
            {
                SessionCompactionStrategy.Summary => 1,
                SessionCompactionStrategy.Full => fullProjection.Messages.Length,
                _ => (await ContextProjectionBuilder.BuildAsync(
                    source.Records,
                    keepLastTokens!.Value,
                    EstimateAsync,
                    ct).ConfigureAwait(false)).Messages.Length
            };
            return new SessionCompactionResult(
                sessionId,
                options.Strategy,
                providerId,
                modelId,
                keepLastTokens,
                source.Records.Count,
                planned,
                Math.Max(0, source.Records.Count - planned),
                fullProjection.EstimatedTokens,
                null,
                fullProjection.EstimateSource,
                true,
                false,
                false,
                cacheMissReason,
                selectionSource,
                false, Outcome: "NotNeeded", OperationId: options.OperationId);
        }

        var projection = options.Strategy is SessionCompactionStrategy.Full
            ? fullProjection
            : await ContextProjectionBuilder.BuildAsync(source.Records, keepLastTokens!.Value, EstimateAsync, ct)
                .ConfigureAwait(false);
        var projectionMessages = projection.Messages;
        var droppedMessageCount = projection.DroppedMessageCount;
        var afterEstimatedTokens = projection.EstimatedTokens;
        var estimateSource = projection.EstimateSource;
        const bool summaryGenerated = false;
        var cache = new ConversationCompactCacheV1(
            ConversationCompactCacheV1.CurrentSchema,
            sessionId,
            source.HistorySha256,
            strategyName,
            providerId,
            modelId,
            keepLastTokens,
            source.Records.Count,
            droppedMessageCount,
            fullProjection.EstimatedTokens,
            afterEstimatedTokens,
            estimateSource,
            projectionMessages.Select(ToCompactMessage).ToArray(),
            DateTimeOffset.UtcNow,
            RuntimeMode: runtimeMode,
            ContextPolicyHash: contextPolicyHash,
            AgentPromptHash: agentPromptHash,
            ToolCatalogVersion: toolCatalogVersion,
            SummaryModelVersion: summaryModelVersion,
            CacheMissReason: cacheMissReason,
            ProjectionRevision: Guid.NewGuid().ToString("N"), OperationId: options.OperationId);
        await store.WriteCompactCacheAsync(sessionId, cache, ct).ConfigureAwait(false);
        return new SessionCompactionResult(
            sessionId,
            options.Strategy,
            providerId,
            modelId,
            keepLastTokens,
            source.Records.Count,
            projectionMessages.Length,
            droppedMessageCount,
            fullProjection.EstimatedTokens,
            afterEstimatedTokens,
            estimateSource,
            false,
            false,
            true,
            cacheMissReason,
            selectionSource,
            summaryGenerated, OperationId: options.OperationId, ProjectionRevision: cache.ProjectionRevision);
    }

    private static void ValidateOptions(SessionCompactionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.RawTailSize is < 0 or > 10000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RawTailSize must be between 0 and 10000.");
        }
        if (!Enum.IsDefined(options.Strategy))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        if (options.ProviderId is not null && string.IsNullOrWhiteSpace(options.ProviderId))
        {
            throw new ArgumentException("The compaction Provider must not be empty.", nameof(options));
        }
        if (options.ModelId is not null && string.IsNullOrWhiteSpace(options.ModelId))
        {
            throw new ArgumentException("The compaction model must not be empty.", nameof(options));
        }
        if (options.KeepLastTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        if (options.KeepLastTokens is not null && options.Strategy is not SessionCompactionStrategy.SlidingWindow)
        {
            throw new ArgumentException(
                "KeepLastTokens can only be used with the sliding-window strategy.",
                nameof(options));
        }
    }

    private static void ValidateModel(IRuntimeProviderAdapter provider, string modelId)
    {
        if (provider.Models.Count == 0 || provider.Models.Any(model =>
                string.Equals(model.Id, modelId, StringComparison.Ordinal)
                || string.Equals(model.ProviderModelId, modelId, StringComparison.Ordinal)))
        {
            return;
        }
        throw new KeyNotFoundException($"Model '{modelId}' is not available from Provider '{provider.ProviderId}'.");
    }

    private static int ResolveDefaultWindow(IRuntimeProviderAdapter provider, string modelId)
    {
        var model = provider.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, modelId, StringComparison.Ordinal)
            || string.Equals(candidate.ProviderModelId, modelId, StringComparison.Ordinal));
        return model?.ContextWindow is > 0
            ? Math.Max(1, (int)(model.ContextWindow.Value * 0.9))
            : SessionCompactionOptions.DefaultKeepLastTokens;
    }

    private static string ResolveModelVersion(
        IRuntimeProviderAdapter provider,
        string modelId)
    {
        var found = provider.Models.FirstOrDefault(model =>
            string.Equals(model.Id, modelId, StringComparison.Ordinal)
            || string.Equals(model.ProviderModelId, modelId, StringComparison.Ordinal));
        return found?.ProviderModelId ?? found?.Id ?? modelId;
    }

    private static string ComputeHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static SessionCompactionResult ToCachedResult(
        ConversationCompactCacheV1 cache,
        string selectionSource) => new(
            cache.SessionId,
            ParseStrategy(cache.Strategy),
            cache.ProviderId,
            cache.ModelId,
            cache.KeepLastTokens,
            cache.SourceMessageCount,
            cache.SummaryValidated ? 1 + cache.SourceMessageCount - cache.DroppedMessageCount : cache.ProjectionMessages.Length,
            cache.DroppedMessageCount,
            cache.BeforeEstimatedTokens,
            cache.AfterEstimatedTokens,
            cache.EstimateSource,
            false,
            true,
            false,
            null,
            selectionSource,
            string.Equals(cache.Strategy, "summary", StringComparison.Ordinal));

    private static ConversationCompactMessageV1 ToCompactMessage(RuntimeProviderMessage message) => new(
        message.Role,
        JsonSerializer.SerializeToElement(
            message.Content,
            RuntimeJsonContext.Default.ContentBlockArray));

    private static SessionCompactionStrategy ParseStrategy(string value) => value switch
    {
        "full" => SessionCompactionStrategy.Full,
        "sliding-window" => SessionCompactionStrategy.SlidingWindow,
        "summary" => SessionCompactionStrategy.Summary,
        _ => throw new InvalidDataException($"Unknown compaction strategy '{value}'.")
    };

    private static string ToWireValue(SessionCompactionStrategy value) => value switch
    {
        SessionCompactionStrategy.Full => "full",
        SessionCompactionStrategy.SlidingWindow => "sliding-window",
        SessionCompactionStrategy.Summary => "summary",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}
