using System.Diagnostics;
using System.Threading.Channels;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.WorkerHost;

namespace Madorin.AI.Runtime.Server.ProviderWorkers;

public sealed partial class ProviderWorkerProcess
{
    private async Task ReadLoopAsync(Stream stdout, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await ProviderWorkerFrameCodec.ReadAsync(stdout, cancellationToken)
                    .ConfigureAwait(false);
                if (read.Status == ProviderWorkerFrameReadStatus.EndOfStream)
                {
                    RecordUnexpectedCrash();
                    FailAll("Provider worker stdout closed.");
                    return;
                }

                if (read.Status == ProviderWorkerFrameReadStatus.Oversize)
                {
                    RecordUnexpectedCrash();
                    FailAll("Provider worker frame exceeded 16 MiB.");
                    await KillAsync().ConfigureAwait(false);
                    return;
                }

                DispatchFrame(read.Frame!);
            }
        }
        catch (OperationCanceledException)
        {
            FailAll("Provider worker reader cancelled.");
        }
        catch (IOException)
        {
            RecordUnexpectedCrash();
            FailAll("Provider worker I/O failed.");
        }
    }

    private void DispatchFrame(ProviderWorkerFrame frame)
    {
        switch (frame)
        {
            case HelloOkFrame:
                _hello?.TrySetResult(new HelloFrameResult(true, null, null));
                break;
            case HelloRejectedFrame rejected:
                _hello?.TrySetResult(new HelloFrameResult(false, rejected.Code, rejected.Message));
                break;
            case PongFrame pong:
                if (_pings.TryGetValue(pong.Id, out var ping))
                {
                    ping.TrySetResult(true);
                }

                break;
            case EventFrame ev:
                CompleteEvent(ev.InvocationId, ev.Event);
                break;
            case ErrorFrame error:
                CompleteError(error);
                break;
        }
    }

    private void CompleteEvent(string invocationId, RuntimeProviderEvent providerEvent)
    {
        if (!_invokes.TryGetValue(invocationId, out var channel))
        {
            return;
        }

        channel.Writer.TryWrite(providerEvent);
        if (providerEvent is InvocationCompletedProviderEvent or InvocationFailedProviderEvent)
        {
            channel.Writer.TryComplete();
        }
    }

    private void CompleteError(ErrorFrame error)
    {
        if (error.InvocationId is null)
        {
            FailAll(error.Message);
            return;
        }

        if (_invokes.TryGetValue(error.InvocationId, out var channel))
        {
            channel.Writer.TryWrite(
                new InvocationFailedProviderEvent(
                    error.InvocationId,
                    MapWorkerError(error.Code, error.Message)));
            channel.Writer.TryComplete();
        }
    }

    private void FailAll(string message)
    {
        foreach (var pair in _invokes)
        {
            pair.Value.Writer.TryWrite(
                new InvocationFailedProviderEvent(pair.Key, WorkerExitedError(message)));
            pair.Value.Writer.TryComplete();
        }
    }

    private void RecordUnexpectedCrash()
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _crashRecorded, 1) == 1)
        {
            return;
        }

        _ = _fuse.RecordCrash();
    }

    private async Task KillAsync()
    {
        FailAll("Provider worker process was killed.");
        ForceKillTwice();
        await ReapAsync().ConfigureAwait(false);
    }

    private void ForceKillTwice()
    {
        if (_process is not { HasExited: false })
        {
            return;
        }

        TryKillProcess();
        if (_process is { HasExited: false })
        {
            TryKillProcess();
        }

        if (_process is { HasExited: false })
        {
            Trace.WriteLine($"Provider worker '{_slug}' still alive after two Kill attempts.");
        }
    }

    private void TryKillProcess()
    {
        try
        {
            _process?.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private async Task ReapAsync()
    {
        try
        {
            _readerCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (_reader is not null)
        {
            try
            {
                await _reader.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _process?.Dispose();
        _process = null;
        _stdin = null;
        _readerCts?.Dispose();
        _readerCts = null;
        _reader = null;
        _hello = null;
    }

    private async Task ShutdownAsync()
    {
        Interlocked.Exchange(ref _stopping, 1);
        if (_process is { HasExited: false } && _stdin is not null)
        {
            try
            {
                await WriteFrameAsync(new ShutdownFrame(), CancellationToken.None).ConfigureAwait(false);
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ForceKillTwice();
            }
            catch (IOException)
            {
                ForceKillTwice();
            }
        }

        await ReapAsync().ConfigureAwait(false);
    }
}
