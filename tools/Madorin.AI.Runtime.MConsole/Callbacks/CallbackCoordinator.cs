using System.Collections.Concurrent;

using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.MConsole.Callbacks;

public sealed class CallbackCoordinator
{
    private readonly TimeProvider _timeProvider;
    private readonly ConservativeToolGrantFactory _grantFactory;
    private readonly ToolCallExecutionCoordinator _toolCalls;
    private readonly IToolCallExecutor _toolExecutor;
    private readonly ConcurrentDictionary<string, IPendingCallback> _pending =
        new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<HostInteractionResult> _history = new();
    private CallbackPolicy _policy;

    public CallbackCoordinator(
        CallbackPolicy policy,
        ConservativeToolGrantFactory grantFactory,
        ToolCallExecutionCoordinator toolCalls,
        IToolCallExecutor toolExecutor,
        TimeProvider? timeProvider = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _grantFactory = grantFactory;
        _toolCalls = toolCalls;
        _toolExecutor = toolExecutor;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public CallbackPolicy Policy
    {
        get => Volatile.Read(ref _policy);
        set => Volatile.Write(
            ref _policy,
            value ?? throw new ArgumentNullException(nameof(value)));
    }

    public IReadOnlyList<CallbackRequestEnvelope> Pending => _pending.Values
        .Select(static item => item.Envelope)
        .OrderBy(static item => item.ArrivedAt)
        .ToArray();

    public IReadOnlyList<HostInteractionResult> History => [.. _history];

    public RuntimeHostCallbacks CreateCallbacks(
        string runtimeInstanceId,
        int maxConcurrency = 4,
        TimeSpan? callbackTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
        if (maxConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrency),
                maxConcurrency,
                "Callback concurrency must be positive.");
        }

        var timeout = callbackTimeout ?? TimeSpan.FromSeconds(30);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(callbackTimeout),
                timeout,
                "Callback timeout must be positive.");
        }

        return new RuntimeHostCallbacks
        {
            MaxConcurrency = maxConcurrency,
            CallbackTimeout = timeout,
            ToolPermissionRequestedAsync = (request, cancellationToken) =>
                HandlePermissionAsync(runtimeInstanceId, request, timeout, cancellationToken),
            ApprovalRequestedAsync = (request, cancellationToken) =>
                HandleApprovalAsync(runtimeInstanceId, request, timeout, cancellationToken),
            ToolCallRequestedAsync = (request, cancellationToken) =>
                HandleToolCallAsync(runtimeInstanceId, request, timeout, cancellationToken),
            ToolResultQueriedAsync = (request, cancellationToken) =>
                HandleQueryAsync(runtimeInstanceId, request, timeout, cancellationToken),
            ToolCallCancelledAsync = (request, cancellationToken) =>
                HandleCancelAsync(runtimeInstanceId, request, timeout, cancellationToken),
        };
    }

    public bool RespondToPermission(string callbackId, ToolPermissionResponse response) =>
        TryRespond(callbackId, response);

    public bool RespondToApproval(string callbackId, ApprovalResponse response) =>
        TryRespond(callbackId, response);

    public bool RespondToToolCall(string callbackId, ToolCallResponse response) =>
        TryRespond(callbackId, response);

    public bool RespondToQuery(string callbackId, ToolResultQueryResponse response) =>
        TryRespond(callbackId, response);

    public bool RespondToCancellation(string callbackId, bool shouldCancel) =>
        TryRespond(callbackId, shouldCancel);

    private async ValueTask<ToolPermissionResponse> HandlePermissionAsync(
        string runtimeInstanceId,
        ToolPermissionRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var envelope = CreateEnvelope(
            HostCallbackKind.ToolPermission,
            runtimeInstanceId,
            request.RunId,
            request.CallId,
            request.AgentId,
            grantId: null,
            request.ToolId,
            $"Permission requested for tool '{request.ToolId}'.",
            timeout);
        var manual = envelope.PolicyMode is CallbackPolicyMode.Manual;
        try
        {
            ToolPermissionResponse response;
            if (manual)
            {
                response = await WaitForManualResponseAsync<ToolPermissionResponse>(
                    envelope,
                    cancellationToken);
            }
            else if (Policy.AllowsTool(request.ToolId))
            {
                response = new ToolPermissionResponse(
                    request.CorrelationId ?? request.ApprovalRequestId,
                    request.CallId,
                    ToolAuthorizationDecision.Granted,
                    _grantFactory.Create(request));
            }
            else
            {
                response = DenyPermission(request, "The active host policy did not allow this tool.");
            }

            Record(envelope, response.Decision.ToString(), response.Reason, manual);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var response = DenyPermission(request, "The host permission callback was cancelled.");
            Record(envelope, response.Decision.ToString(), response.Reason, manual);
            return response;
        }
        catch (Exception ex) when (IsExpectedCallbackException(ex))
        {
            var response = DenyPermission(request, "The host could not safely evaluate this permission.");
            Record(envelope, response.Decision.ToString(), ex.GetType().Name, manual);
            return response;
        }
    }

    private async ValueTask<ApprovalResponse> HandleApprovalAsync(
        string runtimeInstanceId,
        ApprovalRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var envelope = CreateEnvelope(
            HostCallbackKind.Approval,
            runtimeInstanceId,
            request.RunId,
            request.CallId,
            request.AgentId,
            grantId: null,
            request.ToolId,
            $"Approval requested for {request.Risk} tool '{request.ToolId}'.",
            timeout);
        var manual = envelope.PolicyMode is CallbackPolicyMode.Manual;
        try
        {
            ApprovalResponse response;
            var policy = Policy;
            if (manual)
            {
                response = await WaitForManualResponseAsync<ApprovalResponse>(
                    envelope,
                    cancellationToken);
            }
            else if (request.Risk is ToolRiskLevel.Low
                && policy.AllowLowRiskApprovals
                && policy.AllowsTool(request.ToolId))
            {
                response = new ApprovalResponse(
                    request.CorrelationId,
                    request.ApprovalRequestId,
                    ToolAuthorizationDecision.Granted,
                    _grantFactory.Create(request));
            }
            else
            {
                response = DenyApproval(request, "The active host policy did not approve this request.");
            }

            Record(envelope, response.Decision.ToString(), response.Reason, manual);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var response = DenyApproval(request, "The host approval callback was cancelled.");
            Record(envelope, response.Decision.ToString(), response.Reason, manual);
            return response;
        }
        catch (Exception ex) when (IsExpectedCallbackException(ex))
        {
            var response = DenyApproval(request, "The host could not safely evaluate this approval.");
            Record(envelope, response.Decision.ToString(), ex.GetType().Name, manual);
            return response;
        }
    }

    private async ValueTask<ToolCallResponse> HandleToolCallAsync(
        string runtimeInstanceId,
        ToolCallRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var envelope = CreateEnvelope(
            HostCallbackKind.ToolCall,
            runtimeInstanceId,
            request.RunId,
            request.CallId,
            request.AgentId,
            request.GrantId,
            request.ToolId,
            $"Execution requested for tool '{request.ToolId}'.",
            timeout);
        var manual = envelope.PolicyMode is CallbackPolicyMode.Manual;
        try
        {
            IToolCallExecutor executor = manual
                ? new ManualToolCallExecutor(this, envelope)
                : Policy.AllowsTool(request.ToolId)
                    ? _toolExecutor
                    : new FixedToolCallExecutor(new ToolCallResponse(
                        request.CorrelationId,
                        request.CallId,
                        ToolCallStatus.Failed,
                        ErrorCode: "HostToolDenied",
                        ErrorMessage: "The active host policy did not allow this tool."));
            var stored = await _toolCalls.ExecuteAsync(
                runtimeInstanceId,
                request,
                executor,
                cancellationToken);
            var response = stored.ToCallResponse(request.CorrelationId);
            Record(envelope, response.Status.ToString(), response.ErrorCode, manual);
            return response;
        }
        catch (Exception ex) when (IsExpectedCallbackException(ex))
        {
            var response = new ToolCallResponse(
                request.CorrelationId,
                request.CallId,
                ToolCallStatus.Failed,
                ErrorCode: "HostCallbackFailed",
                ErrorMessage: "The host tool callback failed.");
            Record(envelope, response.Status.ToString(), ex.GetType().Name, manual);
            return response;
        }
    }

    private async ValueTask<ToolResultQueryResponse> HandleQueryAsync(
        string runtimeInstanceId,
        ToolResultQueryRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var envelope = CreateEnvelope(
            HostCallbackKind.ToolResultQuery,
            runtimeInstanceId,
            runId: null,
            request.CallId,
            agentId: null,
            grantId: null,
            toolId: null,
            "Durable tool result queried.",
            timeout);
        var manual = envelope.PolicyMode is CallbackPolicyMode.Manual;
        try
        {
            var response = manual
                ? await WaitForManualResponseAsync<ToolResultQueryResponse>(
                    envelope,
                    cancellationToken)
                : await _toolCalls.QueryAsync(runtimeInstanceId, request, cancellationToken);
            Record(envelope, response.Status.ToString(), response.ErrorCode, manual);
            return response;
        }
        catch (Exception ex) when (IsExpectedCallbackException(ex))
        {
            var response = new ToolResultQueryResponse(
                request.CorrelationId,
                request.CallId,
                ToolCallStatus.Unknown,
                ErrorCode: "HostQueryFailed",
                ErrorMessage: "The host could not query the durable tool result.");
            Record(envelope, response.Status.ToString(), ex.GetType().Name, manual);
            return response;
        }
    }

    private async ValueTask<bool> HandleCancelAsync(
        string runtimeInstanceId,
        ToolCallCancelRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var envelope = CreateEnvelope(
            HostCallbackKind.ToolCallCancel,
            runtimeInstanceId,
            runId: null,
            request.CallId,
            agentId: null,
            grantId: null,
            toolId: null,
            "Tool call cancellation requested.",
            timeout);
        var manual = envelope.PolicyMode is CallbackPolicyMode.Manual;
        try
        {
            var shouldCancel = !manual
                || await WaitForManualResponseAsync<bool>(envelope, cancellationToken);
            var outcome = shouldCancel
                ? await _toolCalls.CancelAsync(runtimeInstanceId, request.CallId, cancellationToken)
                : ToolCallCancellationOutcome.NotFound;
            var accepted = outcome is ToolCallCancellationOutcome.CancellationRequested;
            Record(envelope, outcome.ToString(), request.Reason, manual);
            return accepted;
        }
        catch (Exception ex) when (IsExpectedCallbackException(ex))
        {
            Record(envelope, "Failed", ex.GetType().Name, manual);
            return false;
        }
    }

    private async Task<T> WaitForManualResponseAsync<T>(
        CallbackRequestEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var pending = new PendingCallback<T>(envelope);
        if (!_pending.TryAdd(envelope.CallbackId, pending))
        {
            throw new InvalidOperationException(
                $"Callback '{envelope.CallbackId}' is already pending.");
        }

        try
        {
            return await pending.Completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _pending.TryRemove(
                new KeyValuePair<string, IPendingCallback>(envelope.CallbackId, pending));
        }
    }

    private bool TryRespond<T>(string callbackId, T response)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackId);
        return _pending.TryGetValue(callbackId, out var value)
            && value is PendingCallback<T> pending
            && pending.Completion.TrySetResult(response);
    }

    private CallbackRequestEnvelope CreateEnvelope(
        HostCallbackKind kind,
        string runtimeInstanceId,
        string? runId,
        string? callId,
        string? agentId,
        string? grantId,
        string? toolId,
        string summary,
        TimeSpan timeout)
    {
        var now = _timeProvider.GetUtcNow();
        return new CallbackRequestEnvelope(
            $"callback-{Guid.NewGuid():N}",
            kind,
            runtimeInstanceId,
            runId,
            callId,
            agentId,
            grantId,
            toolId,
            summary,
            now,
            now.Add(timeout),
            Policy.Mode);
    }

    private void Record(
        CallbackRequestEnvelope envelope,
        string outcome,
        string? detail,
        bool manual) => _history.Enqueue(new HostInteractionResult(
            envelope.CallbackId,
            envelope.Kind,
            envelope.RuntimeInstanceId,
            envelope.RunId,
            envelope.CallId,
            outcome,
            detail,
            manual,
            envelope.ArrivedAt,
            _timeProvider.GetUtcNow()));

    private static ToolPermissionResponse DenyPermission(
        ToolPermissionRequest request,
        string reason) => new(
            request.CorrelationId ?? request.ApprovalRequestId,
            request.CallId,
            ToolAuthorizationDecision.Denied,
            Reason: reason);

    private static ApprovalResponse DenyApproval(ApprovalRequest request, string reason) => new(
        request.CorrelationId,
        request.ApprovalRequestId,
        ToolAuthorizationDecision.Denied,
        Reason: reason);

    private static bool IsExpectedCallbackException(Exception exception) => exception is
        ArgumentException
        or InvalidDataException
        or InvalidOperationException
        or IOException
        or OperationCanceledException
        or TimeoutException;

    private interface IPendingCallback
    {
        public CallbackRequestEnvelope Envelope { get; }
    }

    private sealed class PendingCallback<T>(CallbackRequestEnvelope envelope) : IPendingCallback
    {
        public CallbackRequestEnvelope Envelope { get; } = envelope;

        public TaskCompletionSource<T> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ManualToolCallExecutor(
        CallbackCoordinator owner,
        CallbackRequestEnvelope envelope) : IToolCallExecutor
    {
        public ValueTask<ToolCallResponse> ExecuteAsync(
            ToolCallRequest request,
            CancellationToken cancellationToken = default) => new(
                owner.WaitForManualResponseAsync<ToolCallResponse>(envelope, cancellationToken));
    }

    private sealed class FixedToolCallExecutor(ToolCallResponse response) : IToolCallExecutor
    {
        public ValueTask<ToolCallResponse> ExecuteAsync(
            ToolCallRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(response);
    }
}
