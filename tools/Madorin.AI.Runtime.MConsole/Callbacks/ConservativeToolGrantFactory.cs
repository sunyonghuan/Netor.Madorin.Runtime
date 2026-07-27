using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.MConsole.Callbacks;

public sealed class ConservativeToolGrantFactory(
    string workspaceRoot,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly string _workspaceRoot = Path.GetFullPath(workspaceRoot);

    public ToolGrant Create(ToolPermissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var grantId = $"mconsole-{Guid.NewGuid():N}";
        return new ToolGrant(
            grantId,
            RequireValue(request.RunId, nameof(request.RunId)),
            _workspaceRoot,
            AllowedReadRoots: [],
            AllowedWriteRoots: [],
            AllowOverwrite: false,
            AllowMove: false,
            AllowDelete: false,
            AllowedExecutables: [],
            AllowPowerShell: false,
            new NetworkPolicy(DenyAll: true, AllowedHosts: []),
            _timeProvider.GetUtcNow().Add(GrantLifetime),
            AllowDelegation: false,
            DelegatedAgentIds: [],
            AllowedToolIds: [request.ToolId],
            AllowedCallIds: [request.CallId],
            MaximumRisk: ParseRisk(request.Risk),
            AllowedEnvironmentVariables: [],
            AgentId: request.AgentId,
            ParentGrantId: null,
            RootGrantId: grantId,
            DelegationChain: [],
            ApprovalRequestId: request.ApprovalRequestId);
    }

    public ToolGrant Create(ApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var grantId = $"mconsole-{Guid.NewGuid():N}";
        return new ToolGrant(
            grantId,
            request.RunId,
            _workspaceRoot,
            AllowedReadRoots: [],
            AllowedWriteRoots: [],
            AllowOverwrite: false,
            AllowMove: false,
            AllowDelete: false,
            AllowedExecutables: [],
            AllowPowerShell: false,
            new NetworkPolicy(DenyAll: true, AllowedHosts: []),
            _timeProvider.GetUtcNow().Add(GrantLifetime),
            AllowDelegation: false,
            DelegatedAgentIds: [],
            AllowedToolIds: [request.ToolId],
            AllowedCallIds: [request.CallId],
            MaximumRisk: request.Risk,
            AllowedEnvironmentVariables: [],
            AgentId: request.AgentId,
            ParentGrantId: null,
            RootGrantId: grantId,
            DelegationChain: [],
            ApprovalRequestId: request.ApprovalRequestId);
    }

    private static ToolRiskLevel ParseRisk(string risk) =>
        Enum.TryParse<ToolRiskLevel>(risk, ignoreCase: true, out var parsed)
            ? parsed
            : ToolRiskLevel.Destructive;

    private static string RequireValue(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"{name} is required to create a grant.");
}
