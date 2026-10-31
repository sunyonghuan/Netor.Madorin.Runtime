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

                if (read.Frame is BlobWriteFrame write)
                {
                    await RespondToBlobWriteAsync(write).ConfigureAwait(false);
                }
                else
                {
                    DispatchFrame(read.Frame!);
                }
            }
        }
        catch (OperationCanceledException)
        {
            FailAll("Provider worker reader cancelled.");
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            RecordUnexpectedCrash();
            FailAll("Provider worker I/O failed.");
        }
    }

    private void DispatchFrame(ProviderWorkerFrame frame)
    {
        switch (frame)
        {
            case HelloOkFrame helloOk:
                _negotiatedCapabilities = helloOk.Capabilities;
                _negotiatedModels = helloOk.Models;
                _hello?.TrySetResult(new HelloFrameResult(true, null, null, helloOk.Capabilities));
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
                if (!_blobOutputs.TryGetValue(ev.InvocationId, out var output)
                    || ev.Generation != output.Generation)
                {
                    break;
                }
                if (ev.Event is AttachmentCompletedProviderEvent attachment && !output.Contains(attachment.Blob))
                {
                    CompleteError(new ErrorFrame(ev.InvocationId, ProviderWorkerIpc.ErrorInvokeFailed,
                        "Worker returned an unconfirmed output attachment."));
                    break;
                }
                CompleteEvent(ev.InvocationId, ev.Event);
                break;
            case BlobReadFrame read:
                _ = RespondToBlobReadAsync(read);
                break;
            case ErrorFrame error:
                CompleteError(error);
                break;
        }
    }

    private async Task RespondToBlobWriteAsync(BlobWriteFrame write)
    {
        if (!_blobOutputs.TryGetValue(write.InvocationId, out var lease))
        {
            return;
        }
        BlobWriteAckFrame response;
        try
        {
            response = await lease.WriteAsync(write).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or OperationCanceledException)
        {
            response = new(write.InvocationId, write.Generation, write.TransferId, write.Operation, write.Offset,
                Error: "Worker output attachment denied, cancelled, invalid or unavailable.");
        }
        try
        {
            await WriteFrameAsync(response, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private async Task RespondToBlobReadAsync(BlobReadFrame read)
    {
        BlobChunkFrame response;
        try
        {
            if (!_blobInputs.TryGetValue(read.InvocationId, out var lease))
            {
                return;
            }
            response = await lease.ReadAsync(read).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or OperationCanceledException)
        {
            response = new BlobChunkFrame(read.InvocationId, read.Generation, read.TransferId,
                read.Offset, [], false, "Worker attachment read denied, cancelled, invalid or unavailable.");
        }
        try
        {
            await WriteFrameAsync(response, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 调用取消或 Worker 退出后不重开传输。
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

        if (error.Generation is not null
            && (!_blobOutputs.TryGetValue(error.InvocationId, out var output)
                || error.Generation != output.Generation))
        {
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
