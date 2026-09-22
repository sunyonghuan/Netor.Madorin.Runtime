namespace Madorin.AI.Runtime.Tools.Builtin.Work;

/// <summary>
/// Bridges mcli_work_dispatch_step tool execution to the live WorkModeOrchestrator handler.
/// RuntimeExecutionCore creates one bridge per run; the orchestrator binds/unbinds the handler.
/// </summary>
public sealed class WorkStepDispatchBridge
{
    private Func<WorkDispatchStepRequest, CancellationToken, Task<WorkDispatchStepResult>>? _handler;

    public void Bind(
        Func<WorkDispatchStepRequest, CancellationToken, Task<WorkDispatchStepResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    public void Unbind() => _handler = null;

    public Task<WorkDispatchStepResult> RunAsync(
        WorkDispatchStepRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var handler = _handler;
        if (handler is null)
        {
            return Task.FromResult(
                new WorkDispatchStepResult(
                    false,
                    ErrorCode: "dispatch_runner_unavailable",
                    ErrorMessage: "No live Work orchestrator dispatch handler is bound for this run."));
        }

        return handler(request, ct);
    }
}

/// <summary>Validated dispatch target handed to the Work orchestrator.</summary>
public sealed record WorkDispatchStepRequest(
    string SessionId,
    string RunId,
    string AgentId,
    string CallId,
    string PlanVersion,
    string StepId,
    string TargetAgentId,
    string Goal,
    string Instruction,
    int StepIndex,
    int SubStepIndex,
    string? InvocationId = null,
    string? ParentInvocationId = null,
    string? WorkStepId = null);

/// <summary>Result of a synchronous orchestrator-backed dispatch.</summary>
public sealed record WorkDispatchStepResult(
    bool Executed,
    string? OutputText = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? ChildInvocationId = null);
