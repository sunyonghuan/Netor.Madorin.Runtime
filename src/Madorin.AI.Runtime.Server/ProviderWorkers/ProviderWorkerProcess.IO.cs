using System.Diagnostics;
using System.Threading.Channels;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.WorkerHost;

namespace Madorin.AI.Runtime.Server.ProviderWorkers;

public sealed partial class ProviderWorkerProcess
{
    public async IAsyncEnumerable<RuntimeProviderEvent> InvokeAsync(
        ProviderConnectionConfig connection,
        RuntimeProviderRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var fusedFailure = _fuse.IsFused;
        RuntimeError? startupFailure = null;
        if (!fusedFailure)
        {
            try
            {
                await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException) when (_fuse.IsFused)
            {
                fusedFailure = true;
            }
            catch (Exception ex) when (ex is FileNotFoundException
                or TimeoutException
                or InvalidOperationException)
            {
                startupFailure = MapStartupFailure(ex);
            }
        }

        if (fusedFailure)
        {
            yield return new InvocationFailedProviderEvent(
                request.InvocationId,
                WorkerExitedError("Provider worker is fused after repeated crashes."));
            yield break;
        }

        if (startupFailure is not null)
        {
            yield return new InvocationFailedProviderEvent(request.InvocationId, startupFailure);
            yield break;
        }

        RememberSecret(connection.ApiKey);
        var channel = Channel.CreateUnbounded<RuntimeProviderEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        if (!_invokes.TryAdd(request.InvocationId, channel))
        {
            throw new InvalidOperationException($"Duplicate invocationId '{request.InvocationId}'.");
        }

        using var cancelReg = cancellationToken.Register(() =>
            _ = SendCancelAsync(request.InvocationId));

        RuntimeError? writeFailure = null;
        try
        {
            await WriteFrameAsync(
                    new InvokeFrame(request.InvocationId, connection, request),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProviderWorkerFrameTooLargeException ex)
        {
            writeFailure = InvokeFailedError($"Invoke frame exceeds 16 MiB ({ex.Length} bytes).");
        }
        catch (IOException)
        {
            writeFailure = WorkerExitedError("Provider worker stdin closed.");
        }
        catch (ObjectDisposedException)
        {
            writeFailure = WorkerExitedError("Provider worker stdin closed.");
        }

        if (writeFailure is not null)
        {
            _invokes.TryRemove(request.InvocationId, out _);
            yield return new InvocationFailedProviderEvent(request.InvocationId, writeFailure);
            yield break;
        }

        try
        {
            await foreach (var providerEvent in channel.Reader.ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return providerEvent;
            }
        }
        finally
        {
            _invokes.TryRemove(request.InvocationId, out _);
        }
    }

    private async Task SendCancelAsync(string invocationId)
    {
        try
        {
            await WriteFrameAsync(new CancelFrame(invocationId), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The worker may already have exited when cancellation is delivered.
        }
    }

    private async Task WriteFrameAsync(ProviderWorkerFrame frame, CancellationToken cancellationToken)
    {
        var stdin = _stdin ?? throw new InvalidOperationException("Worker stdin is not available.");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ProviderWorkerFrameCodec.WriteAsync(stdin, frame, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<bool> TryPingAsync(CancellationToken cancellationToken)
    {
        if (_process is null || _process.HasExited || _stdin is null)
        {
            return false;
        }

        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pings[id] = tcs;
        try
        {
            await WriteFrameAsync(new PingFrame(id), cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            return await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _pings.TryRemove(id, out _);
        }
    }

    private static RuntimeError WorkerExitedError(string message) =>
        CreateProviderError("ProviderWorkerExited", message, isRetryable: true);

    private static RuntimeError InvokeFailedError(string message) =>
        CreateProviderError("InvokeFailed", message, isRetryable: false);

    private static RuntimeError MapWorkerError(string code, string message) =>
        code switch
        {
            ProviderWorkerIpc.ErrorInvokeFailed => InvokeFailedError(message),
            ProviderWorkerIpc.ErrorProtocolMismatch =>
                CreateProviderError("ProtocolMismatch", message, isRetryable: false),
            _ => WorkerExitedError(message)
        };

    private RuntimeError MapStartupFailure(Exception exception)
    {
        var message = exception is FileNotFoundException
            ? $"Provider worker not found for protocol '{_protocol}' slug '{_slug}' in '{_directory}'."
            : exception.Message;
        return CreateProviderError("ProviderWorkerUnavailable", message, isRetryable: false);
    }

    private static RuntimeError CreateProviderError(string code, string message, bool isRetryable) =>
        new(
            code,
            "provider",
            message,
            isRetryable,
            ProviderDetails: null,
            DiagnosticId: Guid.NewGuid().ToString("N"));

    private void RememberSecret(string? secret)
    {
        if (!string.IsNullOrEmpty(secret))
        {
            _secrets[secret] = 0;
        }
    }

    private string RedactSecrets(string value)
    {
        var redacted = value;
        foreach (var secret in _secrets.Keys)
        {
            if (!string.IsNullOrEmpty(secret))
            {
                redacted = redacted.Replace(secret, "***", StringComparison.Ordinal);
            }
        }

        return redacted;
    }

    private void WriteWorkerLog(string line)
    {
        if (_workerLog is not null)
        {
            _workerLog(line);
            return;
        }

        Trace.WriteLine(line);
    }
}
