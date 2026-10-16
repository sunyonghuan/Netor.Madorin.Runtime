# Host Integration

## Dependency Boundary

Reference `Madorin.AI.Runtime.Client` and required `Madorin.AI.Runtime.Contracts` types only. Do not reference Provider implementations, MEAI/MAF, SQLite, Persistence, Server, or the Named Pipe protocol. Do not inspect Runtime data files or internal dependency injection.

Each `RuntimeClient` is bound to one Runtime instance. Keep all event cursors, Run queries, cancellation requests, callbacks, and tool ownership inside that binding.

## Start Policy

| Policy | Behavior | Use it for |
| --- | --- | --- |
| `AttachExisting` | Connect only; fail when the Runtime is absent | A service manager or another process owns Runtime |
| `StartIfMissing` | Connect first, then start Runtime if needed | A single-instance desktop host |
| `AlwaysStart` | Start a new isolated Runtime | Tests, multi-workspace hosts, and strict isolation |

For `AttachExisting`, start Runtime operationally:

```powershell
madorin serve --workspace <workspace> --instance <runtimeInstanceId>
```

For `StartIfMissing` and `AlwaysStart`, configure the executable path and let the SDK start it. Never start `serve` manually for the same workspace at the same time. The SDK sends the handshake secret through redirected stdin; never put it in an argument, environment variable, log, or exception message.

## Create A Client

```csharp
using Madorin.AI.Runtime.Client;

var options = new RuntimeClientOptions(
    PipeName: "owned-by-create",
    HostInstanceId: Guid.NewGuid().ToString("N"))
{
    RuntimeExecutablePath = @"C:\Program Files\Madorin\madorin-cli.exe",
    WorkspaceDirectory = @"E:\Projects\demo",
    DataDirectory = @"E:\Projects\demo\.madorin",
    LogDirectory = @"E:\Projects\demo\.madorin\logs",
    RuntimeInstanceId = Guid.NewGuid().ToString("N"),
    PipePrefix = "madorin.ai.runtime",
    StartPolicy = RuntimeStartPolicy.AlwaysStart,
    OwnedProcessClosePolicy = OwnedProcessClosePolicy.Shutdown,
    StartupTimeout = TimeSpan.FromSeconds(15)
};

await using var client = await RuntimeClient.CreateAsync(options, cancellationToken);
var binding = client.InstanceBinding;
```

Access `InstanceBinding` only after `CreateAsync` completes. It identifies the host, Runtime, normalized workspace, process ID, endpoints, and whether the Client owns the Runtime process. Use `ExpectedRuntimeVersion`, `ExpectedRuntimeInstanceId`, and `ExpectedServerProcessId` when a stricter connection contract is required.

## Run And Event Processing

Use `StartNewSessionRunAsync` for a new Session and `ExistingSessionRunRequest` for a continued Session. Persist the event cursor per Runtime instance, not per `runId` alone.

```csharp
var runId = await client.StartNewSessionRunAsync(request, cancellationToken);

await foreach (var item in client.ReadEventsAsync(lastConfirmedGsn, cancellationToken))
{
    await durableProjection.ApplyAsync(item, cancellationToken);
    await client.AcknowledgeEventsAsync(item.Gsn, cancellationToken);
}
```

Apply and durably store the event before acknowledgement. On reconnect:

1. Reconnect to the original Runtime and validate PID, authentication, instance, protocol, and capabilities.
2. Resume `ReadEventsAsync` from the persisted `lastConfirmedGsn`.
3. Deduplicate at-least-once delivery by `(runtimeInstanceId, gsn)`.
4. Acknowledge only after successful business processing.
5. Query the Run terminal state; if Runtime restarted, rehydrate then resume the Session.

Do not report `Failed` merely because a local timeout occurred. A `RuntimeProcessExitedException` describes an owned Runtime process, not a Run business outcome.

## Callbacks And Tools

Configure host callbacks in `RuntimeClientOptions.HostCallbacks`:

```csharp
var callbacks = new RuntimeHostCallbacks
{
    ToolPermissionRequestedAsync = HandlePermissionAsync,
    ApprovalRequestedAsync = HandleApprovalAsync,
    ToolCallRequestedAsync = ExecuteToolAsync,
    ToolResultQueriedAsync = QueryToolResultAsync,
    ToolCallCancelledAsync = CancelToolAsync,
    MaxConcurrency = 4,
    CallbackTimeout = TimeSpan.FromSeconds(30)
};
```

Publish catalog changes with `ReplaceToolCatalogAsync` or `PatchToolCatalogAsync`. Persist side-effecting tool results by stable `callId`. When a callback times out or transport reconnects, implement `ToolResultQueriedAsync` and return the persisted result; do not run the operation again. Preserve Runtime, Run, Agent, Grant, and delegation ownership in every authorization and approval response.

## Session, Selection, And Credentials

Use these high-level APIs instead of protocol calls or file access:

- `ListSessionsAsync`, `GetSessionAsync`, `ListSessionMessagesAsync`
- `UpdateSessionSelectionAsync`, `ResumeSessionAsync`, `RehydrateSessionAsync`
- `QueryRunAsync`, `ListRunsAsync`, `CancelRunAsync`
- `ReplaceToolCatalogAsync`, `PatchToolCatalogAsync`, `RevokeGrantAsync`
- `UpdateCredentialsAsync`

Selection changes affect the next turn without changing the `sessionId` or clearing history. Resume a Session with `ExistingSessionRunRequest`. Update live credentials through the API or through `madorin ctl credential update`; never add credentials to prompt text or source code.

## Lifecycle And Upgrade

`DisposeAsync` releases only the current Client's background loops and connections. The closure policy applies only to a Runtime process owned by the Client:

- `KeepAlive` leaves it running.
- `Shutdown` requests graceful shutdown, then terminates only that owned process after its timeout.

For an upgrade:

1. Stop assigning new Runs and drain or intentionally cancel active Runs.
2. Preserve the old executable and make a data backup/recovery point.
3. Start the new isolated Runtime and validate PID, mutual authentication, protocol, capabilities, and expected version.
4. Rehydrate target Sessions and establish a new instance-specific event cursor.
5. Smoke-test a Run, callbacks, cancellation, and acknowledgement before switching traffic.
6. On failure, stop the new instance and restore the old version. Never allow two versions to write the same normalized workspace.

Before schema maintenance, run `madorin db check`, `madorin db backup`, and `madorin db migrate --dry-run`. Do not copy an active `state.db` as an online backup.
