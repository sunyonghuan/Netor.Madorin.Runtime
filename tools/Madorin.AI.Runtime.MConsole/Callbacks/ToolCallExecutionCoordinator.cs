using System.Collections.Concurrent;

using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.MConsole.Callbacks;

public enum ToolCallCancellationOutcome
{
    CancellationRequested,
    AlreadyCompleted,
    NotFound,
}

public sealed class ToolCallExecutionCoordinator(ToolCallResultStore resultStore)
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight =
        new(StringComparer.Ordinal);
    private readonly ToolCallResultStore _resultStore = resultStore;

    public async Task<StoredToolCallResult> ExecuteAsync(
        string runtimeInstanceId,
        ToolCallRequest request,
        IToolCallExecutor executor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(executor);

        return await _resultStore.GetOrExecuteAsync(
            runtimeInstanceId,
            request,
            new RegisteredExecutor(this, runtimeInstanceId, executor),
            cancellationToken);
    }

    public async Task<ToolResultQueryResponse> QueryAsync(
        string runtimeInstanceId,
        ToolResultQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stored = await _resultStore.GetAsync(
            runtimeInstanceId,
            request.CallId,
            cancellationToken);
        return stored?.ToQueryResponse(request.CorrelationId)
            ?? new ToolResultQueryResponse(
                request.CorrelationId,
                request.CallId,
                ToolCallStatus.Unknown,
                ErrorCode: "HostToolResultUnknown",
                ErrorMessage: "No durable host result exists for this callId.");
    }

    public async Task<ToolCallCancellationOutcome> CancelAsync(
        string runtimeInstanceId,
        string callId,
        CancellationToken cancellationToken = default)
    {
        var key = CreateKey(runtimeInstanceId, callId);
        if (_inFlight.TryGetValue(key, out var source))
        {
            await source.CancelAsync();
            return ToolCallCancellationOutcome.CancellationRequested;
        }

        return await _resultStore.GetAsync(runtimeInstanceId, callId, cancellationToken) is not null
            ? ToolCallCancellationOutcome.AlreadyCompleted
            : ToolCallCancellationOutcome.NotFound;
    }

    private static string CreateKey(string runtimeInstanceId, string callId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        return runtimeInstanceId + "\n" + callId;
    }

    private sealed class RegisteredExecutor(
        ToolCallExecutionCoordinator owner,
        string runtimeInstanceId,
        IToolCallExecutor inner) : IToolCallExecutor
    {
        public async ValueTask<ToolCallResponse> ExecuteAsync(
            ToolCallRequest request,
            CancellationToken cancellationToken = default)
        {
            var key = CreateKey(runtimeInstanceId, request.CallId);
            using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (!owner._inFlight.TryAdd(key, source))
            {
                throw new InvalidOperationException(
                    $"Tool call '{request.CallId}' is already executing.");
            }

            try
            {
                return await inner.ExecuteAsync(request, source.Token);
            }
            finally
            {
                owner._inFlight.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, source));
            }
        }
    }
}
