using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Services.Tools;

/// <summary>Provides the single permission and approval authority for one Runtime execution.</summary>
public interface IToolConsentHandler
{
    public bool CanRequestPermission => true;

    public bool CanRequestApproval => true;

    public Task<ToolPermissionResponse> RequestPermissionAsync(
        ToolPermissionConsentContext context,
        CancellationToken ct);

    public Task<ApprovalResponse> RequestApprovalAsync(
        ToolApprovalConsentContext context,
        CancellationToken ct);
}

/// <summary>Provides Runtime-only context for a tool permission request.</summary>
public sealed record ToolPermissionConsentContext(
    ToolPermissionRequest Request,
    ToolInvocation Invocation,
    ToolDescriptor Descriptor,
    ToolConsentScopeResolution ScopeResolution,
    string WorkspaceRoot);

/// <summary>Provides Runtime-only context for a tool or workflow approval request.</summary>
public sealed record ToolApprovalConsentContext(
    ApprovalRequest Request,
    ToolInvocation? Invocation,
    ToolDescriptor? Descriptor,
    ToolConsentScopeResolution ScopeResolution,
    string WorkspaceRoot);

/// <summary>Adapts the existing Reverse RPC permission and approval delegates to the unified handler.</summary>
public sealed class ReverseRpcToolConsentHandler(
    Func<ToolPermissionRequest, CancellationToken, Task<ToolPermissionResponse>>?
        permissionHandler,
    Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse>>?
        approvalHandler) : IToolConsentHandler
{
    private readonly Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse>>?
        _approvalHandler = approvalHandler;
    private readonly Func<ToolPermissionRequest, CancellationToken, Task<ToolPermissionResponse>>?
        _permissionHandler = permissionHandler;

    public bool CanRequestPermission => _permissionHandler is not null;

    public bool CanRequestApproval => _approvalHandler is not null;

    public Task<ToolPermissionResponse> RequestPermissionAsync(
        ToolPermissionConsentContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _permissionHandler is null
            ? Task.FromException<ToolPermissionResponse>(
                new InvalidOperationException("The consent authority does not support permission requests."))
            : _permissionHandler(context.Request, ct);
    }

    public Task<ApprovalResponse> RequestApprovalAsync(
        ToolApprovalConsentContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _approvalHandler is null
            ? Task.FromException<ApprovalResponse>(
                new InvalidOperationException("The consent authority does not support approval requests."))
            : _approvalHandler(context.Request, ct);
    }
}
