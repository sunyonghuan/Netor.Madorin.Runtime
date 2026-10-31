namespace Madorin.AI.Runtime.Providers.WorkerHost;

public static partial class ProviderWorkerHost
{
    private sealed partial class Session
    {
        private async Task<int?> DispatchAsync(ProviderWorkerFrame frame)
        {
            switch (frame)
            {
                case HelloFrame hello:
                    return await HandleHelloAsync(hello).ConfigureAwait(false);
                case ShutdownFrame:
                    _shutdownCts.Cancel();
                    CancelAllInvocations();
                    return 0;
                case PingFrame ping when _helloCompleted:
                    await WriteAsync(new PongFrame(ping.Id), _shutdownCts.Token).ConfigureAwait(false);
                    return null;
                case CancelFrame cancel when _helloCompleted:
                    if (cancel.Generation is null || (_blobResolvers.TryGetValue(cancel.InvocationId, out var current)
                        && current.Generation == cancel.Generation))
                    {
                        CancelInvocation(cancel.InvocationId);
                    }
                    return null;
                case BlobChunkFrame chunk when _helloCompleted:
                    if (_blobResolvers.TryGetValue(chunk.InvocationId, out var resolver))
                    {
                        resolver.Accept(chunk);
                    }
                    return null;
                case BlobWriteAckFrame ack when _helloCompleted:
                    if (_blobWriters.TryGetValue(ack.InvocationId, out var writer))
                    {
                        writer.Accept(ack);
                    }
                    return null;
                case InvokeFrame invoke when _helloCompleted:
                    QueueInvoke(invoke);
                    return null;
                case InvokeFrame invoke:
                    await WriteAsync(
                            new ErrorFrame(
                                invoke.InvocationId,
                                ProviderWorkerIpc.ErrorInvokeFailed,
                                "hello is not completed."),
                            _shutdownCts.Token)
                        .ConfigureAwait(false);
                    return null;
                default:
                    return null;
            }
        }

        private async Task<int?> HandleHelloAsync(HelloFrame hello)
        {
            if (_helloCompleted)
            {
                return null;
            }

            if (hello.IpcVersion != ProviderWorkerIpc.Version)
            {
                await WriteAsync(
                        new HelloRejectedFrame(
                            ProviderWorkerIpc.ErrorIpcVersionMismatch,
                            $"ipcVersion {hello.IpcVersion} is not {ProviderWorkerIpc.Version}."),
                        _shutdownCts.Token)
                    .ConfigureAwait(false);
                return null;
            }

            if (!string.Equals(hello.Slug, slug, StringComparison.Ordinal))
            {
                await WriteAsync(
                        new HelloRejectedFrame(
                            ProviderWorkerIpc.ErrorSlugMismatch,
                            $"slug '{hello.Slug}' is not '{slug}'."),
                        _shutdownCts.Token)
                    .ConfigureAwait(false);
                return null;
            }

            _helloCompleted = true;
            await WriteAsync(
                    new HelloOkFrame(
                        ProviderWorkerIpc.Version,
                        slug,
                        protocol,
                        declaredCapabilities,
                        declaredModels),
                    _shutdownCts.Token)
                .ConfigureAwait(false);
            return null;
        }

        private void CancelInvocation(string invocationId)
        {
            if (_invocations.TryGetValue(invocationId, out var cts))
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private void CancelAllInvocations()
        {
            foreach (var cts in _invocations.Values)
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private void QueueInvoke(InvokeFrame invoke)
        {
            var task = RunInvokeAsync(invoke);
            _ = task.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
