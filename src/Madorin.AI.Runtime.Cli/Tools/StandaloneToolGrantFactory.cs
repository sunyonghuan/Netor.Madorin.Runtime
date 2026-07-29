using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;

namespace Madorin.AI.Runtime.Cli.Tools;

internal enum StandaloneToolGrantLifetime
{
    Once,
    Run
}

internal sealed class StandaloneToolGrantFactory(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromHours(4);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public ToolGrant Create(
        ToolPermissionConsentContext context,
        StandaloneToolGrantLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Create(
            context.Invocation,
            context.Descriptor,
            context.ScopeResolution,
            context.WorkspaceRoot,
            lifetime);
    }

    public ToolGrant CreateOnce(ToolApprovalConsentContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Invocation is null || context.Descriptor is null)
        {
            throw new ArgumentException(
                "A workflow approval cannot create a tool Grant.",
                nameof(context));
        }

        return Create(
            context.Invocation,
            context.Descriptor,
            context.ScopeResolution,
            context.WorkspaceRoot,
            StandaloneToolGrantLifetime.Once);
    }

    private ToolGrant Create(
        ToolInvocation invocation,
        ToolDescriptor descriptor,
        ToolConsentScopeResolution resolution,
        string workspaceRoot,
        StandaloneToolGrantLifetime lifetime)
    {
        if (!resolution.IsSuccess || resolution.Scope is not { } scope)
        {
            throw new InvalidOperationException(
                "A safely normalized scope is required to create a standalone Grant.");
        }

        if (lifetime is StandaloneToolGrantLifetime.Run && !scope.CanGrantForRun)
        {
            throw new InvalidOperationException(
                "This tool scope cannot be granted for the full Run.");
        }

        var grantId = $"standalone-{Guid.NewGuid():N}";
        var normalizedWorkspace = Path.GetFullPath(workspaceRoot);
        var isRunGrant = lifetime is StandaloneToolGrantLifetime.Run;
        var expandWorkspaceRoots = isRunGrant
            && (descriptor.ToolId.StartsWith("mcli_fs_", StringComparison.Ordinal)
                || string.Equals(
                    descriptor.ToolId,
                    BuiltinToolRegistry.ProcessRunToolId,
                    StringComparison.Ordinal));
        var allowedReadRoots = expandWorkspaceRoots && scope.AllowedReadRoots.Length > 0
            ? [normalizedWorkspace]
            : scope.AllowedReadRoots;
        var allowedWriteRoots = expandWorkspaceRoots && scope.AllowedWriteRoots.Length > 0
            ? [normalizedWorkspace]
            : scope.AllowedWriteRoots;
        var allowOverwrite = scope.AllowOverwrite
            || isRunGrant && string.Equals(
                descriptor.ToolId,
                BuiltinToolRegistry.FileWriteToolId,
                StringComparison.Ordinal);
        string[]? scriptKeys = null;
        string? skillCatalogVersion = null;
        if (string.Equals(descriptor.ToolId, SkillScriptAuthorization.ToolId, StringComparison.Ordinal)
            && SkillScriptAuthorization.TryReadScope(
                invocation.ArgumentsJson,
                out var skillName,
                out var scriptHash,
                out var interpreter,
                out skillCatalogVersion))
        {
            scriptKeys = [SkillScriptAuthorization.CreateKey(skillName, scriptHash, interpreter)];
        }

        return new ToolGrant(
            grantId,
            invocation.RunId,
            normalizedWorkspace,
            allowedReadRoots,
            allowedWriteRoots,
            allowOverwrite,
            scope.AllowMove,
            scope.AllowDelete,
            scope.AllowedExecutables,
            scope.AllowPowerShell,
            scope.NetworkPolicy,
            _timeProvider.GetUtcNow().Add(GrantLifetime),
            AllowDelegation: false,
            DelegatedAgentIds: [],
            AllowedToolIds: [descriptor.ToolId],
            AllowedCallIds: lifetime is StandaloneToolGrantLifetime.Once
                ? [invocation.CallId]
                : null,
            MaximumRisk: descriptor.Risk,
            AllowedEnvironmentVariables: scope.AllowedEnvironmentVariables,
            AgentId: invocation.AgentId,
            ParentGrantId: null,
            RootGrantId: grantId,
            DelegationChain: [],
            AllowedSkillScriptKeys: scriptKeys,
            SkillCatalogVersion: skillCatalogVersion);
    }
}
