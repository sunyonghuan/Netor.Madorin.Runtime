using System.Collections.Frozen;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Cli.Tools;

/// <summary>
/// 非交互运行时，只自动放行命令行明确列出的工具。
/// 删除、覆盖和进程执行必须出现在名单里才会放行。
/// </summary>
internal sealed class StandaloneToolAllowList(IReadOnlySet<string> toolIds) : IToolConsentHandler
{
    private readonly FrozenSet<string> _toolIds = toolIds.ToFrozenSet(StringComparer.Ordinal);
    private readonly StandaloneToolGrantFactory _grants = new();

    public static bool TryParse(string? value, out StandaloneToolAllowList? allowList, out string? error)
    {
        allowList = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "--allow-tools requires one or more tool ids separated by commas.";
            return false;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Contains(' ', StringComparison.Ordinal)
                || part.Contains('*', StringComparison.Ordinal))
            {
                error = $"Tool id '{part}' is not a single tool id. List each id explicitly.";
                return false;
            }

            ids.Add(part);
        }

        if (ids.Count == 0)
        {
            error = "--allow-tools requires one or more tool ids separated by commas.";
            return false;
        }

        allowList = new StandaloneToolAllowList(ids);
        return true;
    }

    public Task<ToolPermissionResponse> RequestPermissionAsync(
        ToolPermissionConsentContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        if (!IsAllowed(context.Request.ToolId, context.ScopeResolution, out var reason))
        {
            return Task.FromResult(DeniedPermission(context, reason));
        }

        var lifetime = StandaloneToolConsentPolicy.CanGrantForRun(context)
            ? StandaloneToolGrantLifetime.Run
            : StandaloneToolGrantLifetime.Once;
        return Task.FromResult(new ToolPermissionResponse(
            context.Request.CorrelationId ?? string.Empty,
            context.Request.CallId,
            ToolAuthorizationDecision.Granted,
            _grants.Create(context, lifetime),
            $"Allowed for this Run by --allow-tools ({context.Request.ToolId})."));
    }

    public Task<ApprovalResponse> RequestApprovalAsync(
        ToolApprovalConsentContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        var reason = "Workflow approval is not covered by --allow-tools.";
        if (context.Invocation is null
            || context.Descriptor is null
            || !IsAllowed(context.Request.ToolId, context.ScopeResolution, out reason))
        {
            return Task.FromResult(DeniedApproval(
                context,
                reason));
        }

        return Task.FromResult(new ApprovalResponse(
            context.Request.CorrelationId,
            context.Request.ApprovalRequestId,
            ToolAuthorizationDecision.Granted,
            _grants.CreateOnce(context),
            $"Approved for this call by --allow-tools ({context.Request.ToolId})."));
    }

    private bool IsAllowed(
        string? toolId,
        ToolConsentScopeResolution resolution,
        out string reason)
    {
        if (!resolution.IsSuccess)
        {
            reason = "The requested tool scope could not be safely normalized.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(toolId) || !_toolIds.Contains(toolId))
        {
            reason = $"Tool '{toolId}' is not included in --allow-tools.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static ToolPermissionResponse DeniedPermission(
        ToolPermissionConsentContext context,
        string reason) =>
        new(
            context.Request.CorrelationId ?? string.Empty,
            context.Request.CallId,
            ToolAuthorizationDecision.Denied,
            Grant: null,
            reason);

    private static ApprovalResponse DeniedApproval(
        ToolApprovalConsentContext context,
        string reason) =>
        new(
            context.Request.CorrelationId,
            context.Request.ApprovalRequestId,
            ToolAuthorizationDecision.Denied,
            Grant: null,
            reason);
}
