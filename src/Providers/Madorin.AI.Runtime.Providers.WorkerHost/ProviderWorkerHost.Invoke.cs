using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Providers.WorkerHost;

public static partial class ProviderWorkerHost
{
    private sealed partial class Session
    {
        private async Task RunInvokeAsync(InvokeFrame invoke)
        {
            RuntimeProviderEvent? terminal = null;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            if (!_invocations.TryAdd(invoke.InvocationId, cts))
            {
                cts.Dispose();
                await WriteSafeErrorAsync(
                        invoke.InvocationId,
                        ProviderWorkerIpc.ErrorInvokeFailed,
                        "Duplicate invocationId.",
                        invoke.Connection, invoke.Generation)
                    .ConfigureAwait(false);
                return;
            }

            try
            {
                terminal = await ExecuteInvokeAsync(invoke, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                terminal = new InvocationFailedProviderEvent(invoke.InvocationId,
                    new Madorin.AI.Runtime.Contracts.RuntimeError("InvokeFailed", "provider", "Invocation cancelled.",
                        false, null, Guid.NewGuid().ToString("N")));
            }
            catch (Exception ex)
            {
                terminal = new InvocationFailedProviderEvent(invoke.InvocationId,
                    new Madorin.AI.Runtime.Contracts.RuntimeError("InvokeFailed", "provider",
                        Redact(ex.Message, invoke.Connection), false, null, Guid.NewGuid().ToString("N")));
            }
            finally
            {
                _invocations.TryRemove(invoke.InvocationId, out _);
                cts.Dispose();
            }
            // Release invocation resources before publishing completion: the caller may immediately reuse its ID.
            if (terminal is not null)
            {
                await WriteAsync(new EventFrame(invoke.InvocationId, terminal, invoke.Generation), _shutdownCts.Token)
                    .ConfigureAwait(false);
            }
        }

        private async Task<RuntimeProviderEvent?> ExecuteInvokeAsync(InvokeFrame invoke, CancellationToken ct)
        {
            if (!ProviderProtocolSlugs.TryMap(invoke.Connection.Protocol, out var mapped)
                || !string.Equals(mapped, slug, StringComparison.Ordinal))
            {
                await WriteSafeErrorAsync(
                        invoke.InvocationId,
                        ProviderWorkerIpc.ErrorProtocolMismatch,
                        $"Protocol '{invoke.Connection.Protocol}' is not slug '{slug}'.",
                        invoke.Connection, invoke.Generation)
                    .ConfigureAwait(false);
                return null;
            }

            IRuntimeProviderAdapter adapter;
            using var blobResolver = new WorkerInputBlobResolver(invoke.InvocationId,
                invoke.Generation ?? string.Empty, invoke.MaximumBufferedBlobBytes, WriteAsync);
            if (!_blobResolvers.TryAdd(invoke.InvocationId, blobResolver))
            {
                throw new InvalidOperationException("Duplicate Worker Blob invocation.");
            }
            try
            {
                adapter = adapterFactory(invoke.Connection, blobResolver);
            }
            catch (Exception ex)
            {
                _blobResolvers.TryRemove(invoke.InvocationId, out _);
                await WriteSafeErrorAsync(
                        invoke.InvocationId,
                        ProviderWorkerIpc.ErrorInvokeFailed,
                        ex.Message,
                        invoke.Connection, invoke.Generation)
                    .ConfigureAwait(false);
                return null;
            }

            RuntimeProviderEvent? terminal = null;
            try
            {
                using var writer = new WorkerOutputBlobWriter(invoke.InvocationId, invoke.Generation ?? string.Empty,
                    invoke.MaximumBufferedBlobBytes, WriteAsync);
                _blobWriters[invoke.InvocationId] = writer;
                var request = invoke.Request with
                {
                    CancellationToken = ct,
                    Skills = null,
                    HistoryMessageCount = invoke.HistoryMessageCount,
                    StoreOutputBlob = writer.StoreAsync,
                    MaximumBufferedBlobBytes = invoke.MaximumBufferedBlobBytes
                };

                var sawTerminal = false;
                await foreach (var providerEvent in ProviderInvocationBoundary.CompleteStreamingAsync(adapter, request, ct)
                    .ConfigureAwait(false))
                {
                    sawTerminal |= providerEvent is InvocationCompletedProviderEvent
                        or InvocationFailedProviderEvent;
                    if (providerEvent is InvocationCompletedProviderEvent or InvocationFailedProviderEvent)
                    {
                        terminal = providerEvent;
                        break;
                    }
                    await WriteAsync(
                            new EventFrame(invoke.InvocationId, providerEvent, invoke.Generation),
                            ct)
                        .ConfigureAwait(false);
                }

                if (!sawTerminal)
                {
                    await WriteSafeErrorAsync(
                            invoke.InvocationId,
                            ProviderWorkerIpc.ErrorInvokeFailed,
                            "Stream ended without a terminal event.",
                            invoke.Connection, invoke.Generation)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                _blobWriters.TryRemove(invoke.InvocationId, out _);
                _blobResolvers.TryRemove(invoke.InvocationId, out _);
                switch (adapter)
                {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
            return terminal;
        }

        private Task WriteSafeErrorAsync(
            string? invocationId,
            string code,
            string message,
            ProviderConnectionConfig? connection,
            string? generation = null)
        {
            var safe = Redact(message, connection);
            try
            {
                stderr.WriteLine($"worker error {code} {invocationId}: {safe}");
            }
            catch (IOException)
            {
            }

            return WriteAsync(new ErrorFrame(invocationId, code, safe, generation), _shutdownCts.Token);
        }

        private static string Redact(string message, ProviderConnectionConfig? connection)
        {
            if (connection is null || string.IsNullOrEmpty(connection.ApiKey))
            {
                return message;
            }

            return message.Replace(connection.ApiKey, "***", StringComparison.Ordinal);
        }
    }
}
