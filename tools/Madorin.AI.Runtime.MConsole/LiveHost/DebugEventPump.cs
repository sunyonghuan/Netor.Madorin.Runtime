using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.MConsole.LiveHost;

/// <summary>在后台读事件流，确认水位，并通知界面刷新。</summary>
public sealed class DebugEventPump
{
    private readonly IRuntimeClient _client;
    private readonly DebugTranscript _transcript;
    private readonly Action _changed;
    private string? _activeRunId;
    private TaskCompletionSource? _done;
    private long _cursor;

    public DebugEventPump(IRuntimeClient client, DebugTranscript transcript, Action changed)
    {
        _client = client;
        _transcript = transcript;
        _changed = changed;
    }

    public string? SessionId { get; private set; }

    public void Expect(string runId)
    {
        _activeRunId = runId;
        _done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Task WaitAsync(CancellationToken cancellationToken)
    {
        var done = _done;
        return done is null
            ? Task.CompletedTask
            : done.Task.WaitAsync(cancellationToken);
    }

    public void Abandon() => _done?.TrySetResult();

    public async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var envelope in _client.ReadEventsAsync(_cursor, cancellationToken))
            {
                Accept(envelope);
                await _client.AcknowledgeEventsAsync(envelope.Gsn, cancellationToken);
                _cursor = envelope.Gsn;
            }

            _transcript.Append(DebugOutputChannel.Notice, "事件流已结束。");
            _changed();
            Abandon();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Abandon();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or RuntimeClientException)
        {
            _transcript.Append(DebugOutputChannel.Notice, "事件流中断: " + ex.Message);
            _changed();
            _done?.TrySetException(ex);
        }
    }

    private void Accept(RuntimeEventEnvelope envelope)
    {
        var view = DebugEventFormatter.Format(envelope);
        var aggregate = envelope.MessageType is MessageTypes.TextDelta or MessageTypes.ReasoningDelta;
        _transcript.AddEvent(
            envelope.MessageType,
            envelope.Gsn,
            envelope.Timestamp,
            view.EventLine,
            view.Text?.Length ?? 0,
            aggregate);
        _transcript.Append(view.Channel, view.Text);
        if (!string.IsNullOrWhiteSpace(view.SessionId))
        {
            SessionId = view.SessionId;
        }

        if (view.Terminal)
        {
            _transcript.EndTurn();
            if (string.Equals(envelope.RunId, _activeRunId, StringComparison.Ordinal))
            {
                _done?.TrySetResult();
            }
        }

        _changed();
    }
}
