using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Protocol;

namespace Madorin.AI.Multimedia.Core.Execution;

// 单次调用独占取消、计时和追踪信息，避免多个技能调用共享静态状态。
public sealed class SkillInvocation : IAsyncDisposable
{
    private readonly TimeProvider _time;
    private readonly long _startedAt;
    private readonly Lock _state = new();
    private readonly CancellationTokenSource _operation = new();
    private readonly CancellationTokenSource _readerLifetime = new();
    private readonly ITimer _timer;
    private Stream? _controlInput;
    private Task? _controlReader;
    private string? _responseId;
    private TimeSpan _budget = TimeSpan.FromSeconds(300);
    private string? _terminationCode;
    private bool _committed;
    private int _hostCancelled;

    public SkillInvocation(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _startedAt = _time.GetTimestamp();
        _timer = _time.CreateTimer(_ => CheckDeadline(), null, _budget, Timeout.InfiniteTimeSpan);
    }

    public string? ResponseId
    {
        get => _responseId;
        set
        {
            _responseId = value;
            StartControlReader();
        }
    }
    public string? Model { get; set; }
    public string? ProviderRequestId { get; set; }
    public bool TryRun { get; set; }
    public bool CredentialPresent { get; set; }
    public JsonArray IgnoredParameters { get; } = [];
    public JsonArray ConvertedParameters { get; } = [];
    public bool HostCancelled => Volatile.Read(ref _hostCancelled) == 1;
    public CancellationToken CancellationToken => _operation.Token;
    public long ElapsedMilliseconds => (long)Elapsed.TotalMilliseconds;
    private TimeSpan Elapsed => _time.GetElapsedTime(_startedAt);

    public void SetTimeout(int seconds)
    {
        if (seconds < 1)
        {
            throw new SkillException("invalid_parameter", "timeout 必须是正整数。");
        }

        lock (_state)
        {
            ObserveDeadline();
            if (_terminationCode is null && !_committed)
            {
                _budget = TimeSpan.FromSeconds(seconds);
                ObserveDeadline();
                if (_terminationCode is null)
                {
                    ScheduleDeadline();
                }
            }
        }

        ThrowIfCancellationRequested();
    }

    public void ListenForCancellation(Stream? input)
    {
        _controlInput ??= input;
        StartControlReader();
    }

    public void ThrowIfCancellationRequested()
    {
        CheckDeadline();
        _operation.Token.ThrowIfCancellationRequested();
    }

    public string? CommitTerminal(string? businessError = null)
    {
        lock (_state)
        {
            if (!_committed)
            {
                ObserveDeadline();
                _terminationCode ??= businessError;
                _committed = true;
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            return _terminationCode;
        }
    }

    private void StartControlReader()
    {
        if (_controlInput is not null && _controlReader is null && ResponseId is not null)
        {
            _controlReader = ReadControlFramesAsync(_controlInput);
        }
    }

    private void CheckDeadline()
    {
        lock (_state)
        {
            ObserveDeadline();
            if (!_committed && _terminationCode is null)
            {
                ScheduleDeadline();
            }
        }
    }

    private void ObserveDeadline()
    {
        if (!_committed && _terminationCode is null && Elapsed >= _budget)
        {
            _terminationCode = "timeout";
            _operation.Cancel();
        }
    }

    private void ScheduleDeadline()
    {
        // Timer 的单次等待有上限；较长合法预算分段等待，始终以原起点核对。
        var remaining = _budget - Elapsed;
        _timer.Change(remaining > TimeSpan.FromDays(40) ? TimeSpan.FromDays(40) : remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private void AcceptCancellation()
    {
        lock (_state)
        {
            ObserveDeadline();
            if (!_committed && _terminationCode is null)
            {
                _terminationCode = "cancelled";
                Interlocked.Exchange(ref _hostCancelled, 1);
                _operation.Cancel();
            }
        }
    }

    private async Task ReadControlFramesAsync(Stream input)
    {
        var buffer = new byte[512];
        var frame = new byte[4096];
        var length = 0;
        var discard = false;
        try
        {
            int count;
            // 标准输入的底层读取在部分平台不能取消；停止等待不依赖宿主关闭管道。
            while ((count = await input.ReadAsync(buffer, _readerLifetime.Token).AsTask().WaitAsync(_readerLifetime.Token).ConfigureAwait(false)) > 0)
            {
                for (var index = 0; index < count; index++)
                {
                    var value = buffer[index];
                    if (value == '\n')
                    {
                        if (!discard && IsCancelFrame(frame, length))
                        {
                            AcceptCancellation();
                            return;
                        }

                        length = 0;
                        discard = false;
                    }
                    else if (!discard && length < frame.Length)
                    {
                        frame[length++] = value;
                    }
                    else
                    {
                        discard = true;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_readerLifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // 控制管道关闭与 EOF 一样，只停止监听，不取消业务。
        }
    }

    private bool IsCancelFrame(byte[] frame, int length)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(frame, 0, length);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && !JsonProtocol.HasDuplicateProperties(root) &&
                root.EnumerateObject().Count() == 3 &&
                JsonProtocol.GetString(root, "schema_version") == SkillProtocol.ContractVersion &&
                JsonProtocol.GetString(root, "type") == "cancel" &&
                JsonProtocol.GetString(root, "request_id") == ResponseId;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _timer.DisposeAsync().ConfigureAwait(false);
        await _readerLifetime.CancelAsync().ConfigureAwait(false);
        if (_controlReader is not null)
        {
            await _controlReader.ConfigureAwait(false);
        }

        _readerLifetime.Dispose();
        _operation.Dispose();
    }
}
