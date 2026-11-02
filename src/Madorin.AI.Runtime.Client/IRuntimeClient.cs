using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Client;

public interface IRuntimeClient : IAsyncDisposable
{
    /// <summary>Gets the authenticated and initialized Runtime instance binding.</summary>
    public RuntimeInstanceBinding InstanceBinding { get; }

    /// <summary>
    /// Capabilities negotiated during initialize.
    /// Null when not connected (including faulted, reconnecting, closed, and never-connected).
    /// Hosts read <c>Skills.DiscoveryDirect</c> for skill progressive/direct gating.
    /// </summary>
    public RuntimeCapabilities? NegotiatedCapabilities { get; }

    /// <summary>Gets the current Client lifecycle state.</summary>
    public RuntimeClientState State { get; }

    /// <summary>Completes when the Client closes or observes its first fatal connection failure.</summary>
    public Task Completion { get; }

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 创建新会话并启动 Run。
    /// <see cref="NewSessionRunRequest.Language"/> 传入宿主界面语言；省略、null 或空白时由 Runtime 使用操作系统界面语言。
    /// </summary>
    public ValueTask<string> StartNewSessionRunAsync(
        NewSessionRunRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 在已有会话上启动下一轮 Run。
    /// <see cref="ExistingSessionRunRequest.Language"/> 的缺省规则与新会话相同。
    /// </summary>
    public ValueTask<string> StartExistingSessionRunAsync(
        ExistingSessionRunRequest request,
        CancellationToken cancellationToken = default);

    public IAsyncEnumerable<RuntimeEventEnvelope> ReadEventsAsync(
        long eventReplayCursor,
        CancellationToken cancellationToken = default);

    public ValueTask AcknowledgeEventsAsync(
        long lastConfirmedGsn,
        CancellationToken cancellationToken = default);

    public ValueTask<bool> CancelRunAsync(
        string runId,
        CancellationToken cancellationToken = default);

    public ValueTask<bool> CancelRunAsync(
        string runId,
        string? reason,
        CancellationToken cancellationToken = default);

    public ValueTask<RunQueryResult> QueryRunAsync(
        string runId,
        CancellationToken cancellationToken = default);

    public ValueTask<RuntimeStatusResult> GetRuntimeStatusAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 请求当前 Runtime 退出。服务端先取消活跃 Run、向 Provider 发送 shutdown，再结束进程。
    /// </summary>
    public ValueTask ShutdownAsync(CancellationToken cancellationToken = default);

    public ValueTask<SkillCatalogSummary> GetSkillCatalogAsync(
        CancellationToken cancellationToken = default);

    public ValueTask<SkillCatalogSummary> RefreshSkillCatalogAsync(
        CancellationToken cancellationToken = default);

    public ValueTask<RunListResult> ListRunsAsync(
        RunListParameters parameters,
        CancellationToken cancellationToken = default);

    public ValueTask<SessionGetResult> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    public ValueTask<SessionListResult> ListSessionsAsync(
        SessionListParameters parameters,
        CancellationToken cancellationToken = default);

    public ValueTask<SessionResumeResult> ResumeSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);


    public ValueTask<SessionMessagesListResult> ListSessionMessagesAsync(
        SessionMessagesListParameters parameters,
        CancellationToken cancellationToken = default);

    public ValueTask<SessionHistoryReadResult> ReadSessionHistoryAsync(
        SessionHistoryReadParameters parameters,
        CancellationToken cancellationToken = default);

    public ValueTask<SessionHistoryRoundsReadResult> ReadSessionHistoryRoundsAsync(
        SessionHistoryRoundsReadParameters parameters,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This Runtime client implementation does not support round-based history pages.");

    public ValueTask<SessionCompactionResultDto> CompactSessionAsync(
        SessionCompactionRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This Runtime client implementation does not support Session compaction.");

    public ValueTask<SessionContextStatusDto> GetSessionContextStatusAsync(
        SessionContextStatusRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Upgrade Runtime to query Session context metadata.");

    public ValueTask<NextTurnSelection> UpdateSessionSelectionAsync(
        SessionSelectionUpdateParameters parameters,
        CancellationToken cancellationToken = default);

    public ValueTask<SessionRehydrateResult> RehydrateSessionAsync(
        SessionRehydrateParameters parameters,
        CancellationToken cancellationToken = default);

    public ValueTask<ToolCatalogUpdateResponse> ReplaceToolCatalogAsync(
        ToolCatalogReplaceRequest request,
        CancellationToken cancellationToken = default);

    public ValueTask<ToolCatalogUpdateResponse> PatchToolCatalogAsync(
        ToolCatalogPatchRequest request,
        CancellationToken cancellationToken = default);

    public ValueTask<GrantRevokeResponse> RevokeGrantAsync(
        GrantRevokeRequest request,
        CancellationToken cancellationToken = default);

    public ValueTask UpdateCredentialsAsync(
        CredentialsUpdateParameters parameters,
        CancellationToken cancellationToken = default);

    public ValueTask UpsertProvidersAsync(
        IReadOnlyList<HostProviderConfig> providers,
        CancellationToken cancellationToken = default);
}
