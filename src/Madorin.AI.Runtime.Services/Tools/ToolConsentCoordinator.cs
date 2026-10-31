using System.Security.Cryptography;
using System.Text;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Services.Tools;

/// <summary>Persists consent and resumes a pending tool call with a narrowed grant.</summary>
public sealed class ToolConsentCoordinator
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(2);

    private readonly IToolAuthorizationService _authorizationService;
    private readonly ToolCatalogSnapshot _fallbackCatalogSnapshot;
    private readonly Func<ToolCatalogSnapshot>? _liveCatalogProvider;
    private readonly IToolConsentHandler? _consentHandler;
    private readonly IToolConsentScopeResolver? _scopeResolver;
    private readonly IToolStateStore? _toolStateStore;
    private readonly ToolGateway _toolGateway;
    private readonly string _workspaceRoot;

    /// <summary>Creates a coordinator compatible with existing Reverse RPC delegates.</summary>
    public ToolConsentCoordinator(
        ToolGateway toolGateway,
        IToolAuthorizationService authorizationService,
        ToolCatalogSnapshot catalogSnapshot,
        Func<ToolPermissionRequest, CancellationToken, Task<ToolPermissionResponse>>?
            toolPermissionHandler = null,
        Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse>>?
            approvalHandler = null,
        IToolStateStore? toolStateStore = null,
        Func<ToolCatalogSnapshot>? liveCatalogProvider = null)
        : this(
            toolGateway,
            authorizationService,
            catalogSnapshot,
            toolPermissionHandler is null && approvalHandler is null
                ? null
                : new ReverseRpcToolConsentHandler(toolPermissionHandler, approvalHandler),
            scopeResolver: null,
            workspaceRoot: string.Empty,
            toolStateStore,
            liveCatalogProvider)
    {
    }

    /// <summary>Creates a coordinator with one explicit Runtime consent authority.</summary>
    public ToolConsentCoordinator(
        ToolGateway toolGateway,
        IToolAuthorizationService authorizationService,
        ToolCatalogSnapshot catalogSnapshot,
        IToolConsentHandler? consentHandler,
        IToolConsentScopeResolver? scopeResolver,
        string workspaceRoot,
        IToolStateStore? toolStateStore = null,
        Func<ToolCatalogSnapshot>? liveCatalogProvider = null)
    {
        _toolGateway = toolGateway ?? throw new ArgumentNullException(nameof(toolGateway));
        _authorizationService = authorizationService
            ?? throw new ArgumentNullException(nameof(authorizationService));
        _fallbackCatalogSnapshot = catalogSnapshot
            ?? throw new ArgumentNullException(nameof(catalogSnapshot));
        _liveCatalogProvider = liveCatalogProvider;
        _consentHandler = consentHandler;
        _scopeResolver = scopeResolver;
        _workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot)
            ? string.Empty
            : Path.GetFullPath(workspaceRoot);
        _toolStateStore = toolStateStore;
    }

    /// <summary>
    /// Live catalog after search/browse inject when provided; otherwise the construction-time snapshot.
    /// </summary>
    private ToolCatalogSnapshot CaptureCatalogSnapshot() =>
        _liveCatalogProvider?.Invoke() ?? _fallbackCatalogSnapshot;

    public bool CanRequestApproval => _consentHandler?.CanRequestApproval == true;

    /// <summary>Uses the authenticated host approval channel for a non-tool workflow decision.</summary>
    public Task<ApprovalResponse> RequestWorkflowApprovalAsync(
        ApprovalRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_consentHandler?.CanRequestApproval != true)
        {
            return Task.FromException<ApprovalResponse>(
                new InvalidOperationException("The consent authority does not support workflow approval requests."));
        }

        return _consentHandler.RequestApprovalAsync(
            new ToolApprovalConsentContext(
                request,
                Invocation: null,
                Descriptor: null,
                ToolConsentScopeResolution.Failure("Workflow approval has no tool scope."),
                _workspaceRoot),
            ct);
    }

    public async Task<ToolGatewayResult> ResolveAndExecuteAsync(
        ToolInvocation invocation,
        ToolGatewayResult pendingResult,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(pendingResult);
        var current = pendingResult;
        var handledPermission = false;
        var handledApproval = false;
        while (current.Kind is ToolGatewayResultKind.NeedsPermission
            or ToolGatewayResultKind.NeedsApproval)
        {
            if (current.Kind is ToolGatewayResultKind.NeedsPermission)
            {
                if (handledPermission)
                {
                    return await FailAndCompleteAsync(
                        invocation,
                        ToolIntentStatus.Failed,
                        RuntimeErrorCodes.ToolPermissionRequired,
                        "The permission Grant did not make progress.",
                        ct).ConfigureAwait(false);
                }

                handledPermission = true;
            }
            else
            {
                if (handledApproval)
                {
                    return await FailAndCompleteAsync(
                        invocation,
                        ToolIntentStatus.Failed,
                        RuntimeErrorCodes.ToolApprovalRequired,
                        "The approval Grant did not make progress.",
                        ct).ConfigureAwait(false);
                }

                handledApproval = true;
            }

            current = await ResolveSingleAsync(invocation, current, ct).ConfigureAwait(false);
        }

        return current;
    }

    private async Task<ToolGatewayResult> ResolveSingleAsync(
        ToolInvocation invocation,
        ToolGatewayResult pendingResult,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(pendingResult);
        if (pendingResult.Kind is not (ToolGatewayResultKind.NeedsApproval
            or ToolGatewayResultKind.NeedsPermission))
        {
            throw new ArgumentException(
                "Tool consent can only resolve a pending approval or permission result.",
                nameof(pendingResult));
        }

        var catalogSnapshot = CaptureCatalogSnapshot();
        if (!catalogSnapshot.TryGetTool(invocation.ToolId, out var descriptor)
            || descriptor is null)
        {
            return await FailAndCompleteAsync(
                invocation,
                ToolIntentStatus.Failed,
                RuntimeErrorCodes.ToolExecutorUnavailable,
                "Tool consent cannot be resolved because the tool is absent from the live catalog snapshot.",
                ct).ConfigureAwait(false);
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var scopeResolution = _scopeResolver?.Resolve(invocation, descriptor, _workspaceRoot)
            ?? ToolConsentScopeResolution.Failure(
                "The consent authority does not require an in-process tool scope.");
        ToolApprovalState? consentState = null;
        ToolGrant? grant;
        if (pendingResult.Kind is ToolGatewayResultKind.NeedsApproval)
        {
            var approvalRequestId = CreateApprovalRequestId(invocation);
            var argumentsHash = ComputeHash(invocation.ArgumentsJson);
            var approvalReason = pendingResult.Error?.Message
                ?? "The tool requires explicit approval.";
            var approvalState = new ToolApprovalState(
                approvalRequestId,
                invocation.CallId,
                invocation.RunId,
                invocation.AgentId,
                invocation.ToolId,
                argumentsHash,
                "pending",
                GrantId: null,
                approvalReason,
                DateTimeOffset.UtcNow,
                DecidedAt: null,
                RequestKind: "approval");
            consentState = approvalState;
            if (!await SaveConsentAsync(approvalState, ct).ConfigureAwait(false))
            {
                return await FailAndCompleteAsync(
                    invocation,
                    ToolIntentStatus.Failed,
                    RuntimeErrorCodes.ToolApprovalRequired,
                    "The approval request could not be persisted.",
                    ct).ConfigureAwait(false);
            }

            if (_consentHandler?.CanRequestApproval != true)
            {
                await SaveFinalDecisionAsync(
                    approvalState,
                    "unavailable",
                    grantId: null,
                    "No consent authority is available for approval.",
                    ct).ConfigureAwait(false);
                return await FailAndCompleteAsync(
                    invocation,
                    ToolIntentStatus.Failed,
                    RuntimeErrorCodes.ToolApprovalRequired,
                    "This Runtime has no interactive tool approval channel.",
                    ct).ConfigureAwait(false);
            }

            var request = new ApprovalRequest(
                correlationId,
                approvalRequestId,
                invocation.CallId,
                invocation.RunId,
                invocation.AgentId,
                invocation.ParentAgentId,
                invocation.ToolId,
                argumentsHash,
                descriptor.Risk,
                scopeResolution.Scope?.TargetSummary
                    ?? $"{descriptor.ExecutionTarget.ToString().ToLowerInvariant()}:{descriptor.ToolId}",
                approvalReason,
                invocation.SessionId,
                invocation.InvocationId,
                invocation.ParentInvocationId,
                invocation.WorkStepId,
                invocation.PlanVersion);
            ApprovalResponse response;
            try
            {
                response = await _consentHandler.RequestApprovalAsync(
                    new ToolApprovalConsentContext(
                        request,
                        invocation,
                        descriptor,
                        scopeResolution,
                        _workspaceRoot),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await CancelConsentAsync(approvalState, invocation).ConfigureAwait(false);
                throw;
            }
            catch (Exception)
            {
                await SaveFinalDecisionAsync(
                    approvalState,
                    "handler-error",
                    grantId: null,
                    "The consent handler failed.",
                    ct).ConfigureAwait(false);
                return await FailAndCompleteAsync(
                    invocation,
                    ToolIntentStatus.Failed,
                    RuntimeErrorCodes.ToolApprovalRequired,
                    "The tool approval handler failed.",
                    ct).ConfigureAwait(false);
            }
            if (!string.Equals(response.CorrelationId, correlationId, StringComparison.Ordinal)
                || !string.Equals(
                    response.ApprovalRequestId,
                    approvalRequestId,
                    StringComparison.Ordinal))
            {
                await SaveFinalDecisionAsync(
                    approvalState,
                    "invalid-response",
                    response.Grant?.GrantId,
                    "The approval response did not match the request.",
                    ct).ConfigureAwait(false);
                return await FailAndCompleteAsync(
                    invocation,
                    ToolIntentStatus.Failed,
                    RuntimeErrorCodes.ToolApprovalRequired,
                    "The consent authority returned an approval response for another request.",
                    ct).ConfigureAwait(false);
            }

            if (!await SaveFinalDecisionAsync(
                    approvalState,
                    FormatDecision(response.Decision),
                    response.Grant?.GrantId,
                    response.Reason,
                    ct).ConfigureAwait(false))
            {
                return await FailAndCompleteAsync(
                    invocation,
                    ToolIntentStatus.Failed,
                    RuntimeErrorCodes.ToolApprovalRequired,
                    "The approval decision could not be persisted.",
                    ct).ConfigureAwait(false);
            }

            if (response.Decision is not ToolAuthorizationDecision.Granted)
            {
                if (response.Decision is ToolAuthorizationDecision.Cancelled)
                {
                    await SaveFinalDecisionAsync(
                        approvalState,
                        "cancelled",
                        response.Grant?.GrantId,
                        response.Reason,
                        ct).ConfigureAwait(false);
                    return await FailAndCompleteAsync(
                        invocation,
                        ToolIntentStatus.Cancelled,
                        RuntimeErrorCodes.ToolExecutionCancelled,
                        response.Reason ?? "The tool call was cancelled.",
                        ct).ConfigureAwait(false);
                }

                var decision = response.Decision is ToolAuthorizationDecision.Denied
                    ? "denied"
                    : "invalid-response";
                if (decision is "invalid-response")
                {
                    await SaveFinalDecisionAsync(
                        approvalState,
                        decision,
                        response.Grant?.GrantId,
                        response.Reason,
                        ct).ConfigureAwait(false);
                }

                return await FailAndCompleteAsync(
                    invocation,
                    ToolIntentStatus.Failed,
                    RuntimeErrorCodes.ToolApprovalRequired,
                    response.Reason ?? "The tool call was not approved.",
                    ct).ConfigureAwait(false);
            }

            // 人审只决定 Granted/Denied；可执行维度以 Runtime 解析的 scope 为准（Host Grant 常缺 AllowedExecutables）。
            grant = FinalizeGrantedGrant(
                response.Grant,
                invocation,
                descriptor,
                scopeResolution,
                approvalRequestId,
                pinToCall: false);
        }
        else
        {
            var permissionRequestId = CreateConsentRequestId("permission", invocation);
            var argumentsHash = ComputeHash(invocation.ArgumentsJson);
            var permissionReason = pendingResult.Error?.Message ?? "The tool requires a Grant.";
            var permissionState = new ToolApprovalState(
                permissionRequestId,
                invocation.CallId,
                invocation.RunId,
                invocation.AgentId,
                invocation.ToolId,
                argumentsHash,
                "pending",
                GrantId: null,
                permissionReason,
                DateTimeOffset.UtcNow,
                DecidedAt: null,
                RequestKind: "permission");
            consentState = permissionState;
            if (!await SaveConsentAsync(permissionState, ct).ConfigureAwait(false))
            {
                return await FailAndCompleteAsync(
                    invocation,
                    ToolIntentStatus.Failed,
                    RuntimeErrorCodes.ToolPermissionRequired,
                    "The permission request could not be persisted.",
                    ct).ConfigureAwait(false);
            }

            if (_consentHandler?.CanRequestPermission != true)
            {
                await SaveFinalDecisionAsync(
                    permissionState,
                    "unavailable",
                    grantId: null,
                    "No consent authority is available for permission.",
                    ct).ConfigureAwait(false);
                return await FailAndCompleteAsync(
                    invocation,
                    ToolIntentStatus.Failed,
                    RuntimeErrorCodes.ToolPermissionRequired,
                    "This Runtime has no interactive tool permission channel.",
                    ct).ConfigureAwait(false);
            }

            var request = new ToolPermissionRequest(
                permissionRequestId,
                invocation.AgentId,
                invocation.ParentAgentId,
                invocation.ToolId,
                invocation.CallId,
                argumentsHash,
                descriptor.Risk.ToString(),
                scopeResolution.Scope?.CanGrantForRun == true ? "call-or-run" : "call",
                permissionReason,
                correlationId,
                invocation.RunId,
                catalogSnapshot.EffectiveVersion,
                descriptor.Capabilities?.ToArray(),
                invocation.SessionId,
                invocation.InvocationId,
                invocation.ParentInvocationId,
                invocation.WorkStepId,
                invocation.PlanVersion);
            ToolPermissionResponse response;
            try
            {
                response = await _consentHandler.RequestPermissionAsync(
                    new ToolPermissionConsentContext(
                        request,
                        invocation,
                        descriptor,
                        scopeResolution,
                        _workspaceRoot),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await CancelConsentAsync(permissionState, invocation).ConfigureAwait(false);
                throw;
            }
            catch (Exception)
            {
                await SaveFinalDecisionAsync(
                    permissionState,
                    "handler-error",
                    grantId: null,
                    "The consent handler failed.",
                    ct).ConfigureAwait(false);
                return await FailAndCompleteAsync(
                    invocation,
                    ToolIntentStatus.Failed,
                    RuntimeErrorCodes.ToolPermissionRequired,
                    "The tool permission handler failed.",
                    ct).ConfigureAwait(false);
            }
            if (!string.Equals(response.CorrelationId, correlationId, StringComparison.Ordinal)
                || !string.Equals(response.CallId, invocation.CallId, StringComparison.Ordinal))
            {
                await SaveFinalDecisionAsync(
                    permissionState,
                    "invalid-response",
                    response.Grant?.GrantId,
                    "The permission response did not match the request.",
                    ct).ConfigureAwait(false);
                return await FailAndCompleteAsync(
                    invocation,
                    ToolIntentStatus.Failed,
                    RuntimeErrorCodes.ToolPermissionRequired,
                    "The consent authority returned a permission response for another tool call.",
                    ct).ConfigureAwait(false);
            }

            if (response.Decision is not ToolAuthorizationDecision.Granted)
            {
                if (response.Decision is ToolAuthorizationDecision.Cancelled)
                {
                    await SaveFinalDecisionAsync(
                        permissionState,
                        "cancelled",
                        response.Grant?.GrantId,
                        response.Reason,
                        ct).ConfigureAwait(false);
                    return await FailAndCompleteAsync(
                        invocation,
                        ToolIntentStatus.Cancelled,
                        RuntimeErrorCodes.ToolExecutionCancelled,
                        response.Reason ?? "The tool permission was cancelled.",
                        ct).ConfigureAwait(false);
                }

                var decision = response.Decision is ToolAuthorizationDecision.Denied
                    ? "denied"
                    : "invalid-response";
                await SaveFinalDecisionAsync(
                    permissionState,
                    decision,
                    response.Grant?.GrantId,
                    response.Reason,
                    ct).ConfigureAwait(false);
                return await FailAndCompleteAsync(
                    invocation,
                    ToolIntentStatus.Failed,
                    RuntimeErrorCodes.ToolPermissionRequired,
                    response.Reason ?? "The tool permission was denied.",
                    ct).ConfigureAwait(false);
            }

            // 人审通过后：有 scope 则按本次调用最小权限造 Grant（对齐 Standalone）；否则沿用宿主 Grant。
            grant = FinalizeGrantedGrant(
                response.Grant,
                invocation,
                descriptor,
                scopeResolution,
                permissionRequestId,
                pinToCall: scopeResolution.Scope?.CanGrantForRun != true);
            if (!await SaveFinalDecisionAsync(
                    permissionState,
                    "granted",
                    grant?.GrantId,
                    response.Reason,
                    ct).ConfigureAwait(false))
            {
                return await FailAndCompleteAsync(
                    invocation,
                    ToolIntentStatus.Failed,
                    RuntimeErrorCodes.ToolPermissionRequired,
                    "The permission decision could not be persisted.",
                    ct).ConfigureAwait(false);
            }
        }

        if (grant is null)
        {
            await SaveFinalDecisionAsync(
                consentState!,
                "invalid-grant",
                grantId: null,
                "A granted decision did not include a Grant.",
                ct).ConfigureAwait(false);
            var errorCode = pendingResult.Kind is ToolGatewayResultKind.NeedsApproval
                ? RuntimeErrorCodes.ToolApprovalRequired
                : RuntimeErrorCodes.ToolPermissionRequired;
            return await FailAndCompleteAsync(
                invocation,
                ToolIntentStatus.Failed,
                errorCode,
                "A granted tool decision must include a narrowed Grant.",
                ct).ConfigureAwait(false);
        }

        try
        {
            await _authorizationService.SaveGrantAsync(
                grant,
                invocation.AgentId,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CancelConsentAsync(consentState!, invocation).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await SaveFinalDecisionAsync(
                consentState!,
                "invalid-grant",
                grant.GrantId,
                "The consent authority returned an invalid Grant.",
                ct).ConfigureAwait(false);
            return await FailAndCompleteAsync(
                invocation,
                ToolIntentStatus.Failed,
                RuntimeErrorCodes.ToolPermissionRequired,
                "The consent authority returned an invalid Grant.",
                ct).ConfigureAwait(false);
        }

        return await _toolGateway.ExecuteAsync(
            invocation,
            permission: null,
            CaptureCatalogSnapshot(),
            ct).ConfigureAwait(false);
    }

    public static string CreateConsentRequestId(
        ToolGatewayResultKind pendingKind,
        ToolInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return pendingKind switch
        {
            ToolGatewayResultKind.NeedsApproval => CreateConsentRequestId("approval", invocation),
            ToolGatewayResultKind.NeedsPermission => CreateConsentRequestId("permission", invocation),
            _ => throw new ArgumentOutOfRangeException(
                nameof(pendingKind),
                pendingKind,
                "Only pending approval or permission results have consent request ids.")
        };
    }

    private static string CreateApprovalRequestId(ToolInvocation invocation)
    {
        return CreateConsentRequestId("approval", invocation);
    }

    private static string CreateConsentRequestId(string prefix, ToolInvocation invocation)
    {
        var identity = $"{invocation.RunId}\n{invocation.CallId}";
        return $"{prefix}-{Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)))}";
    }

    /// <summary>
    /// 宿主人审通过后的 Grant 定稿：
    /// 保留宿主 Grant 身份/过期/风险上限；用 Runtime scope 补全空的可执行文件、路径、网络等维度。
    /// 无宿主 Grant 且有 scope 时直接按 scope 新建（Standalone 同源）。
    /// </summary>
    private ToolGrant? FinalizeGrantedGrant(
        ToolGrant? hostGrant,
        ToolInvocation invocation,
        ToolDescriptor descriptor,
        ToolConsentScopeResolution scopeResolution,
        string approvalRequestId,
        bool pinToCall)
    {
        pinToCall |= invocation.ParentAgentId is not null;
        if (hostGrant is null)
        {
            return scopeResolution.IsSuccess && scopeResolution.Scope is { } scopeOnly
                ? CreateGrantFromScope(
                    invocation,
                    descriptor,
                    scopeOnly,
                    approvalRequestId,
                    pinToCall)
                : null;
        }

        if (pinToCall && hostGrant.AllowedCallIds is { Length: > 0 }
            && !hostGrant.AllowedCallIds.Contains(invocation.CallId, StringComparer.Ordinal))
        {
            return null;
        }

        var withApproval = hostGrant with
        {
            ApprovalRequestId = approvalRequestId,
            AgentId = hostGrant.AgentId ?? invocation.AgentId,
            AllowedCallIds = pinToCall ? [invocation.CallId] : hostGrant.AllowedCallIds
        };
        if (!scopeResolution.IsSuccess || scopeResolution.Scope is not { } scope)
        {
            return withApproval;
        }

        return EnrichHostGrantWithScope(
            withApproval,
            invocation,
            descriptor,
            scope,
            pinToCall);
    }

    private static ToolGrant EnrichHostGrantWithScope(
        ToolGrant hostGrant,
        ToolInvocation invocation,
        ToolDescriptor descriptor,
        ToolRequestedScope scope,
        bool pinToCall)
    {
        string[]? scriptKeys = hostGrant.AllowedSkillScriptKeys;
        string? skillCatalogVersion = hostGrant.SkillCatalogVersion;
        if (string.Equals(descriptor.ToolId, SkillScriptAuthorization.ToolId, StringComparison.Ordinal)
            && SkillScriptAuthorization.TryReadScope(
                invocation.ArgumentsJson,
                out var skillName,
                out var scriptHash,
                out var interpreter,
                out var catalogVersion))
        {
            scriptKeys = [SkillScriptAuthorization.CreateKey(skillName, scriptHash, interpreter)];
            skillCatalogVersion = catalogVersion;
        }

        // 宿主空白名单不能与 scope 做交集（空∩非空=空）；空则采用 scope。
        var allowedExecutables = hostGrant.AllowedExecutables.Length > 0
            ? hostGrant.AllowedExecutables
            : scope.AllowedExecutables;
        var allowedEnvironmentVariables =
            hostGrant.AllowedEnvironmentVariables is { Length: > 0 } hostEnv
                ? hostEnv
                : scope.AllowedEnvironmentVariables;
        var allowedReadRoots = scope.AllowedReadRoots.Length > 0
            ? scope.AllowedReadRoots
            : hostGrant.AllowedReadRoots;
        var allowedWriteRoots = scope.AllowedWriteRoots.Length > 0
            ? scope.AllowedWriteRoots
            : hostGrant.AllowedWriteRoots;
        ExpandRunFilesystemRoots(
            descriptor.ToolId,
            pinToCall,
            hostGrant.WorkspaceRoot,
            ref allowedReadRoots,
            ref allowedWriteRoots);
        var networkPolicy = scope.NetworkPolicy.DenyAll
            ? hostGrant.NetworkPolicy
            : scope.NetworkPolicy;

        return hostGrant with
        {
            AllowedReadRoots = allowedReadRoots,
            AllowedWriteRoots = allowedWriteRoots,
            AllowOverwrite = hostGrant.AllowOverwrite
                || scope.AllowOverwrite
                || (!pinToCall && IsRunFileWrite(descriptor.ToolId)),
            AllowMove = hostGrant.AllowMove || scope.AllowMove,
            AllowDelete = hostGrant.AllowDelete || scope.AllowDelete,
            AllowedExecutables = allowedExecutables,
            AllowPowerShell = hostGrant.AllowPowerShell || scope.AllowPowerShell,
            NetworkPolicy = networkPolicy,
            AllowedToolIds = hostGrant.AllowedToolIds is { Length: > 0 }
                ? hostGrant.AllowedToolIds
                : [descriptor.ToolId],
            AllowedCallIds = pinToCall
                ? [invocation.CallId]
                : hostGrant.AllowedCallIds,
            MaximumRisk = hostGrant.MaximumRisk ?? descriptor.Risk,
            AllowedEnvironmentVariables = allowedEnvironmentVariables.Length > 0
                ? allowedEnvironmentVariables
                : hostGrant.AllowedEnvironmentVariables,
            AgentId = hostGrant.AgentId ?? invocation.AgentId,
            AllowedSkillScriptKeys = scriptKeys,
            SkillCatalogVersion = skillCatalogVersion
        };
    }

    private ToolGrant CreateGrantFromScope(
        ToolInvocation invocation,
        ToolDescriptor descriptor,
        ToolRequestedScope scope,
        string approvalRequestId,
        bool pinToCall)
    {
        var grantId = $"scope-{Guid.NewGuid():N}";
        var workspaceRoot = string.IsNullOrWhiteSpace(_workspaceRoot)
            ? Path.GetFullPath(Directory.GetCurrentDirectory())
            : Path.GetFullPath(_workspaceRoot);

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

        var allowedReadRoots = scope.AllowedReadRoots.Length > 0
            ? scope.AllowedReadRoots
            : new[] { workspaceRoot };
        var allowedWriteRoots = scope.AllowedWriteRoots;
        ExpandRunFilesystemRoots(
            descriptor.ToolId,
            pinToCall,
            workspaceRoot,
            ref allowedReadRoots,
            ref allowedWriteRoots);
        return new ToolGrant(
            grantId,
            invocation.RunId,
            workspaceRoot,
            allowedReadRoots,
            allowedWriteRoots,
            scope.AllowOverwrite || (!pinToCall && IsRunFileWrite(descriptor.ToolId)),
            scope.AllowMove,
            scope.AllowDelete,
            scope.AllowedExecutables,
            scope.AllowPowerShell,
            scope.NetworkPolicy,
            DateTimeOffset.UtcNow.AddHours(4),
            AllowDelegation: false,
            DelegatedAgentIds: [],
            AllowedToolIds: [descriptor.ToolId],
            AllowedCallIds: pinToCall ? [invocation.CallId] : null,
            MaximumRisk: descriptor.Risk,
            AllowedEnvironmentVariables: scope.AllowedEnvironmentVariables,
            AgentId: invocation.AgentId,
            ParentGrantId: null,
            RootGrantId: grantId,
            DelegationChain: [],
            ApprovalRequestId: approvalRequestId,
            AllowedSkillScriptKeys: scriptKeys,
            SkillCatalogVersion: skillCatalogVersion);
    }

    /// <summary>
    /// 不钉 CallId 的文件系统 Grant 表示整次运行。读写根必须是工作区，
    /// 不能留在第一次调用的那个文件上。
    /// </summary>
    private static void ExpandRunFilesystemRoots(
        string toolId,
        bool pinToCall,
        string workspaceRoot,
        ref string[] allowedReadRoots,
        ref string[] allowedWriteRoots)
    {
        if (pinToCall || !ExpandsToWorkspace(toolId))
        {
            return;
        }

        var root = Path.GetFullPath(workspaceRoot);
        if (allowedReadRoots.Length > 0)
        {
            allowedReadRoots = [root];
        }

        if (allowedWriteRoots.Length > 0)
        {
            allowedWriteRoots = [root];
        }
    }

    private static bool ExpandsToWorkspace(string toolId) =>
        toolId.StartsWith("mcli_fs_", StringComparison.Ordinal)
        || string.Equals(toolId, "mcli_command", StringComparison.Ordinal);

    private static bool IsRunFileWrite(string toolId) =>
        string.Equals(toolId, "mcli_fs_write", StringComparison.Ordinal);

    private static string ComputeHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string FormatDecision(ToolAuthorizationDecision decision) => decision switch
    {
        ToolAuthorizationDecision.Granted => "granted",
        ToolAuthorizationDecision.Denied => "denied",
        ToolAuthorizationDecision.NeedsApproval => "needs-approval",
        ToolAuthorizationDecision.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(decision))
    };

    private async Task<ToolGatewayResult> FailAndCompleteAsync(
        ToolInvocation invocation,
        ToolIntentStatus status,
        string errorCode,
        string errorMessage,
        CancellationToken ct)
    {
        return await _toolGateway.CompletePendingConsentAsync(
            invocation,
            status,
            errorCode,
            errorMessage,
            ct).ConfigureAwait(false);
    }

    private async Task<bool> SaveConsentAsync(
        ToolApprovalState consent,
        CancellationToken ct)
    {
        if (_toolStateStore is null)
        {
            return true;
        }

        try
        {
            await _toolStateStore.SaveApprovalAsync(consent, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private Task<bool> SaveFinalDecisionAsync(
        ToolApprovalState consent,
        string decision,
        string? grantId,
        string? reason,
        CancellationToken ct)
    {
        return SaveConsentAsync(
            consent with
            {
                Decision = decision,
                GrantId = grantId,
                Reason = reason,
                DecidedAt = DateTimeOffset.UtcNow
            },
            ct);
    }

    private async Task CancelConsentAsync(
        ToolApprovalState consent,
        ToolInvocation invocation)
    {
        using var cleanupSource = new CancellationTokenSource(CleanupTimeout);
        var cleanupToken = cleanupSource.Token;
        await SaveFinalDecisionAsync(
            consent,
            "cancelled",
            grantId: null,
            "The consent request was cancelled.",
            cleanupToken).ConfigureAwait(false);
        try
        {
            await _toolGateway.CompletePendingConsentAsync(
                invocation,
                ToolIntentStatus.Cancelled,
                RuntimeErrorCodes.ToolExecutionCancelled,
                "The tool consent request was cancelled.",
                cleanupToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException
            or OperationCanceledException)
        {
            _ = ex;
        }
    }
}
