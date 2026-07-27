using System.Text.Json;

using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.MConsole.Callbacks;
using Madorin.AI.Runtime.MConsole.Configuration;

namespace Madorin.AI.Runtime.MConsole.Diagnostics;

public sealed class P2SmokeRunner(
    MConsoleOptions options,
    MConsolePaths paths,
    string workspace)
{
    private readonly MConsoleOptions _options = options;
    private readonly MConsolePaths _paths = paths;
    private readonly string _workspace = Path.GetFullPath(workspace);

    public async Task<P2SmokeReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var stateDirectory = Path.Combine(
            _paths.StateDirectory,
            "p2-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDirectory);
        try
        {
            using var store = new ToolCallResultStore(stateDirectory);
            var executor = new ProbeToolExecutor();
            var policy = new CallbackPolicy(
                CallbackPolicyMode.AllowListedTools,
                new HashSet<string>(StringComparer.Ordinal)
                {
                    "host.echo",
                    "host.wait",
                },
                AllowLowRiskApprovals: true);
            var coordinator = new CallbackCoordinator(
                policy,
                new ConservativeToolGrantFactory(_workspace),
                new ToolCallExecutionCoordinator(store),
                executor);
            var callbacks = coordinator.CreateCallbacks(
                "runtime-p2-smoke",
                _options.MaxCallbackConcurrency,
                TimeSpan.FromSeconds(_options.CallbackTimeoutSeconds));

            var permission = await callbacks.ToolPermissionRequestedAsync!(
                CreatePermissionRequest(),
                cancellationToken);
            var approval = await callbacks.ApprovalRequestedAsync!(
                CreateDangerousApprovalRequest(),
                cancellationToken);
            var echoRequest = CreateToolCallRequest("echo-call", "host.echo");
            var first = await callbacks.ToolCallRequestedAsync!(echoRequest, cancellationToken);
            var replayed = await callbacks.ToolCallRequestedAsync!(echoRequest, cancellationToken);
            var query = await callbacks.ToolResultQueriedAsync!(
                new ToolResultQueryRequest("query-correlation", echoRequest.CallId),
                cancellationToken);

            var waitRequest = CreateToolCallRequest("wait-call", "host.wait");
            var waiting = callbacks.ToolCallRequestedAsync!(waitRequest, cancellationToken).AsTask();
            await executor.WaitStarted.Task.WaitAsync(cancellationToken);
            var cancellationAccepted = await callbacks.ToolCallCancelledAsync!(
                new ToolCallCancelRequest("cancel-correlation", waitRequest.CallId, "smoke cleanup"),
                cancellationToken);
            var cancelled = await waiting;

            return new P2SmokeReport(
                permission.Decision,
                approval.Decision,
                executor.EchoExecutionCount,
                first.Status,
                replayed.Status,
                query.Status,
                cancellationAccepted,
                cancelled.Status,
                coordinator.History.Count);
        }
        finally
        {
            DeleteOwnedStateDirectory(stateDirectory);
        }
    }

    private static ToolPermissionRequest CreatePermissionRequest() => new(
        "approval-permission",
        "agent",
        null,
        "host.echo",
        "permission-call",
        "hash",
        "Low",
        "once",
        "P2 smoke",
        "permission-correlation",
        "run-p2");

    private static ApprovalRequest CreateDangerousApprovalRequest() => new(
        "approval-correlation",
        "approval-dangerous",
        "approval-call",
        "run-p2",
        "agent",
        null,
        "host.echo",
        "hash",
        ToolRiskLevel.Destructive,
        "P2 smoke target",
        "P2 smoke dangerous approval");

    private static ToolCallRequest CreateToolCallRequest(string callId, string toolId)
    {
        using var document = JsonDocument.Parse("{}");
        return new ToolCallRequest(
            "correlation-" + callId,
            callId,
            "run-p2",
            "session-p2",
            "invocation-" + callId,
            "agent",
            toolId,
            "p2-v1",
            document.RootElement.Clone(),
            5_000,
            "grant-p2");
    }

    private void DeleteOwnedStateDirectory(string path)
    {
        var stateRoot = Path.GetFullPath(_paths.StateDirectory)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(stateRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullPath).StartsWith("p2-smoke-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to remove an unowned P2 smoke directory.");
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private sealed class ProbeToolExecutor : IToolCallExecutor
    {
        private int _echoExecutionCount;

        public int EchoExecutionCount => Volatile.Read(ref _echoExecutionCount);

        public TaskCompletionSource WaitStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ToolCallResponse> ExecuteAsync(
            ToolCallRequest request,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(request.ToolId, "host.wait", StringComparison.Ordinal))
            {
                WaitStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            Interlocked.Increment(ref _echoExecutionCount);
            using var document = JsonDocument.Parse("{\"echo\":true}");
            return new ToolCallResponse(
                request.CorrelationId,
                request.CallId,
                ToolCallStatus.Succeeded,
                document.RootElement.Clone());
        }
    }
}
