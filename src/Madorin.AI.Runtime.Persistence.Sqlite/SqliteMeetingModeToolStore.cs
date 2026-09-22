using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Persistence.Sqlite;

/// <summary>Adapts <see cref="SqliteMeetingRepository"/> for Meeting builtin tools.</summary>
public sealed class SqliteMeetingModeToolStore(
    SqliteMeetingRepository meetingRepository,
    ConversationStore? conversationStore = null) : IMeetingModeToolStore
{
    private readonly SqliteMeetingRepository _meetingRepository = meetingRepository
        ?? throw new ArgumentNullException(nameof(meetingRepository));
    private readonly ConversationStore? _conversationStore = conversationStore;

    public Task MarkEndConfirmedAsync(
        string sessionId,
        string? finalSummaryMessageId,
        CancellationToken ct = default) =>
        _meetingRepository.MarkEndConfirmedAsync(sessionId, finalSummaryMessageId, ct);

    public Task SaveFinalSummaryMessageIdAsync(
        string sessionId,
        string summaryMessageId,
        CancellationToken ct = default) =>
        _meetingRepository.SaveFinalSummaryMessageIdAsync(sessionId, summaryMessageId, ct);

    public async Task<string> PersistFinalSummaryMessageAsync(
        string sessionId,
        string runId,
        string invocationId,
        string agentId,
        string summaryMarkdown,
        string messageId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(summaryMarkdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        if (_conversationStore is not null)
        {
            ContentBlock[] content = [new TextContentBlock(summaryMarkdown)];
            var contentElement = JsonSerializer.SerializeToElement(
                content,
                RuntimeJsonContext.Default.ContentBlockArray);
            var summarizesThroughSeq = await _conversationStore
                .GetLastSequenceAsync(sessionId, ct)
                .ConfigureAwait(false);
            var metadataBuffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(metadataBuffer))
            {
                writer.WriteStartObject();
                writer.WriteString("kind", "meeting-host-final-summary");
                writer.WriteNumber("summarizesThroughSeq", summarizesThroughSeq);
                writer.WriteString("runId", runId);
                writer.WriteString("source", "mcli_meeting_output_summary");
                writer.WriteEndObject();
            }

            using var metadataDocument = JsonDocument.Parse(metadataBuffer.WrittenMemory);
            var record = new ConversationRecordV1(
                messageId,
                summarizesThroughSeq + 1,
                invocationId,
                agentId,
                "assistant",
                contentElement,
                DateTimeOffset.UtcNow,
                UsageReference: null,
                SummaryMetadata: metadataDocument.RootElement.Clone());
            await _conversationStore
                .AppendMessageAsync(sessionId, "Meeting", record, ct)
                .ConfigureAwait(false);
        }

        await _meetingRepository
            .SaveFinalSummaryMessageIdAsync(sessionId, messageId, ct)
            .ConfigureAwait(false);
        return messageId;
    }

    public async Task<(bool EndConfirmed, string? FinalSummaryMessageId)> GetEndConfirmationStateAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        var snapshot = await _meetingRepository.GetMeetingSnapshotAsync(sessionId, ct)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Meeting session '{sessionId}' not found.");
        return (snapshot.Session.EndConfirmed, snapshot.Session.FinalSummaryMessageId);
    }

    public async Task<IReadOnlyList<MeetingPendingInputPeek>> PeekPendingInputsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        var rows = await _meetingRepository.ListPendingInputsAsync(sessionId, ct).ConfigureAwait(false);
        return rows
            .Select(row => new MeetingPendingInputPeek(row.RequestId, row.ContentJson, row.EnqueuedAt))
            .ToArray();
    }

    public Task EnqueuePendingInputAsync(
        string sessionId,
        string requestId,
        string contentJson,
        string? runId,
        CancellationToken ct = default) =>
        _meetingRepository.EnqueuePendingInputAsync(sessionId, requestId, contentJson, runId, ct);
}
