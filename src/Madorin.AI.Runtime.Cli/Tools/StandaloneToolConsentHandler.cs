using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Services.Tools;

namespace Madorin.AI.Runtime.Cli.Tools;

internal sealed class StandaloneToolConsentHandler(
    ICliTerminal terminal,
    StandaloneToolGrantFactory grantFactory) : IToolConsentHandler, IDisposable
{
    private const int MaxInputAttempts = 3;

    private readonly StandaloneToolGrantFactory _grantFactory = grantFactory
        ?? throw new ArgumentNullException(nameof(grantFactory));
    private readonly SemaphoreSlim _promptGate = new(1, 1);
    private readonly ICliTerminal _terminal = terminal
        ?? throw new ArgumentNullException(nameof(terminal));

    public async Task<ToolPermissionResponse> RequestPermissionAsync(
        ToolPermissionConsentContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.ScopeResolution.IsSuccess)
        {
            return DeniedPermission(context, "The requested scope could not be safely normalized.");
        }

        await _promptGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            WriteToolSummary("Tool permission", context);
            var canGrantForRun = StandaloneToolConsentPolicy.CanGrantForRun(context);
            _terminal.WriteLine("1  Allow once", CliTextStyle.Parameter);
            _terminal.WriteLine(
                canGrantForRun
                    ? "2  Allow for this Run"
                    : "2  Allow for this Run (unavailable)",
                canGrantForRun ? CliTextStyle.Parameter : CliTextStyle.Muted);
            _terminal.WriteLine("3  Deny", CliTextStyle.Parameter);

            var choice = await ReadPermissionChoiceAsync(canGrantForRun, ct)
                .ConfigureAwait(false);
            if (choice is null)
            {
                return DeniedPermission(context, "The user denied the tool permission.");
            }

            var grant = _grantFactory.Create(context, choice.Value);
            return new ToolPermissionResponse(
                context.Request.CorrelationId ?? string.Empty,
                context.Request.CallId,
                ToolAuthorizationDecision.Granted,
                grant,
                choice is StandaloneToolGrantLifetime.Once
                    ? "Allowed once by the interactive user."
                    : "Allowed for this Run by the interactive user.");
        }
        finally
        {
            _promptGate.Release();
        }
    }

    public async Task<ApprovalResponse> RequestApprovalAsync(
        ToolApprovalConsentContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Invocation is not null && !context.ScopeResolution.IsSuccess)
        {
            return DeniedApproval(context, "The requested scope could not be safely normalized.");
        }

        await _promptGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (context.Invocation is not null && context.Descriptor is not null)
            {
                WriteToolSummary("Tool approval", context);
            }
            else
            {
                _terminal.WriteLine("Workflow approval", CliTextStyle.Warning);
                _terminal.WriteLine(
                    $"Request: {context.Request.ApprovalRequestId}",
                    CliTextStyle.Muted);
            }

            var approved = await ReadApprovalChoiceAsync(ct).ConfigureAwait(false);
            if (!approved)
            {
                return DeniedApproval(context, "The user denied the approval request.");
            }

            var grant = context.Invocation is null
                ? null
                : _grantFactory.CreateOnce(context);
            return new ApprovalResponse(
                context.Request.CorrelationId,
                context.Request.ApprovalRequestId,
                ToolAuthorizationDecision.Granted,
                grant,
                "Approved by the interactive user.");
        }
        finally
        {
            _promptGate.Release();
        }
    }

    public void Dispose() => _promptGate.Dispose();

    private async Task<StandaloneToolGrantLifetime?> ReadPermissionChoiceAsync(
        bool canGrantForRun,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxInputAttempts; attempt++)
        {
            _terminal.Write("Select [1/2/3]: ", CliTextStyle.Prompt);
            var input = await _terminal.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            switch (input.Trim())
            {
                case "1":
                    return StandaloneToolGrantLifetime.Once;
                case "2" when canGrantForRun:
                    return StandaloneToolGrantLifetime.Run;
                case "3":
                    return null;
                default:
                    _terminal.WriteLine("Enter 1, 2, or 3.", CliTextStyle.Error);
                    break;
            }
        }

        return null;
    }

    private async Task<bool> ReadApprovalChoiceAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxInputAttempts; attempt++)
        {
            _terminal.Write("Approve [Y/N]: ", CliTextStyle.Prompt);
            var input = await _terminal.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            if (string.Equals(input.Trim(), "Y", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(input.Trim(), "N", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _terminal.WriteLine("Enter Y or N.", CliTextStyle.Error);
        }

        return false;
    }

    private void WriteToolSummary(string heading, ToolPermissionConsentContext context)
    {
        var scope = context.ScopeResolution.Scope!;
        WriteToolSummary(
            heading,
            context.Request.ToolId,
            context.Request.AgentId,
            context.Request.CallId,
            context.Request.ArgumentsHash,
            scope.TargetSummary,
            scope.DetailSummary);
    }

    private void WriteToolSummary(string heading, ToolApprovalConsentContext context)
    {
        var scope = context.ScopeResolution.Scope!;
        WriteToolSummary(
            heading,
            context.Request.ToolId,
            context.Request.AgentId,
            context.Request.CallId,
            context.Request.ArgumentsHash,
            scope.TargetSummary,
            scope.DetailSummary);
    }

    private void WriteToolSummary(
        string heading,
        string toolId,
        string agentId,
        string callId,
        string argumentsHash,
        string targetSummary,
        string detailSummary)
    {
        _terminal.WriteLine(heading, CliTextStyle.Warning);
        _terminal.WriteLine($"Tool: {toolId}", CliTextStyle.Parameter);
        _terminal.WriteLine($"Target: {targetSummary}");
        _terminal.WriteLine($"Scope: {detailSummary}");
        _terminal.WriteLine($"Agent: {agentId}; Call: {callId}", CliTextStyle.Muted);
        _terminal.WriteLine($"Arguments SHA-256: {argumentsHash}", CliTextStyle.Muted);
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
