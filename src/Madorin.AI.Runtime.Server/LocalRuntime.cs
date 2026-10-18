using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Modes.Expert;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server.ProviderWorkers;
using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Services.Skills;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Server;

public sealed class LocalRuntime : IAsyncDisposable
{
    private readonly string _dataDirectory;
    private readonly string _databaseConnectionString;
    private readonly BlobStagingStore _blobStore;
    private readonly RuntimeExecutionCore _executionCore;
    private readonly ToolCatalogStore _toolCatalogStore = new(
        new BuiltinToolRegistry(),
        new JsonSchemaToolValidator());
    private readonly Func<NextTurnSelection, IRuntimeProviderAdapter> _providerResolver;
    private readonly SessionCompactionService _compactionService;
    private readonly IToolConsentHandler? _toolConsentHandler;
    private readonly SessionMaintenanceAuditLog _maintenanceAudit;
    private readonly RuntimeDiagnosticLog _diagnosticLog;
    private readonly HashSet<IRuntimeProviderAdapter> _providers =
        new(ReferenceEqualityComparer.Instance);
    private readonly object _providerSync = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly ConcurrentDictionary<string, NextTurnSelection> _sessionSelections =
        new(StringComparer.Ordinal);
    private readonly WorkspaceWriteLock _workspaceLock;
    private readonly SkillCatalogService _skillCatalog;
    private readonly IProviderWorkerSupervisor _workerSupervisor;
    private int _disposed;

    private LocalRuntime(
        LocalRuntimeOptions options,
        WorkspaceWriteLock workspaceLock,
        string databaseConnectionString,
        AgentContextComposer agentContextComposer,
        MemoryFileService memoryFiles,
        SkillCatalogService skillCatalog,
        RuntimeDiagnosticLog diagnosticLog,
        IProviderWorkerSupervisor workerSupervisor)
    {
        _dataDirectory = options.DataDirectory;
        _databaseConnectionString = databaseConnectionString;
        _providerResolver = options.ProviderResolver;
        _compactionService = new SessionCompactionService(_dataDirectory, databaseConnectionString, selection => ResolveProvider(selection));
        _toolConsentHandler = options.ToolConsentHandler;
        _maintenanceAudit = new SessionMaintenanceAuditLog(options.DataDirectory);
        _diagnosticLog = diagnosticLog;
        _workspaceLock = workspaceLock;
        _skillCatalog = skillCatalog;
        _workerSupervisor = workerSupervisor;
        _blobStore = new BlobStagingStore(
            options.DataDirectory,
            options.RuntimeLimits,
            options.MaxGlobalBlobBytes);
        _executionCore = new RuntimeExecutionCore(
            options.DataDirectory,
            databaseConnectionString,
            options.RuntimeInstanceId,
            options.WorkspaceRoot,
            agentContextComposer,
            memoryFiles,
            options.RuntimeLimits,
            _blobStore);
        RuntimeInstanceId = options.RuntimeInstanceId;
        WorkspaceRoot = options.WorkspaceRoot;
    }

    public string RuntimeInstanceId { get; }

    public string WorkspaceRoot { get; }

    /// <summary>Gets a point-in-time status snapshot for the direct CLI host.</summary>
    public LocalRuntimeStatus GetStatus()
    {
        ThrowIfDisposed();
        return new LocalRuntimeStatus(
            RuntimeInstanceId,
            WorkspaceRoot,
            _runGate.CurrentCount == 0 ? 1 : 0,
            GC.GetTotalMemory(forceFullCollection: false),
            _toolCatalogStore.CaptureSnapshot().Tools.Count);
    }

    public static async Task<LocalRuntime> StartAsync(
        LocalRuntimeOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.LogDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RuntimeInstanceId);
        ArgumentNullException.ThrowIfNull(options.ProviderResolver);
        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        if (!Enum.IsDefined(options.MinimumLogLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        if (options.RuntimeLimits.MaxToolCallsPerRound is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxToolCallsPerRound must be a positive integer or null.");
        }

        if (options.RuntimeLimits.MaxToolRounds is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxToolRounds must be a positive integer or null.");
        }

        if (options.WorkspaceLockTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        var normalized = options with
        {
            WorkspaceRoot = Path.GetFullPath(options.WorkspaceRoot),
            DataDirectory = Path.GetFullPath(options.DataDirectory),
            LogDirectory = Path.GetFullPath(options.LogDirectory)
        };
        if (normalized.MemoryFiles is { } suppliedMemory
            && (suppliedMemory.ProjectPath is null
                || !PathsEqual(
                    suppliedMemory.ProjectPath,
                    MemoryFileService.ResolveProjectPath(normalized.WorkspaceRoot))))
        {
            throw new ArgumentException(
                "The supplied memory service must be bound to the Runtime workspace.",
                nameof(options));
        }

        Directory.CreateDirectory(normalized.WorkspaceRoot);
        var workspaceLock = await WorkspaceWriteLock.AcquireAsync(
                normalized.WorkspaceRoot,
                normalized.RuntimeInstanceId,
                normalized.WorkspaceLockTimeout,
                ct)
            .ConfigureAwait(false);
        SkillCatalogService? skillCatalog = null;
        RuntimeDiagnosticLog? diagnosticLog = null;
        try
        {
            diagnosticLog = RuntimeDiagnosticLog.Create(
                normalized.LogDirectory,
                normalized.RuntimeInstanceId,
                normalized.WorkspaceRoot,
                normalized.MinimumLogLevel,
                normalized.TimeProvider);
            await diagnosticLog.WriteAsync(
                    RuntimeDiagnosticLogLevel.Information,
                    "RuntimeStarting",
                    alwaysWrite: true,
                    ct: ct)
                .ConfigureAwait(false);
            await using var connection = await DataDirectoryInitializer.InitializeAsync(
                    normalized.DataDirectory,
                    ct)
                .ConfigureAwait(false);
            var memoryFiles = normalized.MemoryFiles;
            if (memoryFiles is null)
            {
                var userHome = normalized.MemoryUserHome
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                ArgumentException.ThrowIfNullOrWhiteSpace(userHome);
                memoryFiles = new MemoryFileService(
                    Path.GetFullPath(userHome),
                    normalized.WorkspaceRoot);
            }

            var contextComposer = new AgentContextComposer(
                memoryFiles);
            skillCatalog = new SkillCatalogService(
                normalized.WorkspaceRoot,
                normalized.DataDirectory,
                normalized.RuntimeInstanceId,
                normalized.SkillSources);
            await skillCatalog.InitializeAsync(ct).ConfigureAwait(false);
            await diagnosticLog.WriteAsync(
                    RuntimeDiagnosticLogLevel.Information,
                    "RuntimeStarted",
                    alwaysWrite: true,
                    ct: ct)
                .ConfigureAwait(false);
            return new LocalRuntime(
                normalized,
                workspaceLock,
                connection.ConnectionString,
                contextComposer,
                memoryFiles,
                skillCatalog,
                diagnosticLog,
                normalized.WorkerSupervisor ?? new ProviderWorkerSupervisor(
                    workerLog: line => ProviderWorkerLog.Write(diagnosticLog, line)));
        }
        catch (Exception ex)
        {
            if (diagnosticLog is not null)
            {
                await diagnosticLog.TryWriteAsync(
                        RuntimeDiagnosticLogLevel.Error,
                        "RuntimeStartFailed",
                        exception: ex,
                        alwaysWrite: true)
                    .ConfigureAwait(false);
                await diagnosticLog.DisposeAsync().ConfigureAwait(false);
            }

            if (skillCatalog is not null)
            {
                await skillCatalog.DisposeAsync().ConfigureAwait(false);
            }

            await workspaceLock.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public IAsyncEnumerable<RuntimeEventEnvelope> RunAsync(
        NewSessionRunRequest request,
        CancellationToken ct = default) => RunNewSessionAsync(request, ct);

    public IAsyncEnumerable<RuntimeEventEnvelope> RunAsync(
        ExistingSessionRunRequest request,
        CancellationToken ct = default) => RunExistingSessionAsync(request, ct);

    /// <summary>Gets one persisted Session snapshot.</summary>
    public async Task<PersistedSessionSnapshot?> GetSessionSnapshotAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await using var connection = await OpenDatabaseConnectionAsync(ct).ConfigureAwait(false);
        var repository = new SqliteSessionRepository(connection);
        return await repository.GetSessionSnapshotAsync(sessionId, ct).ConfigureAwait(false);
    }

    /// <summary>Lists persisted Sessions using cursor pagination.</summary>
    public async Task<SessionListResult> ListSessionsAsync(
        SessionListParameters parameters,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters));
        }

        if (parameters.Cursor is not null && string.IsNullOrWhiteSpace(parameters.Cursor))
        {
            throw new ArgumentException("The Session cursor must not be empty.", nameof(parameters));
        }

        await using var connection = await OpenDatabaseConnectionAsync(ct).ConfigureAwait(false);
        var repository = new SqliteSessionRepository(connection);
        var descriptors = await repository.ListSessionsAsync(
                new SessionListQuery(
                    parameters.Mode,
                    parameters.Status,
                    parameters.Since,
                    parameters.Search,
                    parameters.Limit + 1,
                    parameters.Cursor),
                ct)
            .ConfigureAwait(false);

        string? nextCursor = null;
        IReadOnlyList<SessionDescriptor> page = descriptors;
        if (descriptors.Count > parameters.Limit)
        {
            page = descriptors.Take(parameters.Limit).ToArray();
            var last = page[^1];
            nextCursor = string.Format(
                CultureInfo.InvariantCulture,
                "{0:O}|{1}",
                last.UpdatedAt,
                last.SessionId);
        }

        return new SessionListResult(
            page.Select(static descriptor => new SessionListItem(
                    descriptor.SessionId,
                    descriptor.Mode,
                    descriptor.Status,
                    descriptor.UpdatedAt,
                    descriptor.Title))
                .ToArray(),
            nextCursor);
    }

    /// <summary>Lists persisted message metadata for one Session.</summary>
    public async Task<SessionMessagesListResult> ListSessionMessagesAsync(
        SessionMessagesListParameters parameters,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameters.SessionId);
        if (parameters.PageSize is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters));
        }

        if (parameters.Cursor is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters));
        }

        await using var connection = await OpenDatabaseConnectionAsync(ct).ConfigureAwait(false);
        var repository = new SqliteSessionRepository(connection);
        var entries = await repository.ListMessageIndexAsync(
                parameters.SessionId,
                parameters.Cursor,
                parameters.PageSize + 1,
                ct)
            .ConfigureAwait(false);
        var hasMore = entries.Count > parameters.PageSize;
        var messages = entries.Take(parameters.PageSize)
            .Select(static entry => new SessionMessageDescriptor(
                entry.MessageId,
                entry.Sequence,
                entry.InvocationId,
                entry.AgentId,
                entry.Role,
                entry.CreatedAt))
            .ToArray();
        return new SessionMessagesListResult(
            messages,
            hasMore ? messages[^1].Sequence : null);
    }

    /// <summary>Reads canonical Session history with content blocks.</summary>
    public async Task<SessionHistoryReadResult> ReadSessionHistoryAsync(
        SessionHistoryReadParameters parameters,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameters.SessionId);
        if (parameters.PageSize is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters));
        }

        if (parameters.Cursor is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters));
        }

        if (!TryParseBlobMode(parameters.BlobMode, out var blobMode))
        {
            throw new ArgumentOutOfRangeException(nameof(parameters));
        }

        if (await GetSessionSnapshotAsync(parameters.SessionId, ct).ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException($"Session '{parameters.SessionId}' was not found.");
        }

        var store = new ConversationStore(_dataDirectory);
        var page = await store.ReadPageAsync(
                parameters.SessionId,
                parameters.Cursor,
                parameters.PageSize + 1,
                new ConversationReadOptions(blobMode),
                ct)
            .ConfigureAwait(false);
        var hasMore = page.Count > parameters.PageSize;
        var messages = page.Take(parameters.PageSize)
            .Select(record => MapHistoryMessage(
                record,
                parameters.IncludeReasoning,
                parameters.IncludeToolCalls))
            .ToArray();
        return new SessionHistoryReadResult(
            messages,
            hasMore && messages.Length > 0 ? messages[^1].Sequence : null);
    }

    /// <summary>Reads the canonical message history for Session export.</summary>
    public async Task<IReadOnlyList<ConversationRecordV1>> ReadSessionHistoryAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var store = new ConversationStore(_dataDirectory);
        return await store.ReadAllAsync(
                sessionId,
                new ConversationReadOptions(ConversationBlobReadMode.Stream),
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>Gets canonical history and effective context projection statistics.</summary>
    public async Task<SessionContextStatus?> GetSessionContextStatusAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (await GetSessionSnapshotAsync(sessionId, ct).ConfigureAwait(false) is null)
        {
            return null;
        }

        var store = new ConversationStore(_dataDirectory);
        var source = await store.ReadCompactionSourceAsync(sessionId, ct).ConfigureAwait(false);
        var cache = await store.ReadCompactCacheAsync(sessionId, ct).ConfigureAwait(false);
        var metadata = await _compactionService.GetStatusAsync(sessionId, ct: ct).ConfigureAwait(false);
        var cacheCurrent = metadata?.ProjectionRevision is not null;
        var windowCacheCurrent = cache is { Strategy: "full" or "sliding-window" }
            && cache.Schema == ConversationCompactCacheV1.CurrentSchema
            && cache.SourceHistorySha256 == source.HistorySha256;
        var canonicalEstimatedTokens = EstimateHistoryTokens(source.Records);

        return new SessionContextStatus(
            sessionId,
            source.Records.Count,
            canonicalEstimatedTokens,
            cacheCurrent ? 1 + source.Records.Count(record => record.Sequence > metadata!.CoveredThroughSequence)
                : windowCacheCurrent ? cache!.ProjectionMessages.Length : source.Records.Count,
            windowCacheCurrent ? cache!.AfterEstimatedTokens : metadata?.HistoryEstimatedTokens ?? canonicalEstimatedTokens,
            cacheCurrent || windowCacheCurrent ? cache!.Strategy : "full",
            cacheCurrent || windowCacheCurrent,
            cacheCurrent || windowCacheCurrent ? cache!.CreatedAt : null);
    }

    /// <summary>Sets a persisted Session status without changing its conversation data.</summary>
    public async Task<bool> TrySetSessionStatusAsync(
        string sessionId,
        SessionStatus status,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        var operation = status is SessionStatus.Archived ? "archive" : "unarchive";
        var auditId = await AppendMaintenanceAuditAsync(
                sessionId,
                operation,
                "prepared",
                outcome: null,
                $"targetStatus={status}",
                ct)
            .ConfigureAwait(false);
        await using var connection = await OpenDatabaseConnectionAsync(ct).ConfigureAwait(false);
        var repository = new SqliteSessionRepository(connection);
        var updated = await repository.TrySetSessionStatusAsync(sessionId, status, ct)
            .ConfigureAwait(false);
        await AppendMaintenanceAuditAsync(
                auditId,
                sessionId,
                operation,
                "completed",
                updated ? "ok" : "not-found",
                $"targetStatus={status}",
                CancellationToken.None)
            .ConfigureAwait(false);
        return updated;
    }

    /// <summary>Deletes a Session after establishing a same-volume recovery point.</summary>
    public async Task<SessionDeletionResult?> DeleteSessionAsync(
        string sessionId,
        bool includeBlobs,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var detail = $"includeBlobs={includeBlobs.ToString().ToLowerInvariant()}";
        var auditId = await AppendMaintenanceAuditAsync(
                sessionId,
                "delete",
                "prepared",
                outcome: null,
                detail,
                ct)
            .ConfigureAwait(false);
        var result = await DeleteSessionCoreAsync(sessionId, includeBlobs, ct)
            .ConfigureAwait(false);
        await AppendMaintenanceAuditAsync(
                auditId,
                sessionId,
                "delete",
                "completed",
                result is null ? "not-found" : "ok",
                detail,
                CancellationToken.None)
            .ConfigureAwait(false);
        return result;
    }

    private async Task<SessionDeletionResult?> DeleteSessionCoreAsync(
        string sessionId,
        bool includeBlobs,
        CancellationToken ct)
    {

        await using var connection = await OpenDatabaseConnectionAsync(ct).ConfigureAwait(false);
        var repository = new SqliteSessionRepository(connection);
        if (await repository.GetSessionSnapshotAsync(sessionId, ct).ConfigureAwait(false) is null)
        {
            return null;
        }

        var toolBlobReferences = includeBlobs
            ? await repository.GetToolResultBlobReferencesAsync(sessionId, ct)
                .ConfigureAwait(false)
            : new SessionBlobReferences([], []);

        var store = new ConversationStore(_dataDirectory);
        ConversationDeleteRecoveryPoint? recoveryPoint = null;
        var databaseCommitted = false;
        try
        {
            recoveryPoint = await store.PrepareSessionDeletionAsync(
                    sessionId,
                    includeBlobs,
                    toolBlobReferences.TargetBlobIds,
                    toolBlobReferences.ProtectedBlobIds,
                    ct)
                .ConfigureAwait(false);
            if (!await repository.TryDeleteSessionAsync(sessionId, ct).ConfigureAwait(false))
            {
                await store.RestoreSessionDeletionAsync(
                        recoveryPoint,
                        $"Session '{sessionId}' no longer exists.")
                    .ConfigureAwait(false);
                return null;
            }

            databaseCommitted = true;
            await store.CommitSessionDeletionAsync(recoveryPoint, CancellationToken.None)
                .ConfigureAwait(false);
            _sessionSelections.TryRemove(sessionId, out _);
            return new SessionDeletionResult(
                sessionId,
                includeBlobs,
                recoveryPoint.QuarantinedBlobCount,
                recoveryPoint.RecoveryDirectory);
        }
        catch (Exception ex) when (!databaseCommitted && recoveryPoint is not null)
        {
            try
            {
                await store.RestoreSessionDeletionAsync(recoveryPoint, ex.Message)
                    .ConfigureAwait(false);
            }
            catch (Exception restoreException)
            {
                throw new InvalidDataException(
                    $"Session deletion failed and recovery point '{recoveryPoint.RecoveryDirectory}' could not be restored.",
                    new AggregateException(ex, restoreException));
            }

            throw;
        }
    }

    /// <summary>Builds or plans a rebuildable ContextProjection cache for a Session.</summary>
    public async Task<SessionCompactionResult?> CompactSessionAsync(
        string sessionId,
        SessionCompactionOptions options,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(options);
        ValidateCompactionOptions(options);

        if (options.DryRun)
        {
            return await CompactSessionCoreAsync(sessionId, options, ct).ConfigureAwait(false);
        }

        var detail = $"strategy={GetCompactionStrategyName(options.Strategy)};force={options.Force.ToString().ToLowerInvariant()}";
        var auditId = await AppendMaintenanceAuditAsync(
                sessionId,
                "compact",
                "prepared",
                outcome: null,
                detail,
                ct)
            .ConfigureAwait(false);
        var result = await CompactSessionCoreAsync(sessionId, options, ct).ConfigureAwait(false);
        var outcome = result switch
        {
            null => "not-found",
            { CacheHit: true } => "cache-hit",
            _ => "ok"
        };
        await AppendMaintenanceAuditAsync(
                auditId,
                sessionId,
                "compact",
                "completed",
                outcome,
                detail,
                CancellationToken.None)
            .ConfigureAwait(false);
        return result;
    }

    private Task<SessionCompactionResult?> CompactSessionCoreAsync(
        string sessionId, SessionCompactionOptions options, CancellationToken ct) =>
        _compactionService.CompactAsync(sessionId, options, ct);
    /// <summary>Gets the current persisted Selection for a Session.</summary>
    public async Task<NextTurnSelection?> GetSessionSelectionAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        var snapshot = await GetSessionSnapshotAsync(sessionId, ct).ConfigureAwait(false);
        return snapshot is null
            ? null
            : await GetSessionSelectionAsync(snapshot, ct).ConfigureAwait(false);
    }

    /// <summary>Updates the next-turn Selection using optimistic concurrency.</summary>
    public async Task<NextTurnSelection> UpdateSessionSelectionAsync(
        string sessionId,
        int expectedSelectionVersion,
        NextTurnSelection selection,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(selection);
        using var mutation = _compactionService.EnterMutation(sessionId);
        if (expectedSelectionVersion < 0
            || selection.SelectionVersion <= expectedSelectionVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedSelectionVersion));
        }

        await using var connection = await OpenDatabaseConnectionAsync(ct).ConfigureAwait(false);
        var repository = new SqliteSessionRepository(connection);
        var snapshot = await repository.GetSessionSnapshotAsync(sessionId, ct)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
        if (snapshot.SelectionVersion is null || snapshot.SelectionJson is null)
        {
            throw new InvalidOperationException(
                $"Session '{sessionId}' has no persisted Selection.");
        }

        if (selection.Mode != snapshot.Mode)
        {
            throw new InvalidOperationException("A Session's mode cannot be changed.");
        }

        var persistedSelection = RuntimeRunMetadata.RemovePromptContent(selection);
        var selectionJson = JsonSerializer.Serialize(
            persistedSelection,
            RuntimeJsonContext.Default.NextTurnSelection);
        var agentSnapshots = RuntimeRunMetadata.CreateAgentSnapshots(selection);
        var saved = await repository.TrySaveSessionSelectionAsync(
                sessionId,
                expectedSelectionVersion,
                selection.SelectionVersion,
                selectionJson,
                agentSnapshots,
                ct)
            .ConfigureAwait(false);

        if (!saved)
        {
            throw new InvalidOperationException(
                $"Selection version {expectedSelectionVersion} is no longer current.");
        }

        _sessionSelections[sessionId] = selection;
        return selection;
    }

    /// <summary>Captures the immutable tool catalog visible to the next Run.</summary>
    public ToolCatalogSnapshot GetToolCatalogSnapshot()
    {
        ThrowIfDisposed();
        return _toolCatalogStore.CaptureSnapshot();
    }

    public SkillCatalogSummary GetSkillCatalogSummary()
    {
        ThrowIfDisposed();
        return _skillCatalog.GetSummary();
    }

    public async Task<SkillCatalogSummary> RefreshSkillCatalogAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return (await _skillCatalog.RefreshAsync(ct).ConfigureAwait(false)).ToContract();
    }

    public Task<string> LoadSkillAsync(string name, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _skillCatalog.LoadSkillAsync(name, ct);
    }

    public Task<byte[]> ReadSkillResourceAsync(string name, string resource, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _skillCatalog.ReadResourceAsync(name, resource, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _diagnosticLog.TryWriteAsync(
                RuntimeDiagnosticLogLevel.Information,
                "RuntimeStopping",
                alwaysWrite: true)
            .ConfigureAwait(false);

        await _compactionService.DisposeAsync().ConfigureAwait(false);
        List<IRuntimeProviderAdapter> providers;
        lock (_providerSync)
        {
            providers = [.. _providers];
            _providers.Clear();
        }

        foreach (var provider in providers)
        {
            switch (provider)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }

        await _workerSupervisor.DisposeAsync().ConfigureAwait(false);
        await _blobStore.DisposeAsync().ConfigureAwait(false);
        await _skillCatalog.DisposeAsync().ConfigureAwait(false);
        await _workspaceLock.DisposeAsync().ConfigureAwait(false);
        _maintenanceAudit.Dispose();
        _runGate.Dispose();
        await _diagnosticLog.TryWriteAsync(
                RuntimeDiagnosticLogLevel.Information,
                "RuntimeStopped",
                alwaysWrite: true)
            .ConfigureAwait(false);
        await _diagnosticLog.DisposeAsync().ConfigureAwait(false);
    }

    private async IAsyncEnumerable<RuntimeEventEnvelope> RunNewSessionAsync(
        NewSessionRunRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        await _runGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var provider = ResolveProvider(request.Selection);
            var prepared = await PrepareNewSessionAsync(request, provider, ct)
                .ConfigureAwait(false);
            await _diagnosticLog.TryWriteAsync(
                    RuntimeDiagnosticLogLevel.Information,
                    "RunStarted",
                    prepared.RunId,
                    prepared.SessionId)
                .ConfigureAwait(false);
            yield return prepared.Accepted;
            await foreach (var envelope in ExecuteRunWithFailureBoundaryAsync(
                    prepared,
                    request,
                    provider,
                    providerId => ResolveProvider(request.Selection, providerId),
                    ct)
                .ConfigureAwait(false))
            {
                yield return envelope;
            }
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async IAsyncEnumerable<RuntimeEventEnvelope> RunExistingSessionAsync(
        ExistingSessionRunRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunIdempotencyKey);
        if (request.InputOverride is not { Length: > 0 })
        {
            throw new ArgumentException("Existing Session input is required.", nameof(request));
        }

        using var mutation = _compactionService.EnterMutation(request.SessionId);
        await _runGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenDatabaseConnectionAsync(ct).ConfigureAwait(false);
            var repository = new SqliteSessionRepository(connection);
            var snapshot = await repository.GetSessionSnapshotAsync(request.SessionId, ct)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Session '{request.SessionId}' was not found.");
            var savedSelection = await GetSessionSelectionAsync(snapshot, ct).ConfigureAwait(false);
            if (request.ExpectedSelectionVersion is { } expectedVersion
                && expectedVersion != savedSelection.SelectionVersion)
            {
                throw new InvalidOperationException(
                    $"Selection version {expectedVersion} does not match current version {savedSelection.SelectionVersion}.");
            }

            var effectiveSelection = request.TurnOverride ?? savedSelection;
            if (effectiveSelection.Mode != snapshot.Mode)
            {
                throw new InvalidOperationException(
                    $"Selection mode {effectiveSelection.Mode} does not match Session mode {snapshot.Mode}.");
            }

            var executionRequest = new NewSessionRunRequest(
                $"existing:{request.SessionId}",
                request.RunIdempotencyKey,
                snapshot.Mode,
                effectiveSelection,
                request.InputOverride,
                Language: request.Language);
            ValidateRequest(executionRequest);
            if (request.TurnOverride is null
                && effectiveSelection.ModeOptions is ExpertModeOptions
                {
                    Agent.SystemPrompt.Length: 0
                })
            {
                throw new InvalidOperationException(
                    "A TurnOverride with the current Agent prompt is required to resume this Session.");
            }

            if (effectiveSelection.ModeOptions is ExpertModeOptions expertOptions)
            {
                if (string.IsNullOrWhiteSpace(expertOptions.Agent.SystemPrompt)
                    && request.TurnOverride is not null)
                {
                    throw new InvalidOperationException(
                        "An empty Agent prompt cannot be used to resume an Expert Session.");
                }

                if (request.TurnOverride is null)
                {
                    ValidateResumedExpertPrompt(snapshot, expertOptions);
                }
            }

            var provider = ResolveProvider(effectiveSelection);
            var prepared = await PrepareExistingSessionAsync(
                    repository,
                    request,
                    executionRequest,
                    provider,
                    ct)
                .ConfigureAwait(false);
            await _diagnosticLog.TryWriteAsync(
                    RuntimeDiagnosticLogLevel.Information,
                    "RunStarted",
                    prepared.RunId,
                    prepared.SessionId)
                .ConfigureAwait(false);
            yield return prepared.Accepted;
            await foreach (var envelope in ExecuteRunWithFailureBoundaryAsync(
                    prepared,
                    executionRequest,
                    provider,
                    providerId => ResolveProvider(effectiveSelection, providerId),
                    ct)
                .ConfigureAwait(false))
            {
                yield return envelope;
            }
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async IAsyncEnumerable<RuntimeEventEnvelope> ExecuteRunWithFailureBoundaryAsync(
        PreparedRun prepared,
        NewSessionRunRequest request,
        IRuntimeProviderAdapter provider,
        Func<string, IRuntimeProviderAdapter> providerFactory,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var enumerator = _executionCore.RunAsync(
                prepared.RunId,
                prepared.SessionId,
                request,
                provider,
                _toolCatalogStore,
                _toolCatalogStore.CaptureSnapshot(),
                controlPeer: null,
                localToolConsentHandler: _toolConsentHandler,
                credentialRefreshHandler: null,
                providerFactory: providerFactory,
                skillCatalog: _skillCatalog,
                ct: ct)
            .GetAsyncEnumerator(ct);
        RuntimeEventEnvelope? terminalEnvelope = null;
        while (true)
        {
            RuntimeEventEnvelope envelope;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }

                envelope = enumerator.Current;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await _diagnosticLog.TryWriteAsync(
                        RuntimeDiagnosticLogLevel.Warning,
                        "RunCancelled",
                        prepared.RunId,
                        prepared.SessionId)
                    .ConfigureAwait(false);
                terminalEnvelope = await CompleteLocalTerminalRunAsync(
                    prepared,
                    exception: null,
                    isCancelled: true).ConfigureAwait(false);
                break;
            }
            catch (Exception exception)
            {
                await _diagnosticLog.TryWriteAsync(
                        RuntimeDiagnosticLogLevel.Error,
                        "RunFailed",
                        prepared.RunId,
                        prepared.SessionId,
                        exception: exception)
                    .ConfigureAwait(false);
                terminalEnvelope = await CompleteLocalTerminalRunAsync(
                    prepared,
                    exception,
                    isCancelled: false).ConfigureAwait(false);
                break;
            }

            await LogObservedEnvelopeAsync(envelope, prepared).ConfigureAwait(false);
            yield return envelope;
        }

        if (terminalEnvelope is not null)
        {
            yield return terminalEnvelope;
        }
    }

    private ValueTask LogObservedEnvelopeAsync(
        RuntimeEventEnvelope envelope,
        PreparedRun prepared) => envelope.MessageType switch
        {
            MessageTypes.RunCompleted => _diagnosticLog.TryWriteAsync(
                RuntimeDiagnosticLogLevel.Information,
                "RunCompleted",
                prepared.RunId,
                prepared.SessionId),
            MessageTypes.RunCancelled => _diagnosticLog.TryWriteAsync(
                RuntimeDiagnosticLogLevel.Warning,
                "RunCancelled",
                prepared.RunId,
                prepared.SessionId),
            MessageTypes.RunFailed => LogRunFailedAsync(envelope, prepared),
            MessageTypes.ToolCallCompleted => LogToolCallAsync(envelope, prepared),
            MessageTypes.ProviderProjectionAdjusted => _diagnosticLog.TryWriteAsync(
                RuntimeDiagnosticLogLevel.Warning,
                "ProviderProjectionAdjusted",
                prepared.RunId,
                prepared.SessionId,
                writeDetails: writer => writer.WriteString("payload", envelope.Payload.GetRawText())),
            MessageTypes.UsageUpdated => _diagnosticLog.TryWriteAsync(
                RuntimeDiagnosticLogLevel.Debug,
                "UsageUpdated",
                prepared.RunId,
                prepared.SessionId,
                writeDetails: writer => writer.WriteString("payload", envelope.Payload.GetRawText())),
            _ => ValueTask.CompletedTask
        };

    private ValueTask LogToolCallAsync(
        RuntimeEventEnvelope envelope,
        PreparedRun prepared)
    {
        string? toolName = null;
        string? callId = null;
        try
        {
            if (envelope.Payload.TryGetProperty("name", out var name))
            {
                toolName = name.GetString();
            }

            if (envelope.Payload.TryGetProperty("callId", out var id))
            {
                callId = id.GetString();
            }
        }
        catch (InvalidOperationException)
        {
            // 工具事件结构异常时仍记录事件名，不中断运行。
        }

        var capturedName = toolName;
        var capturedCallId = callId;
        return _diagnosticLog.TryWriteAsync(
            RuntimeDiagnosticLogLevel.Information,
            "ToolCallCompleted",
            prepared.RunId,
            prepared.SessionId,
            writeDetails: writer =>
            {
                if (!string.IsNullOrWhiteSpace(capturedName))
                {
                    writer.WriteString("tool", capturedName);
                }

                if (!string.IsNullOrWhiteSpace(capturedCallId))
                {
                    writer.WriteString("callId", capturedCallId);
                }
            });
    }

    private ValueTask LogRunFailedAsync(
        RuntimeEventEnvelope envelope,
        PreparedRun prepared)
    {
        try
        {
            var failed = envelope.Payload.Deserialize(RuntimeJsonContext.Default.RunFailedEvent);
            return _diagnosticLog.TryWriteAsync(
                RuntimeDiagnosticLogLevel.Error,
                "RunFailed",
                prepared.RunId,
                prepared.SessionId,
                error: failed?.Error);
        }
        catch (JsonException exception)
        {
            return _diagnosticLog.TryWriteAsync(
                RuntimeDiagnosticLogLevel.Error,
                "RunFailed",
                prepared.RunId,
                prepared.SessionId,
                exception: exception);
        }
    }

    private async Task<RuntimeEventEnvelope?> CompleteLocalTerminalRunAsync(
        PreparedRun prepared,
        Exception? exception,
        bool isCancelled)
    {
        var error = isCancelled
            ? null
            : new RuntimeError(
                "RunExecutionFailed",
                "runtime",
                string.IsNullOrWhiteSpace(exception?.Message)
                    ? "Run execution failed."
                    : exception.Message,
                IsRetryable: false,
                ProviderDetails: null,
                Guid.NewGuid().ToString("N"));
        var messageType = isCancelled ? MessageTypes.RunCancelled : MessageTypes.RunFailed;
        var payload = isCancelled
            ? JsonSerializer.SerializeToElement(
                new RunCancelledEvent(prepared.RunId, prepared.SessionId),
                RuntimeJsonContext.Default.RunCancelledEvent)
            : JsonSerializer.SerializeToElement(
                new RunFailedEvent(prepared.RunId, prepared.SessionId, error!),
                RuntimeJsonContext.Default.RunFailedEvent);
        var status = isCancelled ? RunStatus.Cancelled : RunStatus.Failed;
        var errorJson = error is null
            ? null
            : JsonSerializer.Serialize(error, RuntimeJsonContext.Default.RuntimeError);

        int[] retryDelaysMilliseconds = [0, 100, 500];
        foreach (var delayMilliseconds in retryDelaysMilliseconds)
        {
            if (delayMilliseconds > 0)
            {
                await Task.Delay(delayMilliseconds).ConfigureAwait(false);
            }

            try
            {
                await using var connection = await OpenDatabaseConnectionAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                var repository = new SqliteSessionRepository(connection);
                var gsn = await repository.TryCommitTerminalRunAsync(
                    prepared.RunId,
                    status,
                    terminalText: null,
                    errorJson,
                    messageType,
                    payload.GetRawText(),
                    CancellationToken.None).ConfigureAwait(false);
                if (gsn is null)
                {
                    return null;
                }

                var snapshot = await repository.GetRunSnapshotAsync(
                    prepared.RunId,
                    CancellationToken.None).ConfigureAwait(false);
                return new RuntimeEventEnvelope(
                    RuntimeInstanceId,
                    gsn.Value,
                    prepared.RunId,
                    snapshot?.LastRunSequence ?? 1,
                    messageType,
                    DateTimeOffset.UtcNow,
                    payload);
            }
            catch (Exception)
            {
            }
        }

        return new RuntimeEventEnvelope(
            RuntimeInstanceId,
            Gsn: 0,
            prepared.RunId,
            RunSequence: 1,
            messageType,
            DateTimeOffset.UtcNow,
            payload);
    }

    private async Task<PreparedRun> PrepareNewSessionAsync(
        NewSessionRunRequest request,
        IRuntimeProviderAdapter provider,
        CancellationToken ct)
    {
        await using var connection = await OpenDatabaseConnectionAsync(ct).ConfigureAwait(false);
        var repository = new SqliteSessionRepository(connection);
        var requestHash = RuntimeRunMetadata.ComputeRequestHash(request);
        var sessionId = await repository.CreateSessionAsync(
                request.Mode,
                request.SessionIdempotencyKey,
                TimeSpan.FromDays(7),
                requestHash,
                ct)
            .ConfigureAwait(false);
        var persistedSelection = RuntimeRunMetadata.RemovePromptContent(request.Selection);
        var selectionJson = JsonSerializer.Serialize(
            persistedSelection,
            RuntimeJsonContext.Default.NextTurnSelection);
        var saved = await repository.TrySaveSessionSelectionAsync(
                sessionId,
                expectedSelectionVersion: null,
                request.Selection.SelectionVersion,
                selectionJson,
                RuntimeRunMetadata.CreateAgentSnapshots(request.Selection),
                ct)
            .ConfigureAwait(false);
        if (!saved)
        {
            throw new InvalidOperationException("The Session Selection could not be initialized.");
        }

        // 临时标题：首条用户输入文本写入 sessions.title（仅空标题时）。
        var titleText = request.InitialInput?
            .OfType<TextContentBlock>()
            .Select(static block => block.Text)
            .FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text));
        if (!string.IsNullOrWhiteSpace(titleText))
        {
            await repository
                .TrySetInitialSessionTitleAsync(sessionId, FlattenSessionTitle(titleText), ct)
                .ConfigureAwait(false);
        }

        _sessionSelections[sessionId] = request.Selection;
        return await PrepareRunAsync(
                repository,
                sessionId,
                request.RunIdempotencyKey,
                requestHash,
                provider,
                ct)
            .ConfigureAwait(false);
    }

    private static string FlattenSessionTitle(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0 && sb[^1] != ' ')
                {
                    sb.Append(' ');
                }
            }
            else
            {
                sb.Append(ch);
            }
        }

        if (sb.Length > 120)
        {
            sb.Length = 120;
            if (sb.Length > 0 && char.IsHighSurrogate(sb[^1]))
            {
                sb.Length = 119;
            }
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<PreparedRun> PrepareExistingSessionAsync(
        SqliteSessionRepository repository,
        ExistingSessionRunRequest request,
        NewSessionRunRequest executionRequest,
        IRuntimeProviderAdapter provider,
        CancellationToken ct)
    {
        // 续跑时若 title 仍空，用本轮用户输入补上临时标题。
        var titleText = executionRequest.InitialInput?
            .OfType<TextContentBlock>()
            .Select(static block => block.Text)
            .FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text));
        if (!string.IsNullOrWhiteSpace(titleText))
        {
            await repository
                .TrySetInitialSessionTitleAsync(
                    request.SessionId,
                    FlattenSessionTitle(titleText),
                    ct)
                .ConfigureAwait(false);
        }

        return await PrepareRunAsync(
                repository,
                request.SessionId,
                executionRequest.RunIdempotencyKey,
                RuntimeRunMetadata.ComputeRequestHash(request),
                provider,
                ct)
            .ConfigureAwait(false);
    }

    private async Task<PreparedRun> PrepareRunAsync(
        SqliteSessionRepository repository,
        string sessionId,
        string runIdempotencyKey,
        string requestHash,
        IRuntimeProviderAdapter provider,
        CancellationToken ct)
    {
        _ = provider;
        var runId = await repository.CreateRunAsync(
                sessionId,
                runIdempotencyKey,
                TimeSpan.FromHours(24),
                requestHash,
                ct)
            .ConfigureAwait(false);
        if (await repository.GetRunStatusAsync(runId, ct).ConfigureAwait(false)
            is not RunStatus.Accepted)
        {
            throw new InvalidOperationException(
                $"Run idempotency key '{runIdempotencyKey}' already completed or is still active.");
        }

        await using var outboxConnection = await OpenDatabaseConnectionAsync(ct).ConfigureAwait(false);
        using var outbox = new SqliteEventOutbox(
            outboxConnection,
            Path.Combine(_dataDirectory, "replay"),
            SqliteEventOutbox.DefaultMaxReplayBytes);
        var payload = JsonSerializer.SerializeToElement(
            new RunAcceptedEvent(sessionId, runId),
            RuntimeJsonContext.Default.RunAcceptedEvent);
        var gsn = await outbox.AppendAsync(
                runId,
                runSequence: 0,
                MessageTypes.RunAccepted,
                payload.GetRawText(),
                ct)
            .ConfigureAwait(false);
        var accepted = new RuntimeEventEnvelope(
            RuntimeInstanceId,
            gsn,
            runId,
            RunSequence: 0,
            MessageTypes.RunAccepted,
            DateTimeOffset.UtcNow,
            payload);
        return new PreparedRun(sessionId, runId, accepted);
    }

    private async Task<NextTurnSelection> GetSessionSelectionAsync(
        PersistedSessionSnapshot snapshot,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_sessionSelections.TryGetValue(snapshot.SessionId, out var selection))
        {
            return selection;
        }

        if (string.IsNullOrWhiteSpace(snapshot.SelectionJson))
        {
            throw new InvalidOperationException(
                $"Session '{snapshot.SessionId}' has no persisted Selection.");
        }

        selection = JsonSerializer.Deserialize(
            snapshot.SelectionJson,
            RuntimeJsonContext.Default.NextTurnSelection)
            ?? throw new InvalidDataException(
                $"Session '{snapshot.SessionId}' has an invalid Selection snapshot.");
        return selection;
    }

    private IRuntimeProviderAdapter ResolveProvider(
        NextTurnSelection selection,
        string? providerId = null)
    {
        var effectiveSelection = string.IsNullOrWhiteSpace(providerId)
            || providerId.Equals(
                selection.DefaultSelection.ProviderId,
                StringComparison.Ordinal)
            ? selection
            : selection with
            {
                DefaultSelection = selection.DefaultSelection with
                {
                    ProviderId = providerId
                }
            };
        var provider = _providerResolver(effectiveSelection)
            ?? throw new InvalidOperationException("The Provider resolver returned null.");
        lock (_providerSync)
        {
            _providers.Add(provider);
        }

        return provider;
    }

    private static void ValidateCompactionOptions(SessionCompactionOptions options)
    {
        if (!Enum.IsDefined(options.Strategy))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.ProviderId is not null && string.IsNullOrWhiteSpace(options.ProviderId))
        {
            throw new ArgumentException("The compaction Provider must not be empty.", nameof(options));
        }

        if (options.ModelId is not null && string.IsNullOrWhiteSpace(options.ModelId))
        {
            throw new ArgumentException("The compaction model must not be empty.", nameof(options));
        }

        if (options.KeepLastTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.KeepLastTokens is not null
            && options.Strategy is not SessionCompactionStrategy.SlidingWindow)
        {
            throw new ArgumentException(
                "KeepLastTokens can only be used with the sliding-window strategy.",
                nameof(options));
        }
    }

    private static int EstimateHistoryTokens(IReadOnlyList<ConversationRecordV1> records)
    {
        long utf8Bytes = 0;
        foreach (var record in records)
        {
            utf8Bytes += Encoding.UTF8.GetByteCount(record.Role);
            utf8Bytes += Encoding.UTF8.GetByteCount(record.Content.GetRawText());
        }

        return (int)Math.Min(int.MaxValue, (utf8Bytes + 3) / 4);
    }

    internal static bool TryParseBlobMode(string? blobMode, out ConversationBlobReadMode mode)
    {
        switch (blobMode?.Trim().ToLowerInvariant())
        {
            case null:
            case "":
            case "metadata":
                mode = ConversationBlobReadMode.Metadata;
                return true;
            case "omit":
                mode = ConversationBlobReadMode.Omit;
                return true;
            case "stream":
                mode = ConversationBlobReadMode.Stream;
                return true;
            default:
                mode = ConversationBlobReadMode.Metadata;
                return false;
        }
    }

    internal static SessionHistoryMessage MapHistoryMessage(
        ConversationRecordV1 record,
        bool includeReasoning,
        bool includeToolCalls)
    {
        var blocks = DeserializeHistoryContent(record.Content)
            .Where(block => block switch
            {
                ReasoningContentBlock => includeReasoning,
                ToolCallContentBlock or ToolResultContentBlock => includeToolCalls,
                _ => true
            })
            .ToArray();
        return new SessionHistoryMessage(
            record.MessageId,
            record.Sequence,
            record.InvocationId,
            record.AgentId,
            record.Role,
            record.Timestamp,
            blocks);
    }

    private static ContentBlock[] DeserializeHistoryContent(JsonElement content)
    {
        if (content.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return [];
        }

        if (content.ValueKind is JsonValueKind.Array)
        {
            try
            {
                return content.Deserialize(RuntimeJsonContext.Default.ContentBlockArray) ?? [];
            }
            catch (JsonException)
            {
                return [new TextContentBlock(content.GetRawText())];
            }
        }

        if (content.ValueKind is JsonValueKind.String)
        {
            return [new TextContentBlock(content.GetString() ?? string.Empty)];
        }

        return [new TextContentBlock(content.GetRawText())];
    }

    private static string GetCompactionStrategyName(SessionCompactionStrategy strategy) =>
        strategy switch
        {
            SessionCompactionStrategy.Full => "full",
            SessionCompactionStrategy.SlidingWindow => "sliding-window",
            SessionCompactionStrategy.Summary => "summary",
            _ => throw new ArgumentOutOfRangeException(nameof(strategy))
        };

    private async Task<string> AppendMaintenanceAuditAsync(
        string sessionId,
        string operation,
        string phase,
        string? outcome,
        string detail,
        CancellationToken ct)
    {
        var auditId = Guid.NewGuid().ToString("N");
        await AppendMaintenanceAuditAsync(
                auditId,
                sessionId,
                operation,
                phase,
                outcome,
                detail,
                ct)
            .ConfigureAwait(false);
        return auditId;
    }

    private async Task AppendMaintenanceAuditAsync(
        string auditId,
        string sessionId,
        string operation,
        string phase,
        string? outcome,
        string detail,
        CancellationToken ct) =>
        await _maintenanceAudit.AppendAsync(
                new SessionMaintenanceAuditRecord(
                    SessionMaintenanceAuditLog.CurrentSchema,
                    auditId,
                    RuntimeInstanceId,
                    sessionId,
                    operation,
                    phase,
                    outcome,
                    detail,
                    DateTimeOffset.UtcNow),
                ct)
            .ConfigureAwait(false);

    private async Task<SqliteConnection> OpenDatabaseConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_databaseConnectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await SqliteSchema.EnsureCreatedAsync(connection, ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void ValidateRequest(NewSessionRunRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionIdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunIdempotencyKey);
        if (request.Selection.Mode != request.Mode)
        {
            throw new ArgumentException(
                "Selection mode must match the requested Runtime mode.",
                nameof(request));
        }

        var hasMatchingOptions = request.Mode switch
        {
            RuntimeMode.Expert => request.Selection.ModeOptions is ExpertModeOptions,
            _ => false
        };
        if (!hasMatchingOptions)
        {
            throw new ArgumentException(
                "Selection options must match the requested Runtime mode.",
                nameof(request));
        }

        if (request.InitialInput.Length == 0)
        {
            throw new ArgumentException("Runtime input is required.", nameof(request));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private static bool PathsEqual(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            comparison);
    }


    private static void ValidateResumedExpertPrompt(
        PersistedSessionSnapshot snapshot,
        ExpertModeOptions expertOptions)
    {
        if (string.IsNullOrWhiteSpace(expertOptions.Agent.SystemPrompt))
        {
            throw new InvalidOperationException(
                "An empty Agent prompt cannot be used to resume an Expert Session.");
        }

        var agentSnapshot = snapshot.AgentSnapshots.FirstOrDefault(agent =>
            string.Equals(agent.AgentId, expertOptions.Agent.AgentId, StringComparison.Ordinal));
        if (agentSnapshot is null)
        {
            throw new InvalidOperationException(
                $"Agent '{expertOptions.Agent.AgentId}' is missing from the Session Agent snapshots.");
        }

        var promptHash = RuntimeRunMetadata.ComputePromptHash(expertOptions.Agent.SystemPrompt);
        if (!string.Equals(promptHash, agentSnapshot.PromptHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Agent prompt hash for '{expertOptions.Agent.AgentId}' does not match the Session snapshot.");
        }

        if (!string.Equals(
                expertOptions.Agent.PromptTemplateVersion,
                agentSnapshot.PromptTemplateVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Agent prompt template version for '{expertOptions.Agent.AgentId}' does not match the Session snapshot.");
        }
    }

    private static string ResolveModelVersion(
        IRuntimeProviderAdapter provider,
        string modelId)
    {
        var found = provider.Models.FirstOrDefault(model =>
            string.Equals(model.Id, modelId, StringComparison.Ordinal)
            || string.Equals(model.ProviderModelId, modelId, StringComparison.Ordinal));
        return found?.ProviderModelId ?? found?.Id ?? modelId;
    }

    private sealed record PreparedRun(
        string SessionId,
        string RunId,
        RuntimeEventEnvelope Accepted);
}
