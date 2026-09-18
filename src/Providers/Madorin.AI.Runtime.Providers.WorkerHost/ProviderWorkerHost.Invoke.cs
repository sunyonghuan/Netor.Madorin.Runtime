using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Providers.WorkerHost;

public static partial class ProviderWorkerHost
{
    private sealed partial class Session
    {
        private async Task RunInvokeAsync(InvokeFrame invoke)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            if (!_invocations.TryAdd(invoke.InvocationId, cts))
            {
                cts.Dispose();
                await WriteSafeErrorAsync(
                        invoke.InvocationId,
                        ProviderWorkerIpc.ErrorInvokeFailed,
                        "Duplicate invocationId.",
                        invoke.Connection)
                    .ConfigureAwait(false);
                return;
            }

            try
            {
                await ExecuteInvokeAsync(invoke, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await WriteSafeErrorAsync(
                        invoke.InvocationId,
                        ProviderWorkerIpc.ErrorInvokeFailed,
                        "Invocation cancelled.",
                        invoke.Connection)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteSafeErrorAsync(
                        invoke.InvocationId,
                        ProviderWorkerIpc.ErrorInvokeFailed,
                        ex.Message,
                        invoke.Connection)
                    .ConfigureAwait(false);
            }
            finally
            {
                _invocations.TryRemove(invoke.InvocationId, out _);
                cts.Dispose();
            }
        }

        private async Task ExecuteInvokeAsync(InvokeFrame invoke, CancellationToken ct)
        {
            if (!ProviderProtocolSlugs.TryMap(invoke.Connection.Protocol, out var mapped)
                || !string.Equals(mapped, slug, StringComparison.Ordinal))
            {
                await WriteSafeErrorAsync(
                        invoke.InvocationId,
                        ProviderWorkerIpc.ErrorProtocolMismatch,
                        $"Protocol '{invoke.Connection.Protocol}' is not slug '{slug}'.",
                        invoke.Connection)
                    .ConfigureAwait(false);
                return;
            }

            IRuntimeProviderAdapter adapter;
            try
            {
                adapter = adapterFactory(invoke.Connection);
            }
            catch (Exception ex)
            {
                await WriteSafeErrorAsync(
                        invoke.InvocationId,
                        ProviderWorkerIpc.ErrorInvokeFailed,
                        ex.Message,
                        invoke.Connection)
                    .ConfigureAwait(false);
                return;
            }

            try
            {
                var request = invoke.Request with
                {
                    CancellationToken = ct,
                    Skills = null
                };

                var sawTerminal = false;
                await foreach (var providerEvent in adapter.CompleteStreamingAsync(request, ct)
                    .ConfigureAwait(false))
                {
                    sawTerminal |= providerEvent is InvocationCompletedProviderEvent
                        or InvocationFailedProviderEvent;
                    await WriteAsync(
                            new EventFrame(invoke.InvocationId, providerEvent),
                            ct)
                        .ConfigureAwait(false);
                }

                if (!sawTerminal)
                {
                    await WriteSafeErrorAsync(
                            invoke.InvocationId,
                            ProviderWorkerIpc.ErrorInvokeFailed,
                            "Stream ended without a terminal event.",
                            invoke.Connection)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
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
        }

        private Task WriteSafeErrorAsync(
            string? invocationId,
            string code,
            string message,
            ProviderConnectionConfig? connection)
        {
            var safe = Redact(message, connection);
            try
            {
                stderr.WriteLine($"worker error {code} {invocationId}: {safe}");
            }
            catch (IOException)
            {
            }

            return WriteAsync(new ErrorFrame(invocationId, code, safe), _shutdownCts.Token);
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
