using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server.ProviderWorkers;
using Madorin.AI.Runtime.Server.Auth;
using Madorin.AI.Runtime.Server.Http;
using Madorin.AI.Runtime.Services;
using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Services.Skills;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;
using Madorin.AI.Runtime.Transport.Abstractions;
using Madorin.AI.Runtime.Transport.NamedPipes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Madorin.AI.Runtime.Server;

public sealed class RuntimeServer : IAsyncDisposable
{
    private readonly WorkspaceWriteLock _instanceLock;
    private readonly FileStream _instanceNameLock;
    private readonly string _dataDirectory;
    private readonly string _databaseConnectionString;
    private readonly string _handshakeSecret;
    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _startedAt;
    private readonly TimeSpan _handshakeClockSkew;
    private readonly int _heartbeatIntervalSeconds;
    private readonly TimeSpan _hostLeaseTimeout;
    private readonly RuntimeLimits _limits;
    private readonly Func<NextTurnSelection, IRuntimeProviderAdapter>? _providerResolver;
    private readonly SessionCompactionService _compactionService;
    private readonly HostProviderRegistry? _hostProviderRegistry;
    private readonly IProviderWorkerSupervisor _workerSupervisor;
    private readonly RuntimeExecutionCore _executionCore;
    private readonly BlobStagingStore _blobStore;
    private readonly RuntimeDiagnosticLog _diagnosticLog;
    private readonly ConcurrentDictionary<int, Task> _connections = new();
    private readonly ConcurrentDictionary<string, ActiveRun> _activeRuns =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActiveRunRequest> _activeRunRequests =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _disconnectedHostLeases =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NextTurnSelection> _sessionSelections =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingTerminalRun> _pendingTerminalRuns =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AuthenticatedSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _seenClientNonces = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _eventDispatchGate = new(1, 1);
    private readonly SemaphoreSlim _meetingResumeGate = new(1, 1);
    private readonly RunRegistry _runRegistry;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource<object?> _fatalSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RuntimeHttpServer? _httpServer;
    private Exception? _fatalFailure;
    private int _startupToolRecoveryPending;
    private int _connectionId;
    private int _disposed;

    private RuntimeServer(
        RuntimeServerOptions options,
        WorkspaceWriteLock instanceLock,
        FileStream instanceNameLock,
        string databaseConnectionString,
        bool startupToolRecoveryPending,
        RuntimeDiagnosticLog diagnosticLog)
    {
        _instanceLock = instanceLock;
        _instanceNameLock = instanceNameLock;
        _dataDirectory = options.DataDirectory;
        _databaseConnectionString = databaseConnectionString;
        _handshakeSecret = options.HandshakeSecret
            ?? throw new ArgumentException("A handshake secret is required.", nameof(options));
        _timeProvider = options.TimeProvider;
        _startedAt = options.TimeProvider.GetUtcNow();
        _handshakeClockSkew = options.HandshakeClockSkew;
        _heartbeatIntervalSeconds = options.HeartbeatIntervalSeconds;
        _hostLeaseTimeout = options.HostLeaseTimeout;
        _limits = options.BlobLimits;
        _workerSupervisor = options.WorkerSupervisor ?? new ProviderWorkerSupervisor(
            workerLog: line => ProviderWorkerLog.Write(diagnosticLog, line));
        if (options.ProviderResolver is null)
        {
            _hostProviderRegistry = new HostProviderRegistry(_workerSupervisor);
            _providerResolver = _hostProviderRegistry.CreateResolver();
        }
        else
        {
            _hostProviderRegistry = null;
            _providerResolver = options.ProviderResolver;
        }

        _diagnosticLog = diagnosticLog;
        _compactionService = new SessionCompactionService(
            _dataDirectory,
            _databaseConnectionString,
            selection => ResolveProviderAdapter(selection, selection.DefaultSelection.ProviderId));
        var userHome = options.MemoryUserHome
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(userHome);
        var memoryFiles = new MemoryFileService(
            Path.GetFullPath(userHome),
            options.WorkspaceRoot);
        var agentContextComposer = new AgentContextComposer(memoryFiles);
        _blobStore = new BlobStagingStore(
            _dataDirectory,
            options.BlobLimits,
            options.MaxGlobalBlobBytes);
        _executionCore = new RuntimeExecutionCore(
            _dataDirectory,
            databaseConnectionString,
            options.InstanceId,
            options.WorkspaceRoot,
            agentContextComposer,
            memoryFiles,
            CreateNegotiatedLimits(supportsStage5: true),
            _blobStore);
        _runRegistry = new RunRegistry(
            options.MaxConcurrentRuns,
            options.MaxConcurrentRunsPerProvider,
            options.MaxConcurrentRunsPerSession,
            options.RunTimeout,
            _shutdown.Token);
        _startupToolRecoveryPending = startupToolRecoveryPending ? 1 : 0;
        InstanceId = options.InstanceId;
        WorkspaceRoot = options.WorkspaceRoot;
        PipeName = $"{options.PipePrefix}.{InstanceId[..Math.Min(8, InstanceId.Length)]}";
        EventPipeName = $"{PipeName}.events";

        if (options.HttpPort is > 0)
        {
            var loggerFactory = NullLoggerFactory.Instance;
            _httpServer = new RuntimeHttpServer(
                options.HttpHost,
                options.HttpPort.Value,
                PipeName,
                InstanceId,
                _handshakeSecret,
                loggerFactory);
        }
    }

    public string InstanceId { get; }

    public string WorkspaceRoot { get; }

    public string PipeName { get; }

    public string EventPipeName { get; }

    public int ConnectedSessionCount => _sessions.Count;

    public int ConnectedEventChannelCount => _sessions.Values.Count(
        static session => session.TryGetEventChannel() is not null);

    public async Task DisconnectControlChannelsAsync()
    {
        foreach (var session in _sessions.Values.ToArray())
        {
            await session.DisconnectControlChannelAsync().ConfigureAwait(false);
        }
    }

    public async Task DisconnectEventChannelsAsync()
    {
        foreach (var session in _sessions.Values.ToArray())
        {
            var eventChannel = session.TryGetEventChannel();
            if (eventChannel is not null)
            {
                session.DetachEventChannel(eventChannel);
                await eventChannel.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public static async Task<RuntimeServer> StartAsync(
        RuntimeServerOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConcurrentRuns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConcurrentRunsPerProvider);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConcurrentRunsPerSession);
        if (options.RunTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.RunTimeout,
                "The Run timeout must be positive.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.HeartbeatIntervalSeconds);
        if (options.BlobLimits.MaxToolCallsPerRound is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxToolCallsPerRound must be a positive integer or null.");
        }

        if (options.BlobLimits.MaxToolRounds is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxToolRounds must be a positive integer or null.");
        }

        if (options.HostLeaseTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.HostLeaseTimeout,
                "The host lease timeout must be positive.");
        }
        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        if (!Enum.IsDefined(options.MinimumLogLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.HandshakeClockSkew <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.HandshakeClockSkew,
                "Handshake clock skew must be positive.");
        }

        ct.ThrowIfCancellationRequested();
        var handshakeSecret = options.HandshakeSecret
            ?? throw new ArgumentException(
                "A handshake secret must be supplied through a controlled handle.",
                nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(handshakeSecret);
        var workspace = Path.GetFullPath(options.WorkspaceRoot);
        var dataDirectory = Path.GetFullPath(options.DataDirectory);
        var configDirectory = Path.GetFullPath(options.ConfigDirectory);
        var logDirectory = Path.GetFullPath(options.LogDirectory);
        ValidateDirectoryLayout(dataDirectory, configDirectory, logDirectory);
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(configDirectory);
        Directory.CreateDirectory(logDirectory);
        var resolvedInstanceId = string.IsNullOrWhiteSpace(options.InstanceId)
            ? Guid.NewGuid().ToString("N")
            : options.InstanceId;
        var resolvedPipePrefix = string.IsNullOrWhiteSpace(options.PipePrefix)
            ? "madorin.ai.runtime"
            : options.PipePrefix;
        var instanceNameLocksDirectory = Path.Combine(
            Path.GetTempPath(),
            "madorin.ai.runtime",
            "instance-locks");
        Directory.CreateDirectory(instanceNameLocksDirectory);
        var instanceLockName = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    $"{resolvedPipePrefix}\0{resolvedInstanceId}")))
            .ToLowerInvariant();
        FileStream instanceNameLock;
        try
        {
            instanceNameLock = new FileStream(
                Path.Combine(instanceNameLocksDirectory, instanceLockName + ".lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                "The Runtime instance name or IPC endpoint is already active.",
                ex);
        }

        WorkspaceWriteLock instanceLock;
        try
        {
            instanceLock = await WorkspaceWriteLock.AcquireAsync(
                    workspace,
                    resolvedInstanceId,
                    waitTimeout: TimeSpan.Zero,
                    ct)
                .ConfigureAwait(false);
        }
        catch
        {
            await instanceNameLock.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var normalized = options with
        {
            WorkspaceRoot = workspace,
            DataDirectory = dataDirectory,
            ConfigDirectory = configDirectory,
            LogDirectory = logDirectory,
            InstanceId = resolvedInstanceId,
            PipePrefix = resolvedPipePrefix,
            HandshakeSecret = handshakeSecret
        };

        RuntimeDiagnosticLog? diagnosticLog = null;
        try
        {
            diagnosticLog = RuntimeDiagnosticLog.Create(
                normalized.LogDirectory,
                normalized.InstanceId,
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
                dataDirectory,
                ct).ConfigureAwait(false);
            using var toolStateStore = new SqliteToolIntentRepository(connection);
            var startupToolRecoveryPending = (await toolStateStore
                    .ListRecoverableIntentsAsync(ct)
                    .ConfigureAwait(false))
                .Any(static intent => intent.Status is ToolIntentStatus.Sent);
            await diagnosticLog.WriteAsync(
                    RuntimeDiagnosticLogLevel.Information,
                    "RuntimeStarted",
                    alwaysWrite: true,
                    ct: ct)
                .ConfigureAwait(false);
            return new RuntimeServer(
                normalized,
                instanceLock,
                instanceNameLock,
                connection.ConnectionString,
                startupToolRecoveryPending,
                diagnosticLog);
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

            await instanceLock.DisposeAsync().ConfigureAwait(false);
            await instanceNameLock.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        var stoppingToken = linkedCancellation.Token;
        var pidPath = Path.Combine(_dataDirectory, "runtime.pid");
        await WritePidFileAsync(pidPath, ct).ConfigureAwait(false);
        try
        {
            using var fatalRegistration = stoppingToken.Register(
                static state => ((TaskCompletionSource<object?>)state!).TrySetCanceled(),
                _fatalSignal);
            var rootTasks = new List<Task>
            {
                AcceptConnectionsAsync(
                    PipeName,
                    NamedPipeTransport.MaxControlMessageBytes,
                    HandleControlConnectionAsync,
                    stoppingToken),
                AcceptConnectionsAsync(
                    EventPipeName,
                    NamedPipeTransport.MaxEventMessageBytes,
                    HandleEventConnectionAsync,
                    stoppingToken),
                MonitorHostLeasesAsync(stoppingToken),
                ReconcilePendingTerminalRunsAsync(stoppingToken),
                _fatalSignal.Task
            };

            if (_httpServer is not null)
            {
                await _httpServer.StartAsync(stoppingToken).ConfigureAwait(false);
            }

            var pending = new HashSet<Task>(rootTasks);
            Exception? rootFailure = null;
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(completed);
                if (completed.IsFaulted)
                {
                    rootFailure = completed.Exception?.GetBaseException()
                        ?? new InvalidOperationException("A Runtime root task faulted without an exception.");
                    break;
                }

                if (completed.IsCanceled && !stoppingToken.IsCancellationRequested)
                {
                    rootFailure = new TaskCanceledException(
                        "A Runtime root task was cancelled unexpectedly.");
                    break;
                }
            }

            if (rootFailure is not null)
            {
                try
                {
                    await _shutdown.CancelAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The root failure is the observable Runtime fault.
                }

                await ObserveTasksAsync(rootTasks).ConfigureAwait(false);
                await ObserveConnectionTasksAsync().ConfigureAwait(false);
                ExceptionDispatchInfo.Capture(rootFailure).Throw();
            }

            if (stoppingToken.IsCancellationRequested)
            {
                await ObserveTasksAsync(rootTasks).ConfigureAwait(false);
            }
            else
            {
                await Task.WhenAll(rootTasks).ConfigureAwait(false);
            }
            await DrainConnectionsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _diagnosticLog.TryWriteAsync(
                    RuntimeDiagnosticLogLevel.Error,
                    "RuntimeFailed",
                    exception: ex,
                    alwaysWrite: true)
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            TryDeletePidFile(pidPath);
        }
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

        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var run in _activeRuns.Values)
        {
            run.Cancel();
        }

        await DrainConnectionsAsync().ConfigureAwait(false);
        await DrainRunsAsync().ConfigureAwait(false);
        await ReconcilePendingTerminalRunsOnceAsync().ConfigureAwait(false);
        _disconnectedHostLeases.Clear();
        _sessions.Clear();
        await _workerSupervisor.DisposeAsync().ConfigureAwait(false);
        await _blobStore.DisposeAsync().ConfigureAwait(false);
        if (_httpServer is not null)
        {
            await _httpServer.DisposeAsync().ConfigureAwait(false);
        }

        await _instanceLock.DisposeAsync().ConfigureAwait(false);
        await _instanceNameLock.DisposeAsync().ConfigureAwait(false);
        _compactionService.Dispose();
        _eventDispatchGate.Dispose();
        _meetingResumeGate.Dispose();
        _runRegistry.Dispose();
        _shutdown.Dispose();
        await _diagnosticLog.TryWriteAsync(
                RuntimeDiagnosticLogLevel.Information,
                "RuntimeStopped",
                alwaysWrite: true)
            .ConfigureAwait(false);
        await _diagnosticLog.DisposeAsync().ConfigureAwait(false);
    }

    private async Task AcceptConnectionsAsync(
        string pipeName,
        int maxFrameBytes,
        Func<NamedPipeTransport, CancellationToken, Task> handler,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var transport = await NamedPipeTransport.CreateServerAsync(
                    pipeName,
                    maxFrameBytes,
                    ct).ConfigureAwait(false);
                var task = handler(transport, ct);
                var connectionId = Interlocked.Increment(ref _connectionId);
                _connections[connectionId] = task;
                _ = task.ContinueWith(
                    static (completed, state) =>
                    {
                        var tuple = ((RuntimeServer Server, int Id))state!;
                        tuple.Server._connections.TryRemove(tuple.Id, out _);
                        _ = completed.Exception;
                    },
                    (this, connectionId),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (IOException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task HandleControlConnectionAsync(
        NamedPipeTransport transport,
        CancellationToken ct)
    {
        await using (transport.ConfigureAwait(false))
        {
            AuthenticatedSession? session = null;
            var sessionRegistered = false;
            try
            {
                session = await AuthenticateControlChannelAsync(transport, ct).ConfigureAwait(false);
                _disconnectedHostLeases.TryRemove(session.HostInstanceId, out _);
                if (!_sessions.TryAdd(session.HostInstanceId, session))
                {
                    throw new InvalidDataException("The host instance already has an authenticated connection.");
                }

                sessionRegistered = true;
                await using var repositoryConnection = await OpenDatabaseConnectionAsync(ct)
                    .ConfigureAwait(false);
                var repository = new SqliteSessionRepository(repositoryConnection);
                await using var outboxConnection = await OpenDatabaseConnectionAsync(ct)
                    .ConfigureAwait(false);
                using var outbox = CreateEventOutbox(outboxConnection);
                using var toolStateStore = new SqliteToolIntentRepository(repositoryConnection);
                var authorizationService = new ToolAuthorizationService(
                    toolStateStore,
                    RuntimeToolPolicy.CreateRestricted(WorkspaceRoot),
                    _timeProvider,
                    new BuiltinToolConsentScopeResolver());
                using var authenticated = new AuthenticatedFrameChannel(
                    transport,
                    session.SessionKey,
                    "runtime-control",
                    "host-control");
                await using var controlChannel = new FramedControlChannel(authenticated);
                session.AttachControlPeer(controlChannel);
                controlChannel.SetRequestHandler(async (request, requestCt) =>
                {
                    session.Touch(_timeProvider.GetUtcNow());
                    await session.EnterRequestAsync(requestCt).ConfigureAwait(false);
                    try
                    {
                        return await CreateControlResponseAsync(
                            request,
                            session,
                            repository,
                            outbox,
                            authorizationService,
                            toolStateStore,
                            requestCt).ConfigureAwait(false);
                    }
                    finally
                    {
                        session.ExitRequest();
                    }
                });
                controlChannel.SetBlobRequestHandler(async (frame, requestCt) =>
                {
                    session.Touch(_timeProvider.GetUtcNow());
                    await session.EnterRequestAsync(requestCt).ConfigureAwait(false);
                    try
                    {
                        return await HandleBlobFrameAsync(
                            BlobFrameProtocol.Decode(frame),
                            session,
                            requestCt).ConfigureAwait(false);
                    }
                    finally
                    {
                        session.ExitRequest();
                    }
                });
                await controlChannel.Completion.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (IOException) when (ct.IsCancellationRequested)
            {
            }
            finally
            {
                if (sessionRegistered && session is not null)
                {
                    if (_sessions.TryRemove(session.HostInstanceId, out _)
                        && !ct.IsCancellationRequested)
                    {
                        _disconnectedHostLeases[session.HostInstanceId] =
                            _timeProvider.GetUtcNow();
                    }

                    await _blobStore.AbortSessionAsync(
                        session.HostInstanceId,
                        CancellationToken.None).ConfigureAwait(false);
                    session.MarkControlDisconnected();
                    TryDisposeDisconnectedSession(session);
                }
            }
        }
    }

    private async Task HandleEventConnectionAsync(
        NamedPipeTransport transport,
        CancellationToken ct)
    {
        await using (transport.ConfigureAwait(false))
        {
            AuthenticatedSession? session = null;
            FramedEventChannel? eventChannel = null;
            var attached = false;
            try
            {
                var framedChannel = new FramedChannel(transport);
                var hello = await framedChannel.ReceiveJsonAsync(
                    RuntimeJsonContext.Default.EventChannelHello,
                    ct).ConfigureAwait(false)
                    ?? throw new EndOfStreamException("The event channel closed during authentication.");
                if (!_sessions.TryGetValue(hello.HostInstanceId, out session)
                    || !session.IsInitialized
                    || !HandshakeProtocol.VerifySessionProof(
                        session.SessionKey,
                        "event-channel",
                        session.HostInstanceId,
                        InstanceId,
                        hello.Proof))
                {
                    throw new InvalidDataException("The event channel session proof is invalid.");
                }

                await using var outboxConnection = await OpenDatabaseConnectionAsync(ct)
                    .ConfigureAwait(false);
                using var outbox = CreateEventOutbox(outboxConnection);
                eventChannel = new FramedEventChannel(transport);
                await _eventDispatchGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (!session.TryAttachEventChannel(eventChannel))
                    {
                        throw new InvalidDataException(
                            "The host instance already has an event channel.");
                    }

                    attached = true;
                    await ReplayPendingEventsAsync(session, outbox, eventChannel, ct)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _eventDispatchGate.Release();
                }

                while (!ct.IsCancellationRequested)
                {
                    var frame = await transport.ReceiveFrameAsync(ct).ConfigureAwait(false);
                    if (frame.Length == 0)
                    {
                        break;
                    }

                    throw new InvalidDataException("The event channel is Runtime-to-Host only.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (IOException) when (ct.IsCancellationRequested)
            {
            }
            finally
            {
                if (attached && session is not null && eventChannel is not null)
                {
                    session.DetachEventChannel(eventChannel);
                }
            }
        }
    }

    private async Task<AuthenticatedSession> AuthenticateControlChannelAsync(
        NamedPipeTransport transport,
        CancellationToken ct)
    {
        var channel = new FramedChannel(transport);
        var hello = await channel.ReceiveJsonAsync(
            RuntimeJsonContext.Default.HandshakeClientHello,
            ct).ConfigureAwait(false)
            ?? throw new EndOfStreamException("The control channel closed during authentication.");
        ArgumentException.ThrowIfNullOrWhiteSpace(hello.HostInstanceId);
        var nonceC = DecodeNonce(hello.NonceC);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var skew = Math.Abs(now - hello.TimestampUnixMilliseconds);
        if (hello.TimestampUnixMilliseconds <= 0
            || skew > _handshakeClockSkew.TotalMilliseconds)
        {
            throw new InvalidDataException("The client handshake timestamp is outside the accepted window.");
        }

        var nonceKey = Convert.ToBase64String(nonceC);
        PruneSeenNonces(now);
        if (!_seenClientNonces.TryAdd(nonceKey, now))
        {
            throw new InvalidDataException("The client handshake nonce was replayed.");
        }

        var nonceS = HandshakeHandler.CreateNonce();
        var runtimeResponse = HandshakeHandler.ComputeRuntimeResponse(
            _handshakeSecret,
            InstanceId,
            hello.TimestampUnixMilliseconds,
            nonceC,
            nonceS);
        await channel.SendJsonAsync(
            new HandshakeServerHello(
                InstanceId,
                Convert.ToBase64String(nonceS),
                runtimeResponse),
            ct).ConfigureAwait(false);

        var result = HandshakeHandler.Complete(
            _handshakeSecret,
            hello.HostInstanceId,
            InstanceId,
            nonceC,
            nonceS);
        var confirmation = await channel.ReceiveJsonAsync(
            RuntimeJsonContext.Default.HandshakeClientConfirmation,
            ct).ConfigureAwait(false)
            ?? throw new EndOfStreamException("The control channel closed before host confirmation.");
        if (!string.Equals(confirmation.HostInstanceId, hello.HostInstanceId, StringComparison.Ordinal)
            || !HandshakeProtocol.VerifySessionProof(
                result.SessionKey,
                "control-channel",
                result.HostInstanceId,
                result.RuntimeInstanceId,
                confirmation.Proof))
        {
            throw new InvalidDataException("The host handshake confirmation is invalid.");
        }

        return new AuthenticatedSession(
            result.HostInstanceId,
            result.SessionKey,
            transport,
            _timeProvider.GetUtcNow());
    }

    private async Task RecoverSentToolIntentsAsync(
        AuthenticatedSession session,
        SqliteSessionRepository repository,
        SqliteToolIntentRepository toolStateStore,
        SqliteEventOutbox outbox,
        ToolCatalogSnapshot toolCatalogSnapshot,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(toolStateStore);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(toolCatalogSnapshot);

        var executors = new List<IToolExecutor>();
        if (session.SupportsReverseRpc)
        {
            executors.Add(new ReverseRpcToolExecutor(
                new ReconnectableDuplexRpcPeer(
                    () => ResolveReverseRpcPeer(session.HostInstanceId)),
                _blobStore));
        }

        using var toolGateway = new ToolGateway(
            session.ToolCatalogStore,
            new JsonSchemaToolValidator(),
            new ToolAuthorizationService(
                toolStateStore,
                RuntimeToolPolicy.CreateRestricted(WorkspaceRoot),
                _timeProvider,
                new BuiltinToolConsentScopeResolver()),
            executors,
            toolStateStore,
            outbox,
            _timeProvider,
            _blobStore,
            _limits);
        var recoveryService = new RecoveryService(
            repository,
            toolStateStore,
            toolGateway,
            toolCatalogSnapshot,
            workRecoveryStore: null);
        try
        {
            await recoveryService.RecoverSentToolIntentsAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (Volatile.Read(ref _startupToolRecoveryPending) != 0)
            {
                var sentIntentsRemain = (await toolStateStore.ListRecoverableIntentsAsync(ct)
                        .ConfigureAwait(false))
                    .Any(static intent => intent.Status is ToolIntentStatus.Sent);
                if (!sentIntentsRemain)
                {
                    Volatile.Write(ref _startupToolRecoveryPending, 0);
                }
            }
        }
    }

    private async Task<JsonRpcResponse> CreateControlResponseAsync(
        JsonRpcRequest request,
        AuthenticatedSession session,
        SqliteSessionRepository repository,
        SqliteEventOutbox outbox,
        ToolAuthorizationService authorizationService,
        SqliteToolIntentRepository toolStateStore,
        CancellationToken ct)
    {
        if (!string.Equals(request.JsonRpc, "2.0", StringComparison.Ordinal))
        {
            return Error(request.Id, -32600, "The JSON-RPC version must be 2.0.");
        }

        if (string.Equals(request.Method, "initialize", StringComparison.Ordinal))
        {
            var initializeRequest = request.Params is { } parameters
                ? JsonSerializer.Deserialize(parameters, RuntimeJsonContext.Default.InitializeRequest)
                : null;
            if (initializeRequest is null
                || !string.Equals(
                    initializeRequest.HostInstanceId,
                    session.HostInstanceId,
                    StringComparison.Ordinal)
                || initializeRequest.LastConfirmedGsn < 0)
            {
                return Error(request.Id, -32602, "The initialize request is invalid.");
            }

            var protocolVersion = ProtocolVersions.Negotiate(
                initializeRequest.SupportedProtocolVersions);
            if (protocolVersion is null)
            {
                return Error(request.Id, -32001, "No supported protocol version was offered.");
            }

            await outbox.AcknowledgeAsync(initializeRequest.LastConfirmedGsn, ct)
                .ConfigureAwait(false);
            var supportsStage5 = string.Equals(
                protocolVersion,
                ProtocolVersions.Current,
                StringComparison.Ordinal);
            SkillCatalogService skillCatalog;
            try
            {
                skillCatalog = await CreateSkillCatalogAsync(initializeRequest.SkillSources, ct).ConfigureAwait(false);
            }
            catch (SkillCatalogRefreshException ex)
            {
                return Error(request.Id, -32020, $"{ex.Code}: {ex.Message}");
            }
            session.Initialize(
                initializeRequest.LastConfirmedGsn,
                protocolVersion,
                initializeRequest.ExpectedCapabilities,
                skillCatalog);
            var initializeResponse = new InitializeResponse(
                InstanceId,
                typeof(RuntimeServer).Assembly.GetName().Version?.ToString() ?? "0.1.0",
                protocolVersion,
                new RuntimeCapabilities(
                    Streaming: false,
                    ToolCalling: supportsStage5,
                    BlobTransfer: true,
                    MultiRun: true,
                    SessionResume: true,
                    ReverseRpc: supportsStage5 ? true : null,
                    ToolCatalog: supportsStage5 ? true : null,
                    ToolPermissions: supportsStage5 ? true : null,
                    Skills: CreateSkillsCapabilities()),
                CreateNegotiatedLimits(supportsStage5),
                EventPipeName,
                _heartbeatIntervalSeconds);
            var result = JsonSerializer.SerializeToElement(
                initializeResponse,
                RuntimeJsonContext.Default.InitializeResponse);
            return new JsonRpcResponse("2.0", request.Id, result);
        }

        if (!session.IsInitialized)
        {
            return Error(request.Id, -32002, "The control channel has not been initialized.");
        }

        if (string.Equals(request.Method, MessageTypes.SkillCatalogGet, StringComparison.Ordinal))
        {
            var summary = session.SkillCatalog?.GetSummary()
                ?? throw new InvalidOperationException("The Skill catalog is not initialized.");
            var result = JsonSerializer.SerializeToElement(summary, RuntimeJsonContext.Default.SkillCatalogSummary);
            return new JsonRpcResponse("2.0", request.Id, result);
        }

        if (string.Equals(request.Method, MessageTypes.SkillCatalogRefresh, StringComparison.Ordinal))
        {
            if (session.SkillCatalog is null)
            {
                return Error(request.Id, -32020, "The Skill catalog is not initialized.");
            }

            try
            {
                var summary = (await session.SkillCatalog.RefreshAsync(ct).ConfigureAwait(false)).ToContract();
                var result = JsonSerializer.SerializeToElement(summary, RuntimeJsonContext.Default.SkillCatalogSummary);
                return new JsonRpcResponse("2.0", request.Id, result);
            }
            catch (SkillCatalogRefreshException ex)
            {
                return Error(request.Id, -32020, $"{ex.Code}: {ex.Message}");
            }
        }

        if (string.Equals(request.Method, MessageTypes.ToolCatalogReplace, StringComparison.Ordinal))
        {
            if (!session.SupportsToolCatalog)
            {
                return Error(request.Id, -32006, "Tool Catalog requires protocol 1.1 capability negotiation.");
            }

            var parameters = request.Params is { } catalogParameters
                ? JsonSerializer.Deserialize(
                    catalogParameters,
                    RuntimeJsonContext.Default.ToolCatalogReplaceRequest)
                : null;
            if (parameters is null)
            {
                return Error(request.Id, -32602, "The tool.catalog.replace parameters are invalid.");
            }

            var snapshot = session.ToolCatalogStore.ReplaceHostCatalog(parameters);
            await RecoverSentToolIntentsAsync(
                session,
                repository,
                toolStateStore,
                outbox,
                snapshot,
                ct).ConfigureAwait(false);
            var result = JsonSerializer.SerializeToElement(
                new ToolCatalogUpdateResponse(
                    snapshot.HostCatalogVersion,
                    snapshot.EffectiveVersion,
                    snapshot.Tools.Count),
                RuntimeJsonContext.Default.ToolCatalogUpdateResponse);
            return new JsonRpcResponse("2.0", request.Id, result);
        }

        if (string.Equals(request.Method, MessageTypes.ToolCatalogPatch, StringComparison.Ordinal))
        {
            if (!session.SupportsToolCatalog)
            {
                return Error(request.Id, -32006, "Tool Catalog requires protocol 1.1 capability negotiation.");
            }

            var parameters = request.Params is { } catalogParameters
                ? JsonSerializer.Deserialize(
                    catalogParameters,
                    RuntimeJsonContext.Default.ToolCatalogPatchRequest)
                : null;
            if (parameters is null)
            {
                return Error(request.Id, -32602, "The tool.catalog.patch parameters are invalid.");
            }

            var snapshot = session.ToolCatalogStore.PatchHostCatalog(parameters);
            await RecoverSentToolIntentsAsync(
                session,
                repository,
                toolStateStore,
                outbox,
                snapshot,
                ct).ConfigureAwait(false);
            var result = JsonSerializer.SerializeToElement(
                new ToolCatalogUpdateResponse(
                    snapshot.HostCatalogVersion,
                    snapshot.EffectiveVersion,
                    snapshot.Tools.Count),
                RuntimeJsonContext.Default.ToolCatalogUpdateResponse);
            return new JsonRpcResponse("2.0", request.Id, result);
        }

        if (string.Equals(request.Method, MessageTypes.GrantRevoke, StringComparison.Ordinal))
        {
            if (!session.SupportsToolPermissions)
            {
                return Error(request.Id, -32006, "Grant revocation requires protocol 1.1 capability negotiation.");
            }

            var parameters = request.Params is { } revokeParameters
                ? JsonSerializer.Deserialize(
                    revokeParameters,
                    RuntimeJsonContext.Default.GrantRevokeRequest)
                : null;
            if (parameters is null || string.IsNullOrWhiteSpace(parameters.GrantId))
            {
                return Error(request.Id, -32602, "The grant.revoke parameters are invalid.");
            }

            var revokedCount = await authorizationService.RevokeGrantAsync(
                parameters.GrantId,
                parameters.RevokeDescendants,
                parameters.Reason,
                ct).ConfigureAwait(false);
            var result = JsonSerializer.SerializeToElement(
                new GrantRevokeResponse(parameters.GrantId, revokedCount),
                RuntimeJsonContext.Default.GrantRevokeResponse);
            return new JsonRpcResponse("2.0", request.Id, result);
        }

        if (string.Equals(request.Method, MessageTypes.NewSessionRun, StringComparison.Ordinal))
        {
            if (Volatile.Read(ref _startupToolRecoveryPending) != 0)
            {
                return Error(
                    request.Id,
                    -32014,
                    "Startup tool recovery is incomplete; publish the Host Tool Catalog before starting a Run.");
            }

            return await CreateNewSessionRunResponseAsync(
                request,
                session,
                repository,
                outbox,
                ct).ConfigureAwait(false);
        }

        if (string.Equals(request.Method, MessageTypes.ExistingSessionRun, StringComparison.Ordinal))
        {
            if (Volatile.Read(ref _startupToolRecoveryPending) != 0)
            {
                return Error(
                    request.Id,
                    -32014,
                    "Startup tool recovery is incomplete; publish the Host Tool Catalog before starting a Run.");
            }

            return await CreateExistingSessionRunResponseAsync(
                request,
                session,
                repository,
                outbox,
                ct).ConfigureAwait(false);
        }

        if (string.Equals(request.Method, MessageTypes.RunQuery, StringComparison.Ordinal))
        {
            var parameters = request.Params is { } queryParameters
                ? JsonSerializer.Deserialize(
                    queryParameters,
                    RuntimeJsonContext.Default.RunQueryParameters)
                : null;
            if (parameters is null || string.IsNullOrWhiteSpace(parameters.RunId))
            {
                return Error(request.Id, -32602, "The run.query parameters are invalid.");
            }

            var snapshot = await repository.GetRunSnapshotAsync(parameters.RunId, ct)
                .ConfigureAwait(false);
            if (snapshot is null)
            {
                return Error(request.Id, -32004, $"Run '{parameters.RunId}' was not found.");
            }

            var result = JsonSerializer.SerializeToElement(
                CreateRunQueryResult(
                    snapshot,
                    GetQueryableRunStatus(snapshot)),
                RuntimeJsonContext.Default.RunQueryResult);
            return new JsonRpcResponse("2.0", request.Id, result);
        }

        if (string.Equals(request.Method, MessageTypes.RuntimeStatus, StringComparison.Ordinal))
        {
            var parameters = request.Params is { } statusParameters
                ? JsonSerializer.Deserialize(
                    statusParameters,
                    RuntimeJsonContext.Default.RuntimeStatusParameters)
                : null;
            if (parameters is null)
            {
                return Error(request.Id, -32602, "The runtime.status parameters are invalid.");
            }

            var result = JsonSerializer.SerializeToElement(
                new RuntimeStatusResult(
                    InstanceId,
                    typeof(RuntimeServer).Assembly.GetName().Version?.ToString() ?? "0.1.0",
                    ProtocolVersions.Current,
                    WorkspaceRoot,
                    Environment.ProcessId,
                    _startedAt,
                    _activeRuns.Count,
                    _sessions.Count,
                    ConnectedEventChannelCount),
                RuntimeJsonContext.Default.RuntimeStatusResult);
            return new JsonRpcResponse("2.0", request.Id, result);
        }

        if (string.Equals(request.Method, MessageTypes.RunList, StringComparison.Ordinal))
        {
            var parameters = request.Params is { } listParameters
                ? JsonSerializer.Deserialize(
                    listParameters,
                    RuntimeJsonContext.Default.RunListParameters)
                : null;
            if (parameters is null
                || (parameters.SessionId is not null
                    && string.IsNullOrWhiteSpace(parameters.SessionId)))
            {
                return Error(request.Id, -32602, "The run.list parameters are invalid.");
            }

            var activeRuns = new List<RunListItem>();
            var snapshots = await repository.ListRunSnapshotsAsync(
                parameters.SessionId,
                nonTerminalOnly: true,
                ct).ConfigureAwait(false);
            foreach (var snapshot in snapshots)
            {
                var status = GetQueryableRunStatus(snapshot);
                activeRuns.Add(new RunListItem(
                    snapshot.RunId,
                    snapshot.SessionId,
                    status,
                    snapshot.CreatedAt ?? snapshot.UpdatedAt ?? DateTimeOffset.UtcNow)
                {
                    Mode = snapshot.Mode,
                    Phase = snapshot.Phase,
                    CurrentStep = snapshot.CurrentStep,
                    CompletedUnits = snapshot.CompletedUnits,
                    TotalUnits = snapshot.TotalUnits,
                    UpdatedAt = snapshot.UpdatedAt,
                    LastRunSequence = snapshot.LastRunSequence,
                    Error = DeserializeRuntimeError(snapshot.ErrorJson)
                });
            }

            var result = JsonSerializer.SerializeToElement(
                new RunListResult([.. activeRuns]),
                RuntimeJsonContext.Default.RunListResult);
            return new JsonRpcResponse("2.0", request.Id, result);
        }

        if (string.Equals(request.Method, MessageTypes.SessionList, StringComparison.Ordinal))
        {
            return await CreateSessionListResponseAsync(request, repository, ct)
                .ConfigureAwait(false);
        }

        if (string.Equals(request.Method, MessageTypes.SessionGet, StringComparison.Ordinal))
        {
            return await CreateSessionGetResponseAsync(request, repository, ct)
                .ConfigureAwait(false);
        }

        if (string.Equals(request.Method, MessageTypes.SessionResume, StringComparison.Ordinal))
        {
            return await CreateSessionResumeResponseAsync(request, repository, ct)
                .ConfigureAwait(false);
        }

        if (string.Equals(request.Method, MessageTypes.SessionMessagesList, StringComparison.Ordinal))
        {
            return await CreateSessionMessagesResponseAsync(request, repository, ct)
                .ConfigureAwait(false);
        }

        if (string.Equals(request.Method, MessageTypes.SessionHistoryRead, StringComparison.Ordinal))
        {
            return await CreateSessionHistoryReadResponseAsync(request, repository, ct)
                .ConfigureAwait(false);
        }

        if (string.Equals(request.Method, MessageTypes.SessionSelectionUpdate, StringComparison.Ordinal))
        {
            return await CreateSessionSelectionUpdateResponseAsync(request, repository, ct)
                .ConfigureAwait(false);
        }

        if (string.Equals(request.Method, MessageTypes.SessionCompaction, StringComparison.Ordinal))
        {
            return await CreateSessionCompactionResponseAsync(request, ct)
                .ConfigureAwait(false);
        }

        if (string.Equals(request.Method, MessageTypes.SessionRehydrate, StringComparison.Ordinal))
        {
            return await CreateSessionRehydrateResponseAsync(
                request,
                session,
                repository,
                outbox,
                ct)
                .ConfigureAwait(false);
        }

        if (string.Equals(request.Method, "events.acknowledge", StringComparison.Ordinal))
        {
            if (request.Params is not { } acknowledgeParameters
                || acknowledgeParameters.ValueKind is not JsonValueKind.Object
                || !acknowledgeParameters.TryGetProperty(
                    "lastConfirmedGsn",
                    out var lastConfirmedProperty)
                || !lastConfirmedProperty.TryGetInt64(out var lastConfirmedGsn)
                || lastConfirmedGsn < 0)
            {
                return Error(request.Id, -32602, "The acknowledgement parameters are invalid.");
            }

            await outbox.AcknowledgeAsync(lastConfirmedGsn, ct).ConfigureAwait(false);
            session.Confirm(lastConfirmedGsn);
            var result = JsonSerializer.SerializeToElement(true, RuntimeJsonContext.Default.Boolean);
            return new JsonRpcResponse("2.0", request.Id, result);
        }

        if (string.Equals(request.Method, "heartbeat", StringComparison.Ordinal))
        {
            var result = JsonSerializer.SerializeToElement(
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                RuntimeJsonContext.Default.Int64);
            return new JsonRpcResponse("2.0", request.Id, result);
        }

        if (string.Equals(request.Method, MessageTypes.RunCancel, StringComparison.Ordinal))
        {
            var parameters = request.Params is { } cancelParameters
                ? JsonSerializer.Deserialize(
                    cancelParameters,
                    RuntimeJsonContext.Default.RunCancelParameters)
                : null;
            if (parameters is null || string.IsNullOrWhiteSpace(parameters.RunId))
            {
                return Error(request.Id, -32602, "The run.cancel parameters are invalid.");
            }

            var cancelAccepted = _activeRuns.TryGetValue(parameters.RunId, out var activeRun)
                && activeRun.Cancel();
            var result = JsonSerializer.SerializeToElement(
                cancelAccepted,
                RuntimeJsonContext.Default.Boolean);
            return new JsonRpcResponse("2.0", request.Id, result);
        }

        if (string.Equals(request.Method, MessageTypes.CredentialsUpdate, StringComparison.Ordinal))
        {
            var parameters = request.Params is { } credentialParameters
                ? JsonSerializer.Deserialize(
                    credentialParameters,
                    RuntimeJsonContext.Default.CredentialsUpdateParameters)
                : null;
            if (parameters is null
                || string.IsNullOrWhiteSpace(parameters.RunId)
                || string.IsNullOrWhiteSpace(parameters.ProviderId)
                || string.IsNullOrWhiteSpace(parameters.Credential))
            {
                return Error(request.Id, -32602, "The credentials.update parameters are invalid.");
            }

            if (!_activeRuns.TryGetValue(parameters.RunId, out var credentialRun))
            {
                return Error(request.Id, -32004, "The Run is not active in this Runtime instance.");
            }

            // Keep host Registry in sync so subsequent Runs resolve the rotated key.
            _hostProviderRegistry?.TryUpdateCredential(parameters.ProviderId, parameters.Credential);

            if (!await credentialRun.UpdateCredentialsAsync(parameters, ct).ConfigureAwait(false))
            {
                return Error(
                    request.Id,
                    -32005,
                    "The selected Provider cannot accept credential updates or is not waiting for credentials.");
            }

            var result = JsonSerializer.SerializeToElement(true, RuntimeJsonContext.Default.Boolean);
            return new JsonRpcResponse("2.0", request.Id, result);
        }

        if (string.Equals(request.Method, MessageTypes.ProvidersUpsert, StringComparison.Ordinal))
        {
            return CreateProvidersUpsertResponse(request);
        }

        return Error(request.Id, -32601, $"Unknown method '{request.Method}'.");
    }

    private async Task<SkillCatalogService> CreateSkillCatalogAsync(
        IReadOnlyList<SkillSourceDescriptor>? sources,
        CancellationToken ct)
    {
        var catalog = new SkillCatalogService(
            WorkspaceRoot,
            _dataDirectory,
            InstanceId,
            sources ?? []);
        try
        {
            await catalog.InitializeAsync(ct).ConfigureAwait(false);
            return catalog;
        }
        catch
        {
            await catalog.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<byte[]> HandleBlobFrameAsync(
        BlobFrame frame,
        AuthenticatedSession session,
        CancellationToken ct)
    {
        try
        {
            switch (frame.Operation)
            {
                case BlobFrameOperation.Begin:
                    await _blobStore.BeginAsync(
                        BlobFrameProtocol.DeserializeMetadata(
                            frame,
                            RuntimeJsonContext.Default.BlobBeginRequest),
                        session.HostInstanceId,
                        ct).ConfigureAwait(false);
                    return EncodeBlobResponse(frame.Operation, null, frame: default);

                case BlobFrameOperation.Chunk:
                    await _blobStore.AppendChunkAsync(
                        BlobFrameProtocol.DeserializeMetadata(
                            frame,
                            RuntimeJsonContext.Default.BlobChunkRequest),
                        frame.Data,
                        session.HostInstanceId,
                        ct).ConfigureAwait(false);
                    return EncodeBlobResponse(frame.Operation, null, frame: default);

                case BlobFrameOperation.Complete:
                    var completed = await _blobStore.CompleteAsync(
                        BlobFrameProtocol.DeserializeMetadata(
                            frame,
                            RuntimeJsonContext.Default.BlobCompleteRequest),
                        session.HostInstanceId,
                        ct).ConfigureAwait(false);
                    return EncodeBlobResponse(
                        frame.Operation,
                        new BlobOperationResponse(true, null, completed, completed.Length, true),
                        frame: default);

                case BlobFrameOperation.Read:
                    var readRequest = BlobFrameProtocol.DeserializeMetadata(
                        frame,
                        RuntimeJsonContext.Default.BlobReadRequest);
                    if (readRequest.MaxBytes == 0)
                    {
                        var reference = await _blobStore.GetReferenceAsync(
                            readRequest,
                            session.HostInstanceId,
                            ct).ConfigureAwait(false);
                        return EncodeBlobResponse(
                            frame.Operation,
                            new BlobOperationResponse(true, null, reference, readRequest.Offset, false),
                            frame: default);
                    }

                    var chunk = await _blobStore.ReadChunkAsync(
                        readRequest,
                        session.HostInstanceId,
                        ct).ConfigureAwait(false);
                    return EncodeBlobResponse(
                        frame.Operation,
                        new BlobOperationResponse(
                            true,
                            null,
                            chunk.Reference,
                            chunk.Offset,
                            chunk.EndOfBlob),
                        chunk.Data);

                case BlobFrameOperation.Abort:
                    var abort = BlobFrameProtocol.DeserializeMetadata(
                        frame,
                        RuntimeJsonContext.Default.BlobChunkRequest);
                    await _blobStore.AbortUploadAsync(
                        abort.UploadId,
                        session.HostInstanceId,
                        ct).ConfigureAwait(false);
                    return EncodeBlobResponse(frame.Operation, null, frame: default);

                default:
                    throw new InvalidDataException("The Blob operation is not supported.");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException
            or InvalidOperationException
            or ArgumentException
            or FileNotFoundException
            or UnauthorizedAccessException)
        {
            return EncodeBlobResponse(
                frame.Operation,
                new BlobOperationResponse(false, ex.Message, null, 0, true),
                frame: default);
        }
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

    /// <summary>
    /// 用本轮用户输入首段文本写入会话临时标题（仅 title 为空时生效）。
    /// 覆盖 new_session 与 existing_session，避免历史列表只能看到 SessionId。
    /// </summary>
    private static async Task TryEnsureInitialSessionTitleAsync(
        SqliteSessionRepository repository,
        string sessionId,
        ContentBlock[]? initialInput,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || initialInput is null || initialInput.Length == 0)
        {
            return;
        }

        var titleText = initialInput
            .OfType<TextContentBlock>()
            .Select(static block => block.Text)
            .FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text));
        if (string.IsNullOrWhiteSpace(titleText))
        {
            return;
        }

        var title = FlattenSessionTitle(titleText);
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        await repository.TrySetInitialSessionTitleAsync(sessionId, title, ct).ConfigureAwait(false);
    }

    private static byte[] EncodeBlobResponse(
        BlobFrameOperation operation,
        BlobOperationResponse? response,
        ReadOnlyMemory<byte> frame = default) =>
        BlobFrameProtocol.Encode(
            operation,
            response ?? new BlobOperationResponse(true, null, null, 0, true),
            RuntimeJsonContext.Default.BlobOperationResponse,
            frame,
            isResponse: true);

    private async Task<JsonRpcResponse> CreateSessionGetResponseAsync(
        JsonRpcRequest request,
        SqliteSessionRepository repository,
        CancellationToken ct)
    {
        var parameters = request.Params is { } json
            ? json.Deserialize(RuntimeJsonContext.Default.SessionIdParameters)
            : null;
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.SessionId))
        {
            return Error(request.Id, -32602, "The session.get parameters are invalid.");
        }

        var snapshot = await repository.GetSessionSnapshotAsync(parameters.SessionId, ct)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return Error(request.Id, -32004, $"Session '{parameters.SessionId}' was not found.");
        }

        var diagnostics = CreateRecoveryDiagnostics(snapshot);
        var selection = DeserializePersistedSelection(snapshot, diagnostics);
        var result = new SessionGetResult(
            snapshot.SessionId,
            snapshot.Mode,
            snapshot.Status,
            snapshot.CreatedAt,
            snapshot.UpdatedAt,
            selection,
            CreateRunQueryResult(
                snapshot.LatestRun,
                snapshot.LatestRun is null ? null : GetQueryableRunStatus(snapshot.LatestRun)),
            IsFullyRecoverable: diagnostics.Count == 0,
            [.. diagnostics]);
        var payload = JsonSerializer.SerializeToElement(
            result,
            RuntimeJsonContext.Default.SessionGetResult);
        return new JsonRpcResponse("2.0", request.Id, payload);
    }

    private static async Task<JsonRpcResponse> CreateSessionListResponseAsync(
        JsonRpcRequest request,
        SqliteSessionRepository repository,
        CancellationToken ct)
    {
        try
        {
            var parameters = request.Params is { } json
                ? json.Deserialize(RuntimeJsonContext.Default.SessionListParameters)
                : null;
            if (parameters is null)
            {
                return Error(request.Id, -32602, "The session.list parameters are invalid.");
            }

            if ((parameters.Mode is { } mode && !Enum.IsDefined(mode))
                || (parameters.Status is { } status && !Enum.IsDefined(status)))
            {
                return Error(request.Id, -32602, "The session.list parameters are invalid.");
            }

            if (parameters.Limit < 1 || parameters.Limit > 200)
            {
                return Error(request.Id, -32602, "The session.list parameters are invalid.");
            }

            if (parameters.Cursor is not null && string.IsNullOrWhiteSpace(parameters.Cursor))
            {
                return Error(request.Id, -32602, "The session.list parameters are invalid.");
            }

            var query = new SessionListQuery(
                parameters.Mode,
                parameters.Status,
                parameters.Since,
                parameters.Search,
                parameters.Limit + 1,
                parameters.Cursor);
            var descriptors = await repository.ListSessionsAsync(query, ct)
                .ConfigureAwait(false);

            string? nextCursor = null;
            IReadOnlyList<SessionDescriptor> items = descriptors;
            if (descriptors.Count > parameters.Limit)
            {
                items = descriptors.Take(parameters.Limit).ToList();
                var last = items[items.Count - 1];
                nextCursor = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:O}|{1}",
                    last.UpdatedAt,
                    last.SessionId);
            }

            var resultItems = items
                .Select(d => new SessionListItem(
                    d.SessionId, d.Mode, d.Status, d.UpdatedAt, d.Title))
                .ToArray();
            var result = new SessionListResult(resultItems, nextCursor);
            var payload = JsonSerializer.SerializeToElement(
                result,
                RuntimeJsonContext.Default.SessionListResult);
            return new JsonRpcResponse("2.0", request.Id, payload);
        }
        catch (JsonException)
        {
            return Error(request.Id, -32602, "The session.list parameters are invalid.");
        }
        catch (FormatException)
        {
            return Error(request.Id, -32602, "The session.list parameters are invalid.");
        }
        catch (ArgumentException)
        {
            return Error(request.Id, -32602, "The session.list parameters are invalid.");
        }
    }

    private async Task<JsonRpcResponse> CreateSessionResumeResponseAsync(
        JsonRpcRequest request,
        SqliteSessionRepository repository,
        CancellationToken ct)
    {
        var parameters = request.Params is { } json
            ? json.Deserialize(RuntimeJsonContext.Default.SessionIdParameters)
            : null;
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.SessionId))
        {
            return Error(request.Id, -32602, "The session.resume parameters are invalid.");
        }

        var snapshot = await repository.GetSessionSnapshotAsync(parameters.SessionId, ct)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return Error(request.Id, -32004, $"Session '{parameters.SessionId}' was not found.");
        }

        var diagnostics = CreateRecoveryDiagnostics(snapshot);
        var selection = DeserializePersistedSelection(snapshot, diagnostics);
        var neededDefinitions = _sessionSelections.ContainsKey(snapshot.SessionId)
            ? []
            : CreateNeededDefinitions(snapshot, selection);
        var historyMeta = await TryGetConversationHistoryMetadataAsync(
                snapshot.SessionId,
                diagnostics,
                ct)
            .ConfigureAwait(false);
        await ValidateResumeBlobReferencesAsync(historyMeta.BlobIds, diagnostics, ct)
            .ConfigureAwait(false);

        var result = new SessionResumeResult(
            snapshot.SessionId,
            snapshot.Mode,
            snapshot.SelectionVersion,
            selection,
            neededDefinitions,
            CreateRunQueryResult(
                snapshot.LatestRun,
                snapshot.LatestRun is null ? null : GetQueryableRunStatus(snapshot.LatestRun)),
            snapshot.LastGsn,
            LastCheckpoint: null,
            IsFullyRecoverable: diagnostics.Count == 0,
            [.. diagnostics],
            historyMeta.MessageCount,
            historyMeta.LastMessageSeq,
            historyMeta.LastMessageId,
            snapshot.AgentSnapshots,
            historyMeta.BlobReferenceCount,
            historyMeta.BlobIds);
        var payload = JsonSerializer.SerializeToElement(
            result,
            RuntimeJsonContext.Default.SessionResumeResult);
        return new JsonRpcResponse("2.0", request.Id, payload);
    }

    private static async Task<JsonRpcResponse> CreateSessionMessagesResponseAsync(
        JsonRpcRequest request,
        SqliteSessionRepository repository,
        CancellationToken ct)
    {
        var parameters = request.Params is { } json
            ? json.Deserialize(RuntimeJsonContext.Default.SessionMessagesListParameters)
            : null;
        if (parameters is null
            || string.IsNullOrWhiteSpace(parameters.SessionId)
            || parameters.PageSize is <= 0 or > 500
            || parameters.Cursor is < 0)
        {
            return Error(request.Id, -32602, "The session.messages.list parameters are invalid.");
        }

        IReadOnlyList<MessageIndexEntry> entries;
        try
        {
            entries = await repository.ListMessageIndexAsync(
                parameters.SessionId,
                parameters.Cursor,
                parameters.PageSize + 1,
                ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return Error(request.Id, -32004, $"Session '{parameters.SessionId}' was not found.");
        }

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
        var result = new SessionMessagesListResult(
            messages,
            hasMore ? messages[^1].Sequence : null);
        var payload = JsonSerializer.SerializeToElement(
            result,
            RuntimeJsonContext.Default.SessionMessagesListResult);
        return new JsonRpcResponse("2.0", request.Id, payload);
    }

    private async Task<JsonRpcResponse> CreateSessionHistoryReadResponseAsync(
        JsonRpcRequest request,
        SqliteSessionRepository repository,
        CancellationToken ct)
    {
        var parameters = request.Params is { } json
            ? json.Deserialize(RuntimeJsonContext.Default.SessionHistoryReadParameters)
            : null;
        if (parameters is null
            || string.IsNullOrWhiteSpace(parameters.SessionId)
            || parameters.PageSize is <= 0 or > 500
            || parameters.Cursor is < 0
            || !LocalRuntime.TryParseBlobMode(parameters.BlobMode, out var blobMode))
        {
            return Error(request.Id, -32602, "The session.history.read parameters are invalid.");
        }

        var snapshot = await repository.GetSessionSnapshotAsync(parameters.SessionId, ct)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return Error(request.Id, -32004, $"Session '{parameters.SessionId}' was not found.");
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
            .Select(record => LocalRuntime.MapHistoryMessage(
                record,
                parameters.IncludeReasoning,
                parameters.IncludeToolCalls))
            .ToArray();
        var result = new SessionHistoryReadResult(
            messages,
            hasMore && messages.Length > 0 ? messages[^1].Sequence : null);
        var payload = JsonSerializer.SerializeToElement(
            result,
            RuntimeJsonContext.Default.SessionHistoryReadResult);
        return new JsonRpcResponse("2.0", request.Id, payload);
    }

    private async Task<JsonRpcResponse> CreateSessionCompactionResponseAsync(
        JsonRpcRequest request,
        CancellationToken ct)
    {
        var parameters = request.Params is { } json
            ? json.Deserialize(RuntimeJsonContext.Default.SessionCompactionRequest)
            : null;
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.SessionId))
        {
            return Error(request.Id, -32602, "The session.compact parameters are invalid.");
        }

        var strategy = parameters.Strategy.Trim().ToLowerInvariant() switch
        {
            "summary" => SessionCompactionStrategy.Summary,
            "full" => SessionCompactionStrategy.Full,
            "sliding-window" => SessionCompactionStrategy.SlidingWindow,
            _ => (SessionCompactionStrategy?)null
        };
        if (strategy is null)
        {
            return Error(request.Id, -32602, "Strategy must be full, sliding-window, or summary.");
        }

        try
        {
            var result = await _compactionService.CompactAsync(
                    parameters.SessionId,
                    new SessionCompactionOptions(
                        strategy.Value,
                        parameters.ProviderId,
                        parameters.ModelId,
                        parameters.KeepLastTokens,
                        parameters.DryRun,
                        parameters.Force,
                        parameters.SelectionSource),
                    ct)
                .ConfigureAwait(false);
            if (result is null)
            {
                return Error(request.Id, -32004, $"Session '{parameters.SessionId}' was not found.");
            }

            var dto = new SessionCompactionResultDto(
                result.SessionId,
                ToCompactionWireValue(result.Strategy),
                result.ProviderId,
                result.ModelId,
                result.KeepLastTokens,
                result.SourceMessageCount,
                result.ProjectedMessageCount,
                result.DroppedMessageCount,
                result.BeforeEstimatedTokens,
                result.AfterEstimatedTokens,
                result.EstimateSource,
                result.DryRun,
                result.CacheHit,
                result.CacheWritten,
                result.CacheMissReason,
                result.SelectionSource,
                result.SummaryGenerated);
            var payload = JsonSerializer.SerializeToElement(
                dto,
                RuntimeJsonContext.Default.SessionCompactionResultDto);
            return new JsonRpcResponse("2.0", request.Id, payload);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(request.Id, -32010, $"InvalidCompactionSelection: {ex.Message}");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            return Error(
                request.Id,
                -32020,
                $"SessionCompactionFailed: stage=projection;sessionId={parameters.SessionId};reason={ex.Message}");
        }
    }

    private static string ToCompactionWireValue(SessionCompactionStrategy strategy) => strategy switch
    {
        SessionCompactionStrategy.Full => "full",
        SessionCompactionStrategy.SlidingWindow => "sliding-window",
        SessionCompactionStrategy.Summary => "summary",
        _ => throw new ArgumentOutOfRangeException(nameof(strategy))
    };
    private async Task<JsonRpcResponse> CreateSessionSelectionUpdateResponseAsync(
        JsonRpcRequest request,
        SqliteSessionRepository repository,
        CancellationToken ct)
    {
        var parameters = request.Params is { } json
            ? json.Deserialize(RuntimeJsonContext.Default.SessionSelectionUpdateParameters)
            : null;
        if (parameters is null
            || string.IsNullOrWhiteSpace(parameters.SessionId)
            || parameters.ExpectedSelectionVersion < 0
            || parameters.Selection is null
            || parameters.Selection.SelectionVersion <= parameters.ExpectedSelectionVersion)
        {
            return Error(request.Id, -32602, "The session.selection.update parameters are invalid.");
        }

        var upsertError = TryUpsertProviderConfigs(parameters.ProviderConfigs);
        if (upsertError is not null)
        {
            return Error(request.Id, -32602, upsertError);
        }

        var snapshot = await repository.GetSessionSnapshotAsync(parameters.SessionId, ct)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return Error(request.Id, -32004, $"Session '{parameters.SessionId}' was not found.");
        }

        if (snapshot.SelectionVersion is null || snapshot.SelectionJson is null)
        {
            return Error(
                request.Id,
                -32602,
                "The session has no persisted selection to patch.");
        }

        var selection = parameters.Selection;
        if (selection.Mode != snapshot.Mode)
        {
            return Error(request.Id, -32602, "A Session's mode cannot be changed.");
        }

        if (_hostProviderRegistry is not null)
        {
            var hostError = HostModeSelectionValidator.ValidateForRun(
                selection,
                snapshot.Mode,
                _hostProviderRegistry);
            if (hostError is not null)
            {
                return Error(request.Id, -32602, hostError);
            }
        }

        var persistedSelection = RuntimeRunMetadata.RemovePromptContent(selection);
        var newSelectionJson = JsonSerializer.Serialize(
            persistedSelection,
            RuntimeJsonContext.Default.NextTurnSelection);
        var agentSnapshots = RuntimeRunMetadata.CreateAgentSnapshots(selection);

        var saved = await repository.TrySaveSessionSelectionAsync(
            parameters.SessionId,
            parameters.ExpectedSelectionVersion,
            selection.SelectionVersion,
            newSelectionJson,
            agentSnapshots,
            ct).ConfigureAwait(false);

        if (!saved)
        {
            return Error(
                request.Id,
                -32006,
                $"Selection version {parameters.ExpectedSelectionVersion} is no longer current.");
        }

        _sessionSelections[parameters.SessionId] = selection;
        var payload = JsonSerializer.SerializeToElement(
            persistedSelection,
            RuntimeJsonContext.Default.NextTurnSelection);
        return new JsonRpcResponse("2.0", request.Id, payload);
    }

    private async Task<JsonRpcResponse> CreateSessionRehydrateResponseAsync(
        JsonRpcRequest request,
        AuthenticatedSession session,
        SqliteSessionRepository repository,
        SqliteEventOutbox outbox,
        CancellationToken ct)
    {
        var parameters = request.Params is { } json
            ? json.Deserialize(RuntimeJsonContext.Default.SessionRehydrateParameters)
            : null;
        if (parameters is null
            || string.IsNullOrWhiteSpace(parameters.SessionId)
            || parameters.AgentDefinitions is null)
        {
            return Error(request.Id, -32602, "The session.rehydrate parameters are invalid.");
        }

        var duplicateDefinition = parameters.AgentDefinitions
            .GroupBy(static definition => definition.AgentRef.AgentId, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateDefinition is not null)
        {
            return Error(request.Id, -32602, "Agent definitions must have unique Agent IDs.");
        }

        var snapshot = await repository.GetSessionSnapshotAsync(parameters.SessionId, ct)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return Error(request.Id, -32004, $"Session '{parameters.SessionId}' was not found.");
        }

        var diagnostics = new List<string>();
        var persistedSelection = DeserializePersistedSelection(snapshot, diagnostics);
        if (persistedSelection is null || snapshot.AgentSnapshots.Length == 0)
        {
            var failed = new SessionRehydrateResult(
                snapshot.SessionId,
                "Failed",
                snapshot.AgentSnapshots.Select(static agent => agent.AgentId).ToArray());
            var failedPayload = JsonSerializer.SerializeToElement(
                failed,
                RuntimeJsonContext.Default.SessionRehydrateResult);
            return new JsonRpcResponse("2.0", request.Id, failedPayload);
        }

        var definitions = parameters.AgentDefinitions.ToDictionary(
            static definition => definition.AgentRef.AgentId,
            StringComparer.Ordinal);
        var mismatched = new List<string>();
        var missing = false;
        foreach (var agent in snapshot.AgentSnapshots)
        {
            if (!definitions.TryGetValue(agent.AgentId, out var definition))
            {
                missing = true;
                continue;
            }

            if (!string.Equals(
                    definition.AgentRef.PromptTemplateVersion,
                    agent.PromptTemplateVersion,
                    StringComparison.Ordinal)
                || !string.Equals(
                    RuntimeRunMetadata.ComputePromptHash(definition.AgentRef.SystemPrompt),
                    agent.PromptHash,
                    StringComparison.Ordinal))
            {
                mismatched.Add(agent.AgentId);
            }
        }

        var status = mismatched.Count > 0
            ? "Failed"
            : missing
                ? "PartiallyReady"
                : "Ready";
        if (string.Equals(status, "Ready", StringComparison.Ordinal))
        {
            _sessionSelections[snapshot.SessionId] = RehydrateSelection(
                persistedSelection,
                definitions);
        }

        var result = new SessionRehydrateResult(snapshot.SessionId, status, [.. mismatched]);
        var payload = JsonSerializer.SerializeToElement(
            result,
            RuntimeJsonContext.Default.SessionRehydrateResult);
        return new JsonRpcResponse("2.0", request.Id, payload);
    }

    private async Task<JsonRpcResponse> CreateNewSessionRunResponseAsync(
        JsonRpcRequest request,
        AuthenticatedSession session,
        SqliteSessionRepository repository,
        SqliteEventOutbox outbox,
        CancellationToken ct)
    {
        var parameters = request.Params
            ?? throw new InvalidDataException("run.new_session requires request parameters.");
        var runRequest = parameters.Deserialize(
            RuntimeJsonContext.Default.NewSessionRunRequest)
            ?? throw new InvalidDataException("run.new_session parameters were empty.");
        if (runRequest.Mode != runRequest.Selection.Mode)
        {
            return Error(request.Id, -32602, "The Run mode and Selection mode must match.");
        }

        var upsertError = TryUpsertProviderConfigs(runRequest.ProviderConfigs);
        if (upsertError is not null)
        {
            return Error(request.Id, -32602, upsertError);
        }

        switch (runRequest.Mode)
        {
            case RuntimeMode.Expert:
                if (runRequest.Selection.ModeOptions is not ExpertModeOptions)
                {
                    return Error(request.Id, -32602, "Expert mode requires ExpertModeOptions.");
                }
                break;
            default:
                return Error(request.Id, -32602, $"Run mode '{runRequest.Mode}' is not supported.");
        }

        if (_hostProviderRegistry is not null)
        {
            var hostError = HostModeSelectionValidator.ValidateForRun(
                runRequest.Selection,
                runRequest.Mode,
                _hostProviderRegistry);
            if (hostError is not null)
            {
                return Error(request.Id, -32602, hostError);
            }
        }

        var plan = new RunStartPlan(
            $"new:{runRequest.SessionIdempotencyKey}",
            RuntimeRunMetadata.ComputeRequestHash(runRequest),
            runRequest,
            ExistingSessionId: null,
            runRequest.SessionIdempotencyKey,
            runRequest.Selection);
        return await StartRunResponseAsync(
            request,
            session,
            repository,
            outbox,
            plan,
            ct).ConfigureAwait(false);
    }

    private async Task<JsonRpcResponse> CreateExistingSessionRunResponseAsync(
        JsonRpcRequest request,
        AuthenticatedSession session,
        SqliteSessionRepository repository,
        SqliteEventOutbox outbox,
        CancellationToken ct)
    {
        var parameters = request.Params
            ?? throw new InvalidDataException("run.existing_session requires request parameters.");
        var existingRun = parameters.Deserialize(
            RuntimeJsonContext.Default.ExistingSessionRunRequest)
            ?? throw new InvalidDataException("run.existing_session parameters were empty.");
        if (string.IsNullOrWhiteSpace(existingRun.SessionId)
            || string.IsNullOrWhiteSpace(existingRun.RunIdempotencyKey)
            || existingRun.InputOverride is not { Length: > 0 })
        {
            return Error(request.Id, -32602, "run.existing_session parameters are invalid.");
        }

        var upsertError = TryUpsertProviderConfigs(existingRun.ProviderConfigs);
        if (upsertError is not null)
        {
            return Error(request.Id, -32602, upsertError);
        }

        var snapshot = await repository.GetSessionSnapshotAsync(existingRun.SessionId, ct)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return Error(request.Id, -32004, $"Session '{existingRun.SessionId}' was not found.");
        }

        if (!_sessionSelections.TryGetValue(existingRun.SessionId, out var savedSelection))
        {
            return Error(
                request.Id,
                -32006,
                $"Session '{existingRun.SessionId}' requires session.rehydrate before it can run.");
        }

        if (existingRun.ExpectedSelectionVersion is { } expectedVersion
            && expectedVersion != savedSelection.SelectionVersion)
        {
            return Error(
                request.Id,
                -32006,
                $"Selection version {expectedVersion} does not match current version {savedSelection.SelectionVersion}.");
        }

        var effectiveSelection = existingRun.TurnOverride ?? savedSelection;
        if (effectiveSelection.Mode != snapshot.Mode)
        {
            return Error(request.Id, -32602, "run.existing_session selection mode does not match the session mode.");
        }

        switch (snapshot.Mode)
        {
            case RuntimeMode.Expert:
                if (effectiveSelection.ModeOptions is not ExpertModeOptions expertOptions)
                {
                    return Error(request.Id, -32602, "run.existing_session Expert mode requires ExpertModeOptions.");
                }

                if (string.IsNullOrWhiteSpace(expertOptions.Agent.SystemPrompt)
                    && existingRun.TurnOverride is null)
                {
                    return Error(
                        request.Id,
                        -32006,
                        "A TurnOverride with the current Agent prompt is required to resume this Session.");
                }

                if (string.IsNullOrWhiteSpace(expertOptions.Agent.SystemPrompt))
                {
                    return Error(
                        request.Id,
                        -32006,
                        "An empty Agent prompt cannot be used to resume an Expert Session.");
                }

                if (existingRun.TurnOverride is null)
                {
                    var promptValidation = ValidateResumedExpertPrompt(snapshot, expertOptions);
                    if (promptValidation is not null)
                    {
                        return Error(request.Id, -32006, promptValidation);
                    }
                }
                break;

            default:
                return Error(request.Id, -32602, $"run.existing_session does not support mode '{snapshot.Mode}'.");
        }

        if (_hostProviderRegistry is not null)
        {
            var hostError = HostModeSelectionValidator.ValidateForRun(
                effectiveSelection,
                snapshot.Mode,
                _hostProviderRegistry);
            if (hostError is not null)
            {
                return Error(request.Id, -32602, hostError);
            }
        }

        var executionRequest = new NewSessionRunRequest(
            $"existing:{existingRun.SessionId}",
            existingRun.RunIdempotencyKey,
            snapshot.Mode,
            effectiveSelection,
            existingRun.InputOverride,
            ProviderConfigs: existingRun.ProviderConfigs,
            Language: existingRun.Language);
        var plan = new RunStartPlan(
            $"session:{existingRun.SessionId}",
            RuntimeRunMetadata.ComputeRequestHash(existingRun),
            executionRequest,
            existingRun.SessionId,
            SessionIdempotencyKey: null,
            InitialSelection: null);
        return await StartRunResponseAsync(
            request,
            session,
            repository,
            outbox,
            plan,
            ct).ConfigureAwait(false);
    }

    private IRuntimeProviderAdapter ResolveProviderAdapter(
        NextTurnSelection selection,
        string providerId)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var providerSelection = selection with
        {
            DefaultSelection = selection.DefaultSelection with { ProviderId = providerId }
        };

        IRuntimeProviderAdapter adapter;
        try
        {
            adapter = _providerResolver?.Invoke(providerSelection)
                ?? new UnavailableProviderAdapter(providerId);
        }
        catch (InvalidOperationException) when (_hostProviderRegistry is not null)
        {
            // Preserve ProviderNotConfigured semantics for host-mode missing Registry entries.
            adapter = new UnavailableProviderAdapter(providerId);
        }

        if (!string.Equals(adapter.ProviderId, providerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Resolved adapter ProviderId '{adapter.ProviderId}' does not match requested '{providerId}'.");
        }

        return adapter;
    }

    private string? TryUpsertProviderConfigs(IReadOnlyList<HostProviderConfig>? configs)
    {
        if (configs is null || configs.Count == 0)
        {
            return null;
        }

        if (_hostProviderRegistry is null)
        {
            return "ProviderConfigs require host-mode Provider Registry.";
        }

        try
        {
            _hostProviderRegistry.Upsert(configs);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }

    private JsonRpcResponse CreateProvidersUpsertResponse(JsonRpcRequest request)
    {
        if (_hostProviderRegistry is null)
        {
            return Error(
                request.Id,
                -32602,
                "providers.upsert is only available in host mode.");
        }

        var parameters = request.Params is { } json
            ? json.Deserialize(RuntimeJsonContext.Default.ProvidersUpsertParameters)
            : null;
        if (parameters?.Providers is null || parameters.Providers.Count == 0)
        {
            return Error(request.Id, -32602, "The providers.upsert parameters are invalid.");
        }

        var upsertError = TryUpsertProviderConfigs(parameters.Providers);
        if (upsertError is not null)
        {
            return Error(request.Id, -32602, upsertError);
        }

        var result = JsonSerializer.SerializeToElement(true, RuntimeJsonContext.Default.Boolean);
        return new JsonRpcResponse("2.0", request.Id, result);
    }

    private async Task<JsonRpcResponse> StartRunResponseAsync(
        JsonRpcRequest request,
        AuthenticatedSession session,
        SqliteSessionRepository repository,
        SqliteEventOutbox outbox,
        RunStartPlan plan,
        CancellationToken ct)
    {
        var runRequest = plan.ExecutionRequest;
        var primaryProviderId = _hostProviderRegistry is not null
            ? HostModeSelectionValidator.ResolvePrimaryProviderId(runRequest.Selection)
            : runRequest.Selection.ModeOptions switch
            {
                ExpertModeOptions expert => expert.Agent.ProviderId
                    ?? runRequest.Selection.DefaultSelection.ProviderId,
                _ => throw new NotSupportedException(
                    $"Mode '{runRequest.Selection.ModeOptions.GetType().Name}' is not supported.")
            };
        if (_activeRunRequests.TryGetValue(
            runRequest.RunIdempotencyKey,
            out var existingRequest))
        {
            if (!string.Equals(
                existingRequest.SessionIdentity,
                plan.SessionIdentity,
                StringComparison.Ordinal))
            {
                return Error(
                    request.Id,
                    -32004,
                    "The Run idempotency key is already bound to another Session request.");
            }

            if (!string.Equals(
                existingRequest.RequestHash,
                plan.RequestHash,
                StringComparison.Ordinal))
            {
                return Error(
                    request.Id,
                    -32004,
                    "The Run idempotency key was reused with a different request payload.");
            }

            var existingPayload = JsonSerializer.SerializeToElement(
                existingRequest.Accepted,
                RuntimeJsonContext.Default.RunAcceptedEvent);
            return new JsonRpcResponse("2.0", request.Id, Result: existingPayload);
        }

        IRuntimeProviderAdapter provider;
        try
        {
            provider = ResolveProviderAdapter(runRequest.Selection, primaryProviderId);
        }
        catch (Exception ex)
        {
            return Error(request.Id, -32602, $"The selected Provider is invalid: {ex.Message}");
        }

        var providerCache = new ConcurrentDictionary<string, Lazy<IRuntimeProviderAdapter>>(
            StringComparer.Ordinal);
        providerCache[primaryProviderId] = new Lazy<IRuntimeProviderAdapter>(
            () => provider,
            LazyThreadSafetyMode.ExecutionAndPublication);

        IRuntimeProviderAdapter ResolveRunProvider(string id)
        {
            return providerCache.GetOrAdd(id, key => new Lazy<IRuntimeProviderAdapter>(
                () => ResolveProviderAdapter(runRequest.Selection, key),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        var runLease = _runRegistry.TryAcquire(provider.ProviderId, out var capacityResult);
        if (runLease is null)
        {
            var message = capacityResult is RunCapacityResult.ProviderCapacityReached
                ? $"The Provider '{provider.ProviderId}' has reached its Run capacity."
                : "The Runtime has reached its Run capacity.";
            return Error(request.Id, -32003, message);
        }

        var capacityTransferred = false;
        try
        {
            var sessionId = plan.ExistingSessionId;
            if (sessionId is null)
            {
                sessionId = await repository.CreateSessionAsync(
                    runRequest.Mode,
                    plan.SessionIdempotencyKey!,
                    TimeSpan.FromDays(7),
                    plan.RequestHash,
                    ct).ConfigureAwait(false);

                var initialSelection = plan.InitialSelection!;
                var persistedSelection = RuntimeRunMetadata.RemovePromptContent(initialSelection);
                var selectionJson = JsonSerializer.Serialize(
                    persistedSelection,
                    RuntimeJsonContext.Default.NextTurnSelection);
                var saved = await repository.TrySaveSessionSelectionAsync(
                    sessionId,
                    expectedSelectionVersion: null,
                    initialSelection.SelectionVersion,
                    selectionJson,
                    RuntimeRunMetadata.CreateAgentSnapshots(initialSelection),
                    ct).ConfigureAwait(false);
                if (!saved)
                {
                    return Error(request.Id, -32006, "The Session Selection could not be initialized.");
                }

                _sessionSelections[sessionId] = initialSelection;
            }

            // 临时标题：新建与续跑共用。仅在 title 仍为空时，用本轮用户首段文本落盘。
            // TrySetInitialSessionTitleAsync 的 WHERE 保证已有标题不会被覆盖。
            await TryEnsureInitialSessionTitleAsync(
                    repository,
                    sessionId,
                    runRequest.InitialInput,
                    ct)
                .ConfigureAwait(false);

            if (!runLease.TryBindSession(sessionId))
            {
                return Error(
                    request.Id,
                    -32003,
                    $"Session '{sessionId}' has reached its Run capacity.");
            }

            var runId = await repository.CreateRunAsync(
                sessionId,
                runRequest.RunIdempotencyKey,
                TimeSpan.FromHours(24),
                plan.RequestHash,
                ct).ConfigureAwait(false);
            var shouldEmitAccepted = await repository.GetRunStatusAsync(runId, ct)
                .ConfigureAwait(false) is RunStatus.Accepted;
            var accepted = new RunAcceptedEvent(sessionId, runId);
            var payload = JsonSerializer.SerializeToElement(
                accepted,
                RuntimeJsonContext.Default.RunAcceptedEvent);
            if (!shouldEmitAccepted)
            {
                return new JsonRpcResponse("2.0", request.Id, Result: payload);
            }

            const long runSequence = 0;
            await outbox.AppendAsync(
                runId,
                runSequence,
                MessageTypes.RunAccepted,
                payload.GetRawText(),
                ct).ConfigureAwait(false);
            await DispatchPendingEventsAsync(session, outbox, ct).ConfigureAwait(false);

            var activeRun = new ActiveRun(
                runLease,
                session.HostInstanceId,
                sessionId,
                _timeProvider.GetUtcNow(),
                provider);
            if (!_activeRuns.TryAdd(runId, activeRun))
            {
                activeRun.Dispose();
                return new JsonRpcResponse("2.0", request.Id, Result: payload);
            }

            _activeRunRequests[runRequest.RunIdempotencyKey] = new ActiveRunRequest(
                plan.SessionIdentity,
                plan.RequestHash,
                accepted);

            var toolCatalogSnapshot = session.ToolCatalogStore.CaptureSnapshot();
            var controlPeer = session.SupportsReverseRpc
                ? new ReconnectableDuplexRpcPeer(
                    () => ResolveReverseRpcPeer(session.HostInstanceId))
                : null;
            var executionTask = Task.Run(
                () => ExecuteRunAsync(
                    runId,
                    sessionId,
                    runRequest,
                    provider,
                    ResolveRunProvider,
                    session,
                    activeRun,
                    toolCatalogSnapshot,
                    controlPeer),
                CancellationToken.None);
            activeRun.Attach(executionTask);
            capacityTransferred = true;
            return new JsonRpcResponse("2.0", request.Id, Result: payload);
        }
        finally
        {
            if (!capacityTransferred)
            {
                runLease.Dispose();
            }
        }
    }

    private async Task ExecuteRunAsync(
        string runId,
        string sessionId,
        NewSessionRunRequest runRequest,
        IRuntimeProviderAdapter provider,
        Func<string, IRuntimeProviderAdapter> providerFactory,
        AuthenticatedSession session,
        ActiveRun activeRun,
        ToolCatalogSnapshot toolCatalogSnapshot,
        IDuplexRpcPeer? controlPeer)
    {
        var ct = activeRun.CancellationToken;
        try
        {
            await using var dispatchConnection = await OpenDatabaseConnectionAsync(ct)
                .ConfigureAwait(false);
            var runRepository = new SqliteSessionRepository(dispatchConnection);
            using var dispatchOutbox = CreateEventOutbox(dispatchConnection);
            await foreach (var envelope in _executionCore.RunAsync(
                runId,
                sessionId,
                runRequest,
                provider,
                session.ToolCatalogStore,
                toolCatalogSnapshot,
                controlPeer,
                (refresh, refreshCt) => RequestCredentialsAsync(
                    refresh,
                    session,
                    dispatchOutbox,
                    activeRun,
                    refreshCt),
                () => activeRun.IsTimedOut,
                providerFactory: providerFactory,
                skillCatalog: session.SkillCatalog,
                isHostModeSkills: _hostProviderRegistry is not null,
                ct: ct).ConfigureAwait(false))
            {
                if (string.Equals(envelope.MessageType, MessageTypes.RunStatusChanged, StringComparison.Ordinal)
                    && envelope.Payload.Deserialize(RuntimeJsonContext.Default.RunStatusChangedEvent)
                        is { } statusChanged
                    && Enum.TryParse<RunStatus>(statusChanged.Status, ignoreCase: false, out var changedStatus))
                {
                    await runRepository.UpdateRunProgressAsync(
                        runId,
                        changedStatus,
                        statusChanged.Phase,
                        statusChanged.CurrentStep,
                        statusChanged.CompletedUnits,
                        statusChanged.TotalUnits,
                        envelope.RunSequence,
                        statusChanged.Error is null
                            ? null
                            : JsonSerializer.Serialize(
                                statusChanged.Error,
                                RuntimeJsonContext.Default.RuntimeError),
                        CancellationToken.None).ConfigureAwait(false);
                }

                if (IsTerminalRunEvent(envelope.MessageType))
                {
                    await TransitionRunForTerminalEventAsync(
                        runRepository,
                        runId,
                        envelope.MessageType).ConfigureAwait(false);
                    var terminalStatus = envelope.MessageType == MessageTypes.RunCompleted
                        ? RunStatus.Completed
                        : envelope.MessageType == MessageTypes.RunCancelled
                            ? RunStatus.Cancelled
                            : RunStatus.Failed;
                    var errorJson = envelope.Payload.TryGetProperty("error", out var errorElement)
                        ? errorElement.GetRawText()
                        : null;
                    await runRepository.UpdateRunProgressAsync(
                        runId,
                        terminalStatus,
                        "terminal",
                        currentStep: null,
                        completedUnits: null,
                        totalUnits: null,
                        lastRunSequence: envelope.RunSequence,
                        errorJson: errorJson,
                        ct: CancellationToken.None).ConfigureAwait(false);
                    activeRun.ReleaseCapacity();
                }

                await DispatchPendingEventsAsync(session, dispatchOutbox, _shutdown.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            activeRun.ReleaseCapacity();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && activeRun.IsTimedOut)
        {
            activeRun.ReleaseCapacity();
            await CompleteFailedRunAsync(
                runId,
                sessionId,
                session,
                new TimeoutException("The Run exceeded its configured timeout."),
                RuntimeErrorCodes.RunTimedOut).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            activeRun.ReleaseCapacity();
            await CompleteCancelledRunAsync(runId, sessionId, session).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            activeRun.ReleaseCapacity();
            await CompleteFailedRunAsync(runId, sessionId, session, ex).ConfigureAwait(false);
        }
        finally
        {
            _activeRuns.TryRemove(runId, out _);
            _activeRunRequests.TryRemove(runRequest.RunIdempotencyKey, out _);
            activeRun.Dispose();
            TryDisposeDisconnectedSession(session);
        }
    }

    private void TryDisposeDisconnectedSession(AuthenticatedSession session)
    {
        if (!session.IsControlDisconnected
            || _activeRuns.Values.Any(run => string.Equals(
                run.OwnerHostInstanceId,
                session.HostInstanceId,
                StringComparison.Ordinal)))
        {
            return;
        }

        session.DisposeIfDisconnected();
    }

    private IDuplexRpcPeer? ResolveReverseRpcPeer(string hostInstanceId)
    {
        if (!_sessions.TryGetValue(hostInstanceId, out var session)
            || !session.SupportsReverseRpc)
        {
            return null;
        }

        return session.TryGetControlPeer();
    }

    private async Task<bool> RequestCredentialsAsync(
        CredentialsRefreshRequestedEvent request,
        AuthenticatedSession session,
        SqliteEventOutbox outbox,
        ActiveRun activeRun,
        CancellationToken ct)
    {
        var waitTask = activeRun.WaitForCredentialsAsync(request.ProviderId, ct);
        if (waitTask.IsCompletedSuccessfully && !await waitTask.ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            var payload = JsonSerializer.SerializeToElement(
                request,
                RuntimeJsonContext.Default.CredentialsRefreshRequestedEvent);
            var runSequence = await outbox.GetNextRunSequenceAsync(request.RunId, ct)
                .ConfigureAwait(false);
            await outbox.AppendAsync(
                request.RunId,
                runSequence,
                MessageTypes.CredentialsRefreshRequested,
                payload.GetRawText(),
                ct).ConfigureAwait(false);
            await DispatchPendingEventsAsync(session, outbox, ct).ConfigureAwait(false);
            return await waitTask.ConfigureAwait(false);
        }
        catch
        {
            activeRun.CancelCredentialWait();
            throw;
        }
    }

    private Task<TerminalCommitResult> CompleteCancelledRunAsync(
        string runId,
        string sessionId,
        AuthenticatedSession session)
    {
        var payload = JsonSerializer.SerializeToElement(
            new RunCancelledEvent(runId, sessionId),
            RuntimeJsonContext.Default.RunCancelledEvent);
        var pending = new PendingTerminalRun(
            runId,
            sessionId,
            session,
            RunStatus.Cancelled,
            MessageTypes.RunCancelled,
            payload.GetRawText(),
            ErrorJson: null,
            MeetingStatus: "Cancelled",
            InterruptWorkSession: true);
        return CompleteTerminalRunAsync(pending);
    }

    private async Task<TerminalCommitResult> CompleteFailedRunAsync(
        string runId,
        string sessionId,
        AuthenticatedSession session,
        Exception exception,
        string errorCode = "RunExecutionFailed")
    {
        ArgumentNullException.ThrowIfNull(exception);
        var error = new RuntimeError(
            errorCode,
            "runtime",
            string.IsNullOrWhiteSpace(exception.Message)
                ? "Run execution failed."
                : exception.Message,
            IsRetryable: false,
            ProviderDetails: null,
            Guid.NewGuid().ToString("N"));
        var payload = JsonSerializer.SerializeToElement(
            new RunFailedEvent(runId, sessionId, error),
            RuntimeJsonContext.Default.RunFailedEvent);
        var errorJson = JsonSerializer.Serialize(
            error,
            RuntimeJsonContext.Default.RuntimeError);
        var pending = new PendingTerminalRun(
            runId,
            sessionId,
            session,
            RunStatus.Failed,
            MessageTypes.RunFailed,
            payload.GetRawText(),
            errorJson,
            MeetingStatus: "Failed",
            InterruptWorkSession: false);
        await _diagnosticLog.TryWriteAsync(
                RuntimeDiagnosticLogLevel.Error,
                "RunFailed",
                runId,
                sessionId,
                exception: exception,
                error: error)
            .ConfigureAwait(false);
        return await CompleteTerminalRunAsync(pending).ConfigureAwait(false);
    }

    private async Task<TerminalCommitResult> CompleteTerminalRunAsync(
        PendingTerminalRun pending)
    {
        int[] retryDelaysMilliseconds = [0, 100, 500];
        foreach (var delayMilliseconds in retryDelaysMilliseconds)
        {
            if (delayMilliseconds > 0)
            {
                await Task.Delay(delayMilliseconds).ConfigureAwait(false);
            }

            var result = await TryCommitTerminalRunOnceAsync(pending).ConfigureAwait(false);
            if (result is not TerminalCommitResult.RetryPending)
            {
                _pendingTerminalRuns.TryRemove(pending.RunId, out _);
                return result;
            }
        }

        _pendingTerminalRuns[pending.RunId] = pending;
        await TryMarkPendingRunPersistingAsync(pending.RunId).ConfigureAwait(false);
        return TerminalCommitResult.RetryPending;
    }

    private async Task<TerminalCommitResult> TryCommitTerminalRunOnceAsync(
        PendingTerminalRun pending)
    {
        try
        {
            await using var repositoryConnection = await OpenDatabaseConnectionAsync(CancellationToken.None)
                .ConfigureAwait(false);

            await TryTransitionMeetingToTerminalAsync(
                repositoryConnection,
                pending.SessionId,
                pending.MeetingStatus).ConfigureAwait(false);
            var repository = new SqliteSessionRepository(repositoryConnection);
            var gsn = await repository.TryCommitTerminalRunAsync(
                pending.RunId,
                pending.Status,
                terminalText: null,
                pending.ErrorJson,
                pending.MessageType,
                pending.PayloadJson,
                CancellationToken.None).ConfigureAwait(false);
            if (gsn is null)
            {
                return TerminalCommitResult.AlreadyTerminal;
            }

            using var outbox = CreateEventOutbox(repositoryConnection);
            try
            {
                await DispatchPendingEventsAsync(
                    pending.Session,
                    outbox,
                    CancellationToken.None).ConfigureAwait(false);
                return TerminalCommitResult.Committed;
            }
            catch (Exception)
            {
                return TerminalCommitResult.DispatchPending;
            }
        }
        catch (Exception)
        {
            return TerminalCommitResult.RetryPending;
        }
    }

    private async Task TryMarkPendingRunPersistingAsync(string runId)
    {
        try
        {
            await using var connection = await OpenDatabaseConnectionAsync(CancellationToken.None)
                .ConfigureAwait(false);
            await new SqliteSessionRepository(connection).UpdateRunProgressAsync(
                runId,
                RunStatus.Persisting,
                "persisting",
                currentStep: null,
                completedUnits: null,
                totalUnits: null,
                lastRunSequence: null,
                errorJson: null,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private async Task ReconcilePendingTerminalRunsAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                await ReconcilePendingTerminalRunsOnceAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task ReconcilePendingTerminalRunsOnceAsync()
    {
        foreach (var pending in _pendingTerminalRuns.Values.ToArray())
        {
            var result = await TryCommitTerminalRunOnceAsync(pending).ConfigureAwait(false);
            if (result is not TerminalCommitResult.RetryPending)
            {
                _pendingTerminalRuns.TryRemove(pending.RunId, out _);
            }
        }
    }

    private static async Task TryTransitionMeetingToTerminalAsync(
        SqliteConnection connection,
        string sessionId,
        string targetStatus)
    {
        // Meeting mode removed - no-op
        await Task.CompletedTask;
    }

    private async Task AppendTerminalEventAsync(
        string runId,
        string messageType,
        JsonElement payload,
        AuthenticatedSession session)
    {
        await using var outboxConnection = await OpenDatabaseConnectionAsync(CancellationToken.None)
            .ConfigureAwait(false);
        using var outbox = CreateEventOutbox(outboxConnection);
        await using var sequenceCommand = outboxConnection.CreateCommand();
        sequenceCommand.CommandText = """
            SELECT COALESCE(MAX(run_sequence), -1) + 1
            FROM event_outbox
            WHERE run_id = $runId;
            """;
        sequenceCommand.Parameters.AddWithValue("$runId", runId);
        var runSequence = Convert.ToInt64(
            await sequenceCommand.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        await outbox.AppendAsync(
            runId,
            runSequence,
            messageType,
            payload.GetRawText(),
            CancellationToken.None).ConfigureAwait(false);
        await DispatchPendingEventsAsync(session, outbox, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task TransitionRunForTerminalEventAsync(
        SqliteSessionRepository repository,
        string runId,
        string messageType)
    {
        var status = string.Equals(messageType, MessageTypes.RunCompleted, StringComparison.Ordinal)
            ? RunStatus.Completed
            : string.Equals(messageType, MessageTypes.RunCancelled, StringComparison.Ordinal)
                ? RunStatus.Cancelled
                : RunStatus.Failed;
        await TryTransitionToTerminalAsync(repository, runId, status).ConfigureAwait(false);
    }

    private static async Task<bool> TryTransitionToTerminalAsync(
        SqliteSessionRepository repository,
        string runId,
        RunStatus terminalStatus)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = await repository.GetRunStatusAsync(runId, CancellationToken.None)
                .ConfigureAwait(false);
            if (Madorin.AI.Runtime.Core.RunStateMachine.IsTerminal(current))
            {
                return false;
            }

            if (terminalStatus is RunStatus.Failed && current is RunStatus.Accepted)
            {
                try
                {
                    await repository.TransitionRunStatusAsync(
                        runId,
                        RunStatus.Accepted,
                        RunStatus.Preparing,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                }

                continue;
            }

            if (terminalStatus is RunStatus.Completed && current is RunStatus.Running)
            {
                try
                {
                    await repository.TransitionRunStatusAsync(
                        runId,
                        RunStatus.Running,
                        RunStatus.Persisting,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                }

                continue;
            }

            try
            {
                await repository.TransitionRunToTerminalAsync(
                    runId,
                    current,
                    terminalStatus,
                    terminalText: null,
                    CancellationToken.None).ConfigureAwait(false);
                return true;
            }
            catch (InvalidOperationException)
            {
            }
        }

        return false;
    }

    private async Task DispatchPendingEventsAsync(
        AuthenticatedSession session,
        SqliteEventOutbox outbox,
        CancellationToken ct)
    {
        if (_sessions.TryGetValue(session.HostInstanceId, out var currentSession))
        {
            session = currentSession;
        }

        await _eventDispatchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var eventChannel = session.TryGetEventChannel();
            if (eventChannel is null)
            {
                return;
            }

            var entries = await outbox.LoadPendingAsync(ct).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                if (entry.Gsn <= session.LastDispatchedGsn)
                {
                    continue;
                }

                using var payloadDocument = JsonDocument.Parse(entry.PayloadJson);
                var envelope = new RuntimeEventEnvelope(
                    InstanceId,
                    entry.Gsn,
                    entry.RunId,
                    entry.RunSequence,
                    entry.MessageType,
                    entry.CreatedAt ?? DateTimeOffset.UtcNow,
                    payloadDocument.RootElement.Clone());
                try
                {
                    await eventChannel.SendAsync(envelope, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    session.DetachEventChannel(eventChannel);
                    return;
                }

                await outbox.MarkSentAsync(entry.Gsn, ct).ConfigureAwait(false);
                session.MarkDispatched(entry.Gsn);
            }
        }
        finally
        {
            _eventDispatchGate.Release();
        }
    }

    private async Task ReplayPendingEventsAsync(
        AuthenticatedSession session,
        SqliteEventOutbox outbox,
        FramedEventChannel eventChannel,
        CancellationToken ct)
    {
        var entries = await outbox.LoadPendingAsync(ct).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            if (entry.Gsn <= session.LastConfirmedGsn)
            {
                continue;
            }

            using var payloadDocument = JsonDocument.Parse(entry.PayloadJson);
            var envelope = new RuntimeEventEnvelope(
                InstanceId,
                entry.Gsn,
                entry.RunId,
                entry.RunSequence,
                entry.MessageType,
                entry.CreatedAt ?? DateTimeOffset.UtcNow,
                payloadDocument.RootElement.Clone());
            await eventChannel.SendAsync(envelope, ct).ConfigureAwait(false);
            await outbox.MarkSentAsync(entry.Gsn, ct).ConfigureAwait(false);
            session.MarkDispatched(entry.Gsn);
        }
    }

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

    private SqliteEventOutbox CreateEventOutbox(SqliteConnection connection) =>
        new(
            connection,
            Path.Combine(_dataDirectory, "replay"),
            SqliteEventOutbox.DefaultMaxReplayBytes);

    private RuntimeLimits CreateNegotiatedLimits(bool supportsStage5) =>
        _limits with
        {
            MaxEventFrameBytes = NamedPipeTransport.MaxEventMessageBytes,
            MaxToolRounds = supportsStage5 ? _limits.MaxToolRounds : null,
            MaxToolCallsPerRound = supportsStage5 ? _limits.MaxToolCallsPerRound : null,
            MaxToolResultBytesPerInvocation = supportsStage5
                ? _limits.MaxToolResultBytesPerInvocation ?? 4L * 1024 * 1024
                : null
        };

    /// <summary>
    /// Skills capability surface advertised on initialize.
    /// <see cref="SkillsCapabilities.DiscoveryDirect"/> is the sole host-facing gate;
    /// Runtime mode parsing only normalizes the agent string (no second process flag).
    /// </summary>
    private static SkillsCapabilities CreateSkillsCapabilities() =>
        new(
            Enabled: true,
            SourceKinds: ["directory"],
            ResourcesSupported: true,
            ScriptsSupported: true,
            HotRefreshSupported: true,
            DiscoveryDirect: true);

    private static JsonRpcResponse Error(long id, int code, string message)
        => new("2.0", id, Error: new JsonRpcError(code, message));

    private static bool IsTerminalRunEvent(string messageType) =>
        string.Equals(messageType, MessageTypes.RunCompleted, StringComparison.Ordinal)
        || string.Equals(messageType, MessageTypes.RunFailed, StringComparison.Ordinal)
        || string.Equals(messageType, MessageTypes.RunCancelled, StringComparison.Ordinal);

    private RunStatus GetQueryableRunStatus(RunSnapshot snapshot) =>
        _pendingTerminalRuns.ContainsKey(snapshot.RunId)
            ? RunStatus.Persisting
            : snapshot.Status;

    private static RunQueryResult? CreateRunQueryResult(
        RunSnapshot? snapshot,
        RunStatus? statusOverride = null) =>
        snapshot is null
            ? null
            : new RunQueryResult(
                snapshot.RunId,
                snapshot.SessionId,
                statusOverride ?? snapshot.Status,
                snapshot.TerminalText)
            {
                Mode = snapshot.Mode,
                Phase = snapshot.Phase,
                CurrentStep = snapshot.CurrentStep,
                CompletedUnits = snapshot.CompletedUnits,
                TotalUnits = snapshot.TotalUnits,
                UpdatedAt = snapshot.UpdatedAt,
                LastRunSequence = snapshot.LastRunSequence,
                Error = DeserializeRuntimeError(snapshot.ErrorJson)
            };

    private static RuntimeError? DeserializeRuntimeError(string? errorJson) =>
        string.IsNullOrWhiteSpace(errorJson)
            ? null
            : JsonSerializer.Deserialize(
                errorJson,
                RuntimeJsonContext.Default.RuntimeError);


    private async Task<ConversationHistoryMetadata> TryGetConversationHistoryMetadataAsync(
        string sessionId,
        List<string> diagnostics,
        CancellationToken ct)
    {
        try
        {
            var store = new ConversationStore(_dataDirectory);
            return await store.GetHistoryMetadataAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(
                $"CanonicalHistoryCorrupt: Session '{sessionId}' history could not be read: {ex.Message}");
            return new ConversationHistoryMetadata(0, null, null, 0, []);
        }
    }

    private async Task ValidateResumeBlobReferencesAsync(
        IReadOnlyList<string> blobIds,
        List<string> diagnostics,
        CancellationToken ct)
    {
        foreach (var blobId in blobIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (blobId.Length != 64 || blobId.Any(static character => !Uri.IsHexDigit(character)))
            {
                diagnostics.Add($"BlobReferenceInvalid: '{blobId}' is not a SHA-256 identifier.");
                continue;
            }

            var path = Path.Combine(_dataDirectory, "blobs", blobId.ToLowerInvariant() + ".blob");
            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var actualHash = Convert.ToHexString(
                    await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct)
                        .ConfigureAwait(false));
                if (!string.Equals(actualHash, blobId, StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(
                        $"BlobHashMismatch: Blob '{blobId}' content does not match its identifier.");
                }
            }
            catch (FileNotFoundException)
            {
                diagnostics.Add($"BlobMissing: Blob '{blobId}' does not exist in retained storage.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add($"BlobUnreadable: Blob '{blobId}' could not be read: {ex.Message}");
            }
        }
    }

    private static string? ValidateResumedExpertPrompt(
        PersistedSessionSnapshot snapshot,
        ExpertModeOptions expertOptions)
    {
        if (string.IsNullOrWhiteSpace(expertOptions.Agent.SystemPrompt))
        {
            return "An empty Agent prompt cannot be used to resume an Expert Session.";
        }

        var agentSnapshot = snapshot.AgentSnapshots.FirstOrDefault(agent =>
            string.Equals(agent.AgentId, expertOptions.Agent.AgentId, StringComparison.Ordinal));
        if (agentSnapshot is null)
        {
            return $"Agent '{expertOptions.Agent.AgentId}' is missing from the Session Agent snapshots.";
        }

        var promptHash = RuntimeRunMetadata.ComputePromptHash(expertOptions.Agent.SystemPrompt);
        if (!string.Equals(promptHash, agentSnapshot.PromptHash, StringComparison.OrdinalIgnoreCase))
        {
            return $"Agent prompt hash for '{expertOptions.Agent.AgentId}' does not match the Session snapshot.";
        }

        if (!string.Equals(
                expertOptions.Agent.PromptTemplateVersion,
                agentSnapshot.PromptTemplateVersion,
                StringComparison.Ordinal))
        {
            return $"Agent prompt template version for '{expertOptions.Agent.AgentId}' does not match the Session snapshot.";
        }

        return null;
    }

    private static List<string> CreateRecoveryDiagnostics(PersistedSessionSnapshot snapshot)
    {
        var diagnostics = new List<string>();
        if (snapshot.SelectionJson is null)
        {
            diagnostics.Add("The Session has no persisted Selection snapshot.");
        }

        if (snapshot.LatestRun is { Status: var status }
            && status is RunStatus.Accepted
                or RunStatus.Preparing
                or RunStatus.Running
                or RunStatus.WaitingForTool
                or RunStatus.Persisting)
        {
            diagnostics.Add(
                $"RunStateUnrecovered: The latest Run is still '{status}' instead of an explicit terminal, waiting, or Interrupted state.");
        }

        return diagnostics;
    }

    private static NextTurnSelection? DeserializePersistedSelection(
        PersistedSessionSnapshot snapshot,
        List<string> diagnostics)
    {
        if (snapshot.SelectionJson is null)
        {
            return null;
        }

        try
        {
            var selection = JsonSerializer.Deserialize(
                snapshot.SelectionJson,
                RuntimeJsonContext.Default.NextTurnSelection);
            if (selection is null
                || selection.Mode != snapshot.Mode
                || selection.SelectionVersion != snapshot.SelectionVersion)
            {
                diagnostics.Add("The persisted Selection metadata is inconsistent.");
                return null;
            }

            return selection;
        }
        catch (JsonException)
        {
            diagnostics.Add("The persisted Selection snapshot is not valid JSON.");
            return null;
        }
    }


    internal static NeededAgentDefinition[] CreateNeededDefinitions(
        PersistedSessionSnapshot snapshot,
        NextTurnSelection? selection)
    {
        var roles = new Dictionary<string, (string Type, string? ParticipantId)>(
            StringComparer.Ordinal);
        switch (selection?.ModeOptions)
        {
            case ExpertModeOptions expert:
                roles[expert.Agent.AgentId] = ("SingleAgent", null);
                if (expert.AvailableAgents is not null)
                {
                    foreach (var agent in expert.AvailableAgents)
                    {
                        roles.TryAdd(agent.AgentId, ("ExpertSubAgent", null));
                    }
                }

                break;
        }

        return
        [
            .. snapshot.AgentSnapshots.Select(agent =>
            {
                var role = roles.GetValueOrDefault(
                    agent.AgentId,
                    snapshot.Mode switch
                    {
                        RuntimeMode.Expert => ("SingleAgent", null),
                        _ => ("Agent", null)
                    });
                return new NeededAgentDefinition(
                    agent.AgentId,
                    agent.PromptHash,
                    role.Item1,
                    role.Item2);
            })
        ];
    }

    private static NextTurnSelection RehydrateSelection(
        NextTurnSelection selection,
        Dictionary<string, AgentDefinition> definitions)
    {
        AgentRef RehydrateAgent(AgentRef persisted)
        {
            var definition = definitions[persisted.AgentId];
            return persisted with { SystemPrompt = definition.AgentRef.SystemPrompt };
        }

        ModeOptions modeOptions = selection.ModeOptions switch
        {
            // Expert 须恢复 AvailableAgents，否则 rehydrate 后 @ 委派池丢失。
            ExpertModeOptions expert => new ExpertModeOptions(
                RehydrateAgent(expert.Agent),
                expert.AvailableAgents is null
                    ? null
                    : [.. expert.AvailableAgents.Select(RehydrateAgent)]),
            _ => throw new InvalidOperationException(
                $"Mode options '{selection.ModeOptions.GetType().Name}' are not supported.")
        };
        return selection with { ModeOptions = modeOptions };
    }

    private async Task WritePidFileAsync(string path, CancellationToken ct)
    {
        var info = new RuntimePidInfo(
            InstanceId,
            PipeName,
            EventPipeName,
            Environment.ProcessId,
            _startedAt);
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            info,
            RuntimePidInfoContext.Default.RuntimePidInfo);
        await File.WriteAllBytesAsync(path, json, ct).ConfigureAwait(false);
    }

    private static void TryDeletePidFile(string path)
    {
        try { File.Delete(path); } catch { /* best-effort */ }
    }

    private static byte[] DecodeNonce(string value)
    {
        try
        {
            var nonce = Convert.FromBase64String(value);
            return nonce.Length == 32
                ? nonce
                : throw new InvalidDataException("The handshake nonce must contain exactly 32 bytes.");
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The handshake nonce is not valid Base64.", ex);
        }
    }

    private static void ValidateDirectoryLayout(
        string dataDirectory,
        string configDirectory,
        string logDirectory)
    {
        var directories = new[] { dataDirectory, configDirectory, logDirectory };
        for (var index = 0; index < directories.Length; index++)
        {
            for (var otherIndex = index + 1; otherIndex < directories.Length; otherIndex++)
            {
                if (string.Equals(
                        directories[index],
                        directories[otherIndex],
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                {
                    throw new ArgumentException("Data, configuration, and log directories must be distinct.");
                }
            }
        }
    }

    private void PruneSeenNonces(long nowUnixMilliseconds)
    {
        var cutoff = nowUnixMilliseconds - (long)Math.Max(
            _handshakeClockSkew.TotalMilliseconds * 2,
            TimeSpan.FromMinutes(2).TotalMilliseconds);
        foreach (var pair in _seenClientNonces)
        {
            if (pair.Value < cutoff)
            {
                _seenClientNonces.TryRemove(pair.Key, out _);
            }
        }
    }

    private async Task MonitorHostLeasesAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(
            Math.Clamp(_hostLeaseTimeout.TotalMilliseconds / 3, 50, 1000));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                var now = _timeProvider.GetUtcNow();
                foreach (var pair in _sessions.ToArray())
                {
                    if (!pair.Value.IsExpired(now, _hostLeaseTimeout)
                        || !_sessions.TryRemove(pair.Key, out var expiredSession))
                    {
                        continue;
                    }

                    _disconnectedHostLeases.TryRemove(
                        expiredSession.HostInstanceId,
                        out _);
                    CancelRunsOwnedBy(expiredSession.HostInstanceId);

                    await _blobStore.AbortSessionAsync(
                        expiredSession.HostInstanceId,
                        CancellationToken.None).ConfigureAwait(false);
                    var eventChannel = expiredSession.TryGetEventChannel();
                    if (eventChannel is not null)
                    {
                        expiredSession.DetachEventChannel(eventChannel);
                        await eventChannel.DisposeAsync().ConfigureAwait(false);
                    }

                    await expiredSession.DisconnectControlChannelAsync().ConfigureAwait(false);
                }

                foreach (var pair in _disconnectedHostLeases.ToArray())
                {
                    if (now - pair.Value < _hostLeaseTimeout
                        || !_disconnectedHostLeases.TryRemove(pair.Key, out var disconnectedAt)
                        || now - disconnectedAt < _hostLeaseTimeout)
                    {
                        continue;
                    }

                    CancelRunsOwnedBy(pair.Key);
                    await _blobStore.AbortSessionAsync(pair.Key, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private void CancelRunsOwnedBy(string hostInstanceId)
    {
        foreach (var run in _activeRuns.Values.Where(candidate =>
                     string.Equals(
                         candidate.OwnerHostInstanceId,
                         hostInstanceId,
                         StringComparison.Ordinal)))
        {
            run.Cancel();
        }
    }

    private async Task DrainConnectionsAsync()
    {
        var tasks = _connections.Values.ToArray();
        if (tasks.Length > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private static async Task ObserveTasksAsync(IEnumerable<Task> tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A prior root fault determines the Runtime outcome.
            }
        }
    }

    private async Task ObserveConnectionTasksAsync()
    {
        await ObserveTasksAsync(_connections.Values.ToArray()).ConfigureAwait(false);
    }

    private void TriggerRuntimeFatal(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (Interlocked.CompareExchange(ref _fatalFailure, failure, null) is not null)
        {
            return;
        }

        _fatalSignal.TrySetException(failure);
        try
        {
            _shutdown.Cancel(throwOnFirstException: false);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (AggregateException)
        {
        }
    }

    private async Task DrainRunsAsync()
    {
        var tasks = _activeRuns.Values
            .Select(static run => run.Completion)
            .ToArray();
        if (tasks.Length > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private enum TerminalCommitResult
    {
        Committed,
        AlreadyTerminal,
        DispatchPending,
        RetryPending
    }

    private sealed record PendingTerminalRun(
        string RunId,
        string SessionId,
        AuthenticatedSession Session,
        RunStatus Status,
        string MessageType,
        string PayloadJson,
        string? ErrorJson,
        string MeetingStatus,
        bool InterruptWorkSession);

    private sealed class ActiveRun(
        RunRegistry.RunLease lease,
        string ownerHostInstanceId,
        string sessionId,
        DateTimeOffset startedAt,
        IRuntimeProviderAdapter provider) : IDisposable
    {
        private readonly RunRegistry.RunLease _lease = lease
            ?? throw new ArgumentNullException(nameof(lease));
        private readonly IRuntimeProviderAdapter _provider = provider
            ?? throw new ArgumentNullException(nameof(provider));
        private readonly object _credentialGate = new();
        private TaskCompletionSource<bool>? _credentialWaiter;

        private static readonly TimeSpan CredentialWaitTimeout = TimeSpan.FromSeconds(30);

        public CancellationToken CancellationToken => _lease.CancellationToken;

        public bool IsTimedOut => _lease.IsTimedOut;

        public string OwnerHostInstanceId { get; } = ownerHostInstanceId;

        public string SessionId { get; } = sessionId;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public Task Completion { get; private set; } = Task.CompletedTask;

        public void Attach(Task completion)
        {
            ArgumentNullException.ThrowIfNull(completion);
            Completion = completion;
        }

        public bool Cancel() => _lease.Cancel();

        public void ReleaseCapacity() => _lease.Dispose();

        public async Task<bool> WaitForCredentialsAsync(
            string providerId,
            CancellationToken ct)
        {
            if (!string.Equals(_provider.ProviderId, providerId, StringComparison.Ordinal)
                || _provider is not IRuntimeProviderCredentialUpdater)
            {
                return false;
            }

            var waiter = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_credentialGate)
            {
                if (_credentialWaiter is not null)
                {
                    return false;
                }

                _credentialWaiter = waiter;
            }

            try
            {
                return await waiter.Task.WaitAsync(CredentialWaitTimeout, ct)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
            finally
            {
                lock (_credentialGate)
                {
                    if (ReferenceEquals(_credentialWaiter, waiter))
                    {
                        _credentialWaiter = null;
                    }
                }
            }
        }

        public async Task<bool> UpdateCredentialsAsync(
            CredentialsUpdateParameters parameters,
            CancellationToken ct)
        {
            TaskCompletionSource<bool>? waiter;
            lock (_credentialGate)
            {
                waiter = _credentialWaiter;
            }

            if (waiter is null
                || !string.Equals(_provider.ProviderId, parameters.ProviderId, StringComparison.Ordinal)
                || _provider is not IRuntimeProviderCredentialUpdater updater)
            {
                return false;
            }

            await updater.UpdateCredentialsAsync(parameters, ct).ConfigureAwait(false);
            waiter.TrySetResult(true);
            return true;
        }

        public void CancelCredentialWait()
        {
            lock (_credentialGate)
            {
                _credentialWaiter?.TrySetResult(false);
                _credentialWaiter = null;
            }
        }

        public void Dispose()
        {
            CancelCredentialWait();
            _lease.Dispose();
        }
    }

    private sealed record ActiveRunRequest(
        string SessionIdentity,
        string RequestHash,
        RunAcceptedEvent Accepted);

    private sealed record RunStartPlan(
        string SessionIdentity,
        string RequestHash,
        NewSessionRunRequest ExecutionRequest,
        string? ExistingSessionId,
        string? SessionIdempotencyKey,
        NextTurnSelection? InitialSelection);


    private sealed class AuthenticatedSession(
        string hostInstanceId,
        string sessionKey,
        NamedPipeTransport controlTransport,
        DateTimeOffset lastActivity) : IDisposable
    {
        private readonly ToolCatalogStore _toolCatalogStore = new(
            new BuiltinToolRegistry(),
            new JsonSchemaToolValidator());
        private readonly SemaphoreSlim _requestGate = new(1, 1);
        private readonly object _eventChannelGate = new();
        private TaskCompletionSource<FramedEventChannel> _eventChannelSource = CreateEventChannelSource();
        private IDuplexRpcPeer? _controlPeer;
        private RuntimeCapabilities? _hostCapabilities;
        private SkillCatalogService? _skillCatalog;
        private string? _protocolVersion;
        private long _lastConfirmedGsn;
        private long _lastDispatchedGsn;
        private int _initialized;
        private int _controlDisconnected;
        private int _disposed;
        private long _lastActivityUnixMilliseconds = lastActivity.ToUnixTimeMilliseconds();

        public string HostInstanceId { get; } = hostInstanceId;

        public string SessionKey { get; } = sessionKey;

        public ToolCatalogStore ToolCatalogStore => _toolCatalogStore;

        public SkillCatalogService? SkillCatalog => Volatile.Read(ref _skillCatalog);

        public bool SupportsReverseRpc =>
            string.Equals(_protocolVersion, ProtocolVersions.Current, StringComparison.Ordinal)
            && _hostCapabilities?.ReverseRpc is true;

        public bool SupportsToolCatalog =>
            string.Equals(_protocolVersion, ProtocolVersions.Current, StringComparison.Ordinal)
            && _hostCapabilities?.ToolCatalog is not false;

        public bool SupportsToolPermissions =>
            string.Equals(_protocolVersion, ProtocolVersions.Current, StringComparison.Ordinal)
                && _hostCapabilities?.ToolPermissions is not false;

        public bool IsControlDisconnected => Volatile.Read(ref _controlDisconnected) != 0;

        public ValueTask DisconnectControlChannelAsync() => controlTransport.DisposeAsync();

        public void AttachControlPeer(IDuplexRpcPeer controlPeer)
        {
            ArgumentNullException.ThrowIfNull(controlPeer);
            if (Interlocked.CompareExchange(ref _controlPeer, controlPeer, null) is not null)
            {
                throw new InvalidOperationException("The control peer is already attached.");
            }
        }

        public IDuplexRpcPeer? TryGetControlPeer() => Volatile.Read(ref _controlPeer);

        public Task EnterRequestAsync(CancellationToken ct) => _requestGate.WaitAsync(ct);

        public void ExitRequest() => _requestGate.Release();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _requestGate.Dispose();
            _ = DisposeSkillCatalogAsync();
        }

        public void MarkControlDisconnected() => Volatile.Write(ref _controlDisconnected, 1);

        public void DisposeIfDisconnected()
        {
            if (IsControlDisconnected)
            {
                Dispose();
            }
        }

        private async Task DisposeSkillCatalogAsync()
        {
            if (_skillCatalog is not null)
            {
                await _skillCatalog.DisposeAsync().ConfigureAwait(false);
            }
        }

        public void Touch(DateTimeOffset timestamp) =>
            Interlocked.Exchange(
                ref _lastActivityUnixMilliseconds,
                timestamp.ToUnixTimeMilliseconds());

        public bool IsExpired(DateTimeOffset now, TimeSpan timeout) =>
            now.ToUnixTimeMilliseconds()
                - Interlocked.Read(ref _lastActivityUnixMilliseconds)
                >= timeout.TotalMilliseconds;

        public bool IsInitialized => Volatile.Read(ref _initialized) != 0;

        public long LastConfirmedGsn => Interlocked.Read(ref _lastConfirmedGsn);

        public long LastDispatchedGsn => Interlocked.Read(ref _lastDispatchedGsn);

        public void Initialize(
            long lastConfirmedGsn,
            string protocolVersion,
            RuntimeCapabilities hostCapabilities,
            SkillCatalogService skillCatalog)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(protocolVersion);
            ArgumentNullException.ThrowIfNull(hostCapabilities);
            _protocolVersion = protocolVersion;
            _hostCapabilities = hostCapabilities;
            _skillCatalog = skillCatalog ?? throw new ArgumentNullException(nameof(skillCatalog));
            Confirm(lastConfirmedGsn);
            MarkDispatched(lastConfirmedGsn);
            Volatile.Write(ref _initialized, 1);
        }

        public void Confirm(long lastConfirmedGsn)
        {
            var current = Interlocked.Read(ref _lastConfirmedGsn);
            while (lastConfirmedGsn > current)
            {
                var observed = Interlocked.CompareExchange(
                    ref _lastConfirmedGsn,
                    lastConfirmedGsn,
                    current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }

        public void MarkDispatched(long lastDispatchedGsn)
        {
            var current = Interlocked.Read(ref _lastDispatchedGsn);
            while (lastDispatchedGsn > current)
            {
                var observed = Interlocked.CompareExchange(
                    ref _lastDispatchedGsn,
                    lastDispatchedGsn,
                    current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }

        public Task<FramedEventChannel> WaitForEventChannelAsync(CancellationToken ct)
        {
            Task<FramedEventChannel> channelTask;
            lock (_eventChannelGate)
            {
                channelTask = _eventChannelSource.Task;
            }

            return channelTask.WaitAsync(ct);
        }

        public bool TryAttachEventChannel(FramedEventChannel eventChannel)
        {
            lock (_eventChannelGate)
            {
                return _eventChannelSource.TrySetResult(eventChannel);
            }
        }

        public FramedEventChannel? TryGetEventChannel()
        {
            lock (_eventChannelGate)
            {
                return _eventChannelSource.Task.IsCompletedSuccessfully
                    ? _eventChannelSource.Task.Result
                    : null;
            }
        }

        public void DetachEventChannel(FramedEventChannel eventChannel)
        {
            lock (_eventChannelGate)
            {
                if (_eventChannelSource.Task.IsCompletedSuccessfully
                    && ReferenceEquals(_eventChannelSource.Task.Result, eventChannel))
                {
                    _eventChannelSource = CreateEventChannelSource();
                }
            }
        }

        private static TaskCompletionSource<FramedEventChannel> CreateEventChannelSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class UnavailableProviderAdapter(string providerId) : IRuntimeProviderAdapter
    {
        public string ProviderId { get; } = providerId;

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } = [];

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new InvocationFailedProviderEvent(
                request.InvocationId,
                new RuntimeError(
                    "ProviderNotConfigured",
                    "provider",
                    $"Provider '{ProviderId}' is not configured for this Runtime instance.",
                    IsRetryable: false,
                    ProviderDetails: null,
                    Guid.NewGuid().ToString("N")));
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
