using System.Text.Json;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Modes.Expert;

/// <summary>Resolves a validated summary and every canonical message after its coverage boundary.</summary>
public static class SummaryProjectionResolver
{
    public static Resolution Resolve(
        ConversationCompactionSource source,
        ConversationCompactCacheV1? cache,
        string? agentPromptHash = null,
        string? toolCatalogVersion = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var reason = CompactionCacheKeyFactory.ValidateSummaryPrefix(
            cache, source, agentPromptHash, toolCatalogVersion);
        if (reason is not null)
        {
            return new Resolution(null, source.Records.Select(ToMessage).ToArray(), reason);
        }

        try
        {
            var summary = cache!.ProjectionMessages.Select(message => new RuntimeProviderMessage(
                message.Role,
                message.Content.Deserialize(RuntimeJsonContext.Default.ContentBlockArray)
                    ?? throw new JsonException("Summary content is missing."))).ToArray();
            if (summary.Length != 1 || !StrictConversationSummarizer.IsValidatedSummary(summary[0]))
            {
                return new Resolution(null, source.Records.Select(ToMessage).ToArray(), "summary-invalid");
            }
            return new Resolution(cache,
                [.. summary, .. source.Records.Where(record => record.Sequence > cache.CoveredThroughSequence).Select(ToMessage)],
                null);
        }
        catch (JsonException)
        {
            return new Resolution(null, source.Records.Select(ToMessage).ToArray(), "summary-invalid");
        }
    }

    /// <summary>Expands the raw tail outward so tool calls and their results cannot be split.</summary>
    public static int GetCompressiblePrefixCount(ConversationCompactionSource source, int rawTailSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(rawTailSize);
        var tailCount = rawTailSize == 0 ? 0
            : ContextProjectionBuilder.BuildTailWindow(source.Records, rawTailSize).Length;
        return Math.Max(0, source.Records.Count - tailCount);
    }

    public static RuntimeProviderMessage[] CreateSummaryInput(
        ConversationCompactionSource source, Resolution resolution, int prefixCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(prefixCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(prefixCount, source.Records.Count);
        var covered = resolution.Cache?.CoveredThroughSequence ?? 0;
        if (prefixCount == 0 || source.Records[prefixCount - 1].Sequence < covered)
        {
            return source.Records.Take(prefixCount).Select(ToMessage).ToArray();
        }
        return [.. resolution.IsApplied ? resolution.Messages.Take(1) : [],
            .. source.Records.Take(prefixCount).Where(record => record.Sequence > covered).Select(ToMessage)];
    }

    public static RuntimeProviderMessage ToMessage(ConversationRecordV1 record) => new(record.Role,
        record.Content.Deserialize(RuntimeJsonContext.Default.ContentBlockArray)
            ?? throw new InvalidDataException("A conversation record has no content."));

    public sealed record Resolution(ConversationCompactCacheV1? Cache,
        RuntimeProviderMessage[] Messages, string? CacheMissReason)
    {
        public bool IsApplied => Cache is not null;
    }
}
