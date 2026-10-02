using System.Runtime.CompilerServices;

using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.MConsole.Tests.TestSupport;

internal sealed class FakeRuntimeClient : IRuntimeClient
{
    public RuntimeInstanceBinding InstanceBinding { get; set; } = new(
        "host",
        "runtime",
        Environment.CurrentDirectory,
        Environment.ProcessId,
        "pipe",
        "pipe.events",
        false);

    public RuntimeClientState State { get; set; } = RuntimeClientState.Connected;

    public RuntimeCapabilities? NegotiatedCapabilities { get; set; }

    public Task Completion { get; set; } = Task.CompletedTask;

    public IReadOnlyList<RuntimeEventEnvelope> Events { get; set; } = [];

    public List<string> Operations { get; } = [];

    public RunQueryResult QueryResult { get; set; } = new(
        "run",
        "session",
        Madorin.AI.Runtime.Entities.RunStatus.Running,
        null);

    public bool CancelAccepted { get; set; } = true;

    public ToolCatalogReplaceRequest? LastCatalogReplace { get; private set; }

    public ToolCatalogPatchRequest? LastCatalogPatch { get; private set; }

    public GrantRevokeRequest? LastGrantRevoke { get; private set; }

    public CredentialsUpdateParameters? LastCredentialsUpdate { get; private set; }

    public IReadOnlyList<HostProviderConfig>? LastProviderUpsert { get; private set; }

    public SessionResumeResult? ResumeResult { get; set; }

    public SessionRehydrateResult? RehydrateResult { get; set; }

    public SessionListResult SessionListResult { get; set; } = new([], null);

    public RunListResult RunListResult { get; set; } = new([]);

    public NewSessionRunRequest? LastNewRequest { get; private set; }

    public ExistingSessionRunRequest? LastExistingRequest { get; private set; }

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask<string> StartNewSessionRunAsync(
        NewSessionRunRequest request,
        CancellationToken cancellationToken = default)
    {
        LastNewRequest = request;
        return ValueTask.FromResult("run-new");
    }

    public ValueTask<string> StartExistingSessionRunAsync(
        ExistingSessionRunRequest request,
        CancellationToken cancellationToken = default)
    {
        LastExistingRequest = request;
        return ValueTask.FromResult("run-existing");
    }

    public async IAsyncEnumerable<RuntimeEventEnvelope> ReadEventsAsync(
        long eventReplayCursor,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in Events.Where(item => item.Gsn > eventReplayCursor))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }

    public ValueTask AcknowledgeEventsAsync(
        long lastConfirmedGsn,
        CancellationToken cancellationToken = default)
    {
        Operations.Add($"ack:{lastConfirmedGsn}");
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> CancelRunAsync(
        string runId,
        CancellationToken cancellationToken = default) =>
        CancelRunAsync(runId, null, cancellationToken);

    public ValueTask<bool> CancelRunAsync(
        string runId,
        string? reason,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CancelAccepted);

    public ValueTask<RunQueryResult> QueryRunAsync(
        string runId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(QueryResult);

    public ValueTask<RuntimeStatusResult> GetRuntimeStatusAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new RuntimeStatusResult(
            "runtime",
            "1.0.0",
            "1.1",
            Environment.CurrentDirectory,
            Environment.ProcessId,
            DateTimeOffset.UtcNow,
            0,
            1,
            1));

    public ValueTask<SkillCatalogSummary> GetSkillCatalogAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask<SkillCatalogSummary> RefreshSkillCatalogAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask<RunListResult> ListRunsAsync(
        RunListParameters parameters,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(RunListResult);

    public ValueTask<SessionGetResult> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask<SessionListResult> ListSessionsAsync(
        SessionListParameters parameters,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(SessionListResult);

    public ValueTask<SessionResumeResult> ResumeSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(
            ResumeResult ?? throw new NotSupportedException());

    public MeetingHitlResponse? LastMeetingHitlResponse { get; private set; }

    public ValueTask<bool> SubmitMeetingHitlResponseAsync(
        MeetingHitlResponse response,
        CancellationToken cancellationToken = default)
    {
        LastMeetingHitlResponse = response;
        return ValueTask.FromResult(true);
    }

    public ValueTask<WorkInputEnqueueResult> EnqueueWorkInputAsync(
        WorkInputEnqueueRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new WorkInputEnqueueResult(
            request.SessionId,
            request.RequestId,
            Accepted: true,
            Status: "Pending"));

    public ValueTask<MeetingInputEnqueueResult> EnqueueMeetingInputAsync(
        MeetingInputEnqueueRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new MeetingInputEnqueueResult(
            request.SessionId,
            request.RequestId,
            Accepted: true,
            Status: "Pending"));

    public ValueTask<MeetingPauseResult> PauseMeetingAsync(
        MeetingPauseRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new MeetingPauseResult(
            request.SessionId,
            request.RequestId,
            Accepted: true,
            Status: "PauseRequested"));

    public ValueTask<MeetingResumeResult> ResumeMeetingAsync(
        MeetingResumeRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new MeetingResumeResult(
            request.SessionId,
            request.RequestId,
            Accepted: true,
            Status: "ReadyForContinuation"));

    public ValueTask<SessionMessagesListResult> ListSessionMessagesAsync(
        SessionMessagesListParameters parameters,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask<SessionHistoryReadResult> ReadSessionHistoryAsync(
        SessionHistoryReadParameters parameters,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask<NextTurnSelection> UpdateSessionSelectionAsync(
        SessionSelectionUpdateParameters parameters,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask<SessionRehydrateResult> RehydrateSessionAsync(
        SessionRehydrateParameters parameters,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(
            RehydrateResult ?? throw new NotSupportedException());

    public ValueTask<ToolCatalogUpdateResponse> ReplaceToolCatalogAsync(
        ToolCatalogReplaceRequest request,
        CancellationToken cancellationToken = default)
    {
        LastCatalogReplace = request;
        return ValueTask.FromResult(new ToolCatalogUpdateResponse(
            request.CatalogVersion,
            request.CatalogVersion,
            request.Tools.Length));
    }

    public ValueTask<ToolCatalogUpdateResponse> PatchToolCatalogAsync(
        ToolCatalogPatchRequest request,
        CancellationToken cancellationToken = default)
    {
        LastCatalogPatch = request;
        return ValueTask.FromResult(new ToolCatalogUpdateResponse(
            request.NewVersion,
            request.NewVersion,
            request.Upserts.Length));
    }

    public ValueTask<GrantRevokeResponse> RevokeGrantAsync(
        GrantRevokeRequest request,
        CancellationToken cancellationToken = default)
    {
        LastGrantRevoke = request;
        return ValueTask.FromResult(new GrantRevokeResponse(request.GrantId, 1));
    }

    public ValueTask UpdateCredentialsAsync(
        CredentialsUpdateParameters parameters,
        CancellationToken cancellationToken = default)
    {
        LastCredentialsUpdate = parameters;
        return ValueTask.CompletedTask;
    }

    public ValueTask UpsertProvidersAsync(
        IReadOnlyList<HostProviderConfig> providers,
        CancellationToken cancellationToken = default)
    {
        LastProviderUpsert = providers;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
