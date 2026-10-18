using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Persistence.Files;

/// <summary>
/// 统一的压缩缓存键生成与匹配工厂，确保手动压缩与 Expert 投影使用相同的规则。
/// </summary>
public static class CompactionCacheKeyFactory
{
    /// <summary>Checks summary semantics and covered bytes, independently of future model selection.</summary>
    public static string? ValidateSummaryPrefix(
        ConversationCompactCacheV1? cache,
        ConversationCompactionSource source,
        string? agentPromptHash = null,
        string? toolCatalogVersion = null)
    {
        if (cache is null)
        {
            return "cache-not-found";
        }
        if (cache.Schema != ConversationCompactCacheV1.CurrentSchema)
        {
            return "cache-schema-unsupported";
        }
        if (cache.Strategy != "summary" || !cache.SummaryValidated || cache.CoveredThroughSequence <= 0
            || string.IsNullOrWhiteSpace(cache.ProjectionRevision))
        {
            return "summary-not-validated";
        }
        if (cache.RuntimeMode != "Expert"
            || (agentPromptHash is not null && cache.AgentPromptHash != agentPromptHash)
            || (toolCatalogVersion is not null && cache.ToolCatalogVersion != toolCatalogVersion))
        {
            return "summary-semantics-changed";
        }
        if (source.PrefixHistoryHashes is null
            || !source.PrefixHistoryHashes.TryGetValue(cache.CoveredThroughSequence, out var prefixHash)
            || !string.Equals(prefixHash, cache.PrefixHistorySha256, StringComparison.OrdinalIgnoreCase))
        {
            return "summary-prefix-changed";
        }
        return null;
    }

    /// <summary>
    /// 检查缓存是否匹配指定的输入参数。
    /// 此方法是缓存命中判定的唯一入口，涵盖所有影响压缩结果的因素。
    /// </summary>
    public static bool IsMatchingCache(
        ConversationCompactCacheV1? cache,
        string sourceHistorySha256,
        string strategy,
        string providerId,
        string modelId,
        int? keepLastTokens,
        string? contextPolicyHash = null,
        string? agentPromptHash = null,
        string? toolCatalogVersion = null,
        string? summaryModelVersion = null)
    {
        if (cache is null)
        {
            return false;
        }

        // 基础匹配：Schema、历史哈希、策略、Provider、Model
        if (!string.Equals(cache.Schema, ConversationCompactCacheV1.CurrentSchema, StringComparison.Ordinal)
            || !string.Equals(cache.SourceHistorySha256, sourceHistorySha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(cache.Strategy, strategy, StringComparison.Ordinal)
            || !string.Equals(cache.ProviderId, providerId, StringComparison.Ordinal)
            || !string.Equals(cache.ModelId, modelId, StringComparison.Ordinal)
            || cache.KeepLastTokens != keepLastTokens)
        {
            return false;
        }

        // Expert 模式专属：检查 ContextPolicy、AgentPrompt、ToolCatalog
        // 若调用方提供了这些参数，则必须精确匹配；否则视为非 Expert 压缩，不检查这些字段
        if (contextPolicyHash is not null
            && !string.Equals(cache.ContextPolicyHash, contextPolicyHash, StringComparison.Ordinal))
        {
            return false;
        }

        if (agentPromptHash is not null
            && !string.Equals(cache.AgentPromptHash, agentPromptHash, StringComparison.Ordinal))
        {
            return false;
        }

        if (toolCatalogVersion is not null
            && !string.Equals(cache.ToolCatalogVersion, toolCatalogVersion, StringComparison.Ordinal))
        {
            return false;
        }

        if (summaryModelVersion is not null
            && !string.Equals(cache.SummaryModelVersion, summaryModelVersion, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 为 Expert 模式生成 ContextPolicy 规范化哈希。
    /// </summary>
    public static string ComputeContextPolicyHash(ExpertContextPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // 规范化策略名称：Summary/SummaryCompressed 都映射为 "summary"
        // 这样可以避免因大小写或策略别名导致的缓存 miss
        var normalizedStrategy = NormalizeStrategyName(policy.Strategy);

        // 使用规范化的 DTO 确保哈希稳定性
        // 注意：SummaryProviderId/SummaryModelId 已作为单独的缓存键字段，不包含在哈希中
        var normalized = new ContextPolicyHashDto
        {
            Strategy = normalizedStrategy,
            MaxTokens = policy.MaxTokens,
            TailMessageCount = policy.TailMessageCount,
            PreserveToolResults = policy.PreserveToolResults,
            PreserveReasoning = policy.PreserveReasoning,
            SummaryRequired = policy.SummaryRequired
        };

        var json = JsonSerializer.Serialize(normalized, RuntimeJsonContext.Default.ContextPolicyHashDto);
        return ComputeHash(json);
    }

    /// <summary>
    /// 规范化策略名称为缓存键标准格式。
    /// </summary>
    private static string NormalizeStrategyName(string strategy)
    {
        if (string.Equals(strategy, "Summary", StringComparison.OrdinalIgnoreCase)
            || string.Equals(strategy, "SummaryCompressed", StringComparison.OrdinalIgnoreCase))
        {
            return "summary";
        }

        if (string.Equals(strategy, "TailWindow", StringComparison.OrdinalIgnoreCase))
        {
            return "tail-window";
        }

        if (string.Equals(strategy, "SlidingWindow", StringComparison.OrdinalIgnoreCase))
        {
            return "sliding-window";
        }

        // 未知策略直接转小写
        return strategy.ToLowerInvariant();
    }

    /// <summary>
    /// 从 Agent SystemPrompt 计算 Prompt 哈希。
    /// </summary>
    public static string ComputeAgentPromptHash(string systemPrompt)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        return ComputeHash(systemPrompt);
    }

    /// <summary>
    /// 从 Session 快照中提取 Expert Agent 的真实 Prompt 哈希。
    /// 若快照中无对应 Agent，返回 null。
    /// </summary>
    public static string? ExtractAgentPromptHashFromSnapshot(
        PersistedSessionSnapshot snapshot,
        string agentId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var agentSnapshot = snapshot.AgentSnapshots
            .FirstOrDefault(agent => string.Equals(agent.AgentId, agentId, StringComparison.Ordinal));

        return agentSnapshot?.PromptHash;
    }

    /// <summary>
    /// 为缓存 miss 诊断生成详细的不匹配原因。
    /// </summary>
    public static string? AnalyzeCacheMismatch(
        ConversationCompactCacheV1? cache,
        string sourceHistorySha256,
        string strategy,
        string providerId,
        string modelId,
        int? keepLastTokens,
        string? contextPolicyHash = null,
        string? agentPromptHash = null,
        string? toolCatalogVersion = null,
        string? summaryModelVersion = null)
    {
        if (cache is null)
        {
            return "cache-not-found";
        }

        var mismatches = new List<string>(8);

        if (!string.Equals(cache.Schema, ConversationCompactCacheV1.CurrentSchema, StringComparison.Ordinal))
        {
            mismatches.Add("schema");
        }

        if (!string.Equals(cache.SourceHistorySha256, sourceHistorySha256, StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add("history");
        }

        if (!string.Equals(cache.Strategy, strategy, StringComparison.Ordinal))
        {
            mismatches.Add("strategy");
        }

        if (!string.Equals(cache.ProviderId, providerId, StringComparison.Ordinal))
        {
            mismatches.Add("provider");
        }

        if (!string.Equals(cache.ModelId, modelId, StringComparison.Ordinal))
        {
            mismatches.Add("model");
        }

        if (cache.KeepLastTokens != keepLastTokens)
        {
            mismatches.Add("window");
        }

        if (contextPolicyHash is not null
            && !string.Equals(cache.ContextPolicyHash, contextPolicyHash, StringComparison.Ordinal))
        {
            mismatches.Add("policy");
        }

        if (agentPromptHash is not null
            && !string.Equals(cache.AgentPromptHash, agentPromptHash, StringComparison.Ordinal))
        {
            mismatches.Add("prompt");
        }

        if (toolCatalogVersion is not null
            && !string.Equals(cache.ToolCatalogVersion, toolCatalogVersion, StringComparison.Ordinal))
        {
            mismatches.Add("tools");
        }

        if (summaryModelVersion is not null
            && !string.Equals(cache.SummaryModelVersion, summaryModelVersion, StringComparison.Ordinal))
        {
            mismatches.Add("summary-model");
        }

        return mismatches.Count > 0
            ? $"cache-identity-mismatch({string.Join(",", mismatches)})"
            : null;
    }

    private static string ComputeHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
