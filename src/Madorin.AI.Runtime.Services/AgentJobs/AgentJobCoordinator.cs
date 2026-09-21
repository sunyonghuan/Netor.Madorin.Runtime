using System.Collections.Concurrent;

namespace Madorin.AI.Runtime.Services.AgentJobs;

/// <summary>
/// 运行作用域内的并行子 Agent 作业池。
/// 创建后立即返回 jobId；后台执行；通过 Collect/Wait 回收结果。
/// V1：Worker 调用串行化，避免并发打共享 Provider/Gateway/Store。
/// </summary>
public sealed class AgentJobCoordinator : IAgentJobCoordinator
{
    private const int DefaultMaxConcurrent = 8;
    private const int DefaultMaxJobsPerRun = 64;
    private const int DefaultJobTimeoutMs = 300_000;
    private const int MaxJobTimeoutMs = 3_600_000;
    private const int DefaultWaitTimeoutMs = 300_000;
    private const int MaxWaitTimeoutMs = 3_600_000;
    private const int DisposeDrainTimeoutMs = 30_000;

    private readonly ConcurrentDictionary<string, JobEntry> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _runningTasks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _pool;
    /// <summary>
    /// 串行化实际 Worker 调用：共享 Runtime 依赖（Provider/Gateway/SessionStore）暂非线程安全。
    /// 入队仍可并行 pending；真正 InvokeSubAgent 一次一个。
    /// </summary>
    private readonly SemaphoreSlim _workerInvokeLock = new(1, 1);
    private readonly object _createGate = new();
    private readonly object _workerGate = new();
    private readonly int _maxConcurrent;
    private readonly int _maxJobsPerRun;
    private AgentJobWorker? _worker;
    private int _disposed;

    public AgentJobCoordinator(int? maxConcurrent = null, int? maxJobsPerRun = null)
    {
        _maxConcurrent = maxConcurrent is > 0 ? maxConcurrent.Value : DefaultMaxConcurrent;
        _maxJobsPerRun = maxJobsPerRun is > 0 ? maxJobsPerRun.Value : DefaultMaxJobsPerRun;
        _pool = new SemaphoreSlim(_maxConcurrent, _maxConcurrent);
    }

    public int MaxConcurrent => _maxConcurrent;

    public int MaxJobsPerRun => _maxJobsPerRun;

    public void BindWorker(AgentJobWorker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ThrowIfDisposed();
        lock (_workerGate)
        {
            _worker = worker;
        }
    }

    public AgentJobSnapshot Create(
        AgentJobCreateRequest request,
        string runId,
        string sessionId,
        string parentAgentId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentAgentId);
        ThrowIfDisposed();

        var normalized = NormalizeCreateRequest(request);
        EnsureWorkerBound();

        lock (_createGate)
        {
            ThrowIfDisposed();
            EnsureCapacity(1);
            return EnqueueCore(normalized, runId, sessionId, parentAgentId);
        }
    }

    public IReadOnlyList<AgentJobSnapshot> CreateMany(
        IReadOnlyList<AgentJobCreateRequest> requests,
        string runId,
        string sessionId,
        string parentAgentId)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            throw new ArgumentException("At least one job request is required.", nameof(requests));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentAgentId);
        ThrowIfDisposed();
        EnsureWorkerBound();

        // 先全部校验，避免半入队。
        var normalized = new List<AgentJobCreateRequest>(requests.Count);
        for (var i = 0; i < requests.Count; i++)
        {
            var item = requests[i]
                ?? throw new ArgumentException($"jobs[{i}] cannot be null.", nameof(requests));
            normalized.Add(NormalizeCreateRequest(item));
        }

        var created = new List<JobEntry>(normalized.Count);
        lock (_createGate)
        {
            ThrowIfDisposed();
            EnsureCapacity(normalized.Count);
            try
            {
                foreach (var request in normalized)
                {
                    var entry = CreateEntry(request, runId, sessionId, parentAgentId);
                    if (!_jobs.TryAdd(entry.JobId, entry))
                    {
                        entry.Dispose();
                        throw new InvalidOperationException($"Failed to register agent job '{entry.JobId}'.");
                    }

                    created.Add(entry);
                    StartJobTask(entry);
                }
            }
            catch
            {
                foreach (var entry in created)
                {
                    entry.TryRequestCancel();
                    _jobs.TryRemove(entry.JobId, out _);
                    _runningTasks.TryRemove(entry.JobId, out _);
                    entry.Dispose();
                }

                throw;
            }
        }

        return created.Select(static e => e.ToSnapshot()).ToArray();
    }

    public AgentJobSnapshot? GetSnapshot(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return null;
        }

        return _jobs.TryGetValue(jobId.Trim(), out var entry) ? entry.ToSnapshot() : null;
    }

    public AgentJobCollectResult Collect(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return new AgentJobCollectResult(
                JobId: string.Empty,
                Ready: false,
                Success: false,
                State: string.Empty,
                AgentName: string.Empty,
                Task: string.Empty,
                Result: null,
                Error: null,
                Message: "Job id cannot be empty.");
        }

        var trimmed = jobId.Trim();
        if (!_jobs.TryGetValue(trimmed, out var entry))
        {
            return new AgentJobCollectResult(
                JobId: trimmed,
                Ready: false,
                Success: false,
                State: string.Empty,
                AgentName: string.Empty,
                Task: string.Empty,
                Result: null,
                Error: null,
                Message: $"Agent job '{trimmed}' was not found.");
        }

        var snapshot = entry.ToSnapshot();
        if (!AgentJobStates.IsTerminal(snapshot.State))
        {
            return new AgentJobCollectResult(
                snapshot.JobId,
                Ready: false,
                Success: false,
                snapshot.State,
                snapshot.AgentName,
                snapshot.Task,
                Result: null,
                Error: null,
                Message: string.Equals(snapshot.State, AgentJobStates.Cancelling, StringComparison.Ordinal)
                    ? $"Agent job '{snapshot.JobId}' is cancelling."
                    : $"Agent job '{snapshot.JobId}' is not finished (state={snapshot.State}).");
        }

        var success = string.Equals(snapshot.State, AgentJobStates.Completed, StringComparison.Ordinal);
        return new AgentJobCollectResult(
            snapshot.JobId,
            Ready: true,
            Success: success,
            snapshot.State,
            snapshot.AgentName,
            snapshot.Task,
            snapshot.Result,
            snapshot.Error,
            success
                ? $"Agent job '{snapshot.JobId}' completed."
                : $"Agent job '{snapshot.JobId}' ended with state={snapshot.State}.");
    }

    public IReadOnlyList<AgentJobCollectResult> CollectMany(IReadOnlyList<string> jobIds)
    {
        ArgumentNullException.ThrowIfNull(jobIds);
        return jobIds.Select(Collect).ToArray();
    }

    public bool TryCancel(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)
            || !_jobs.TryGetValue(jobId.Trim(), out var entry))
        {
            return false;
        }

        return entry.TryRequestCancel();
    }

    public async Task<AgentJobWaitResult> WaitAsync(
        IReadOnlyList<string> jobIds,
        int timeoutMs,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jobIds);
        if (jobIds.Count == 0)
        {
            throw new ArgumentException("At least one job id is required.", nameof(jobIds));
        }

        ThrowIfDisposed();
        var waitMs = NormalizeWaitTimeout(timeoutMs);
        var ids = jobIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
        {
            throw new ArgumentException("At least one job id is required.", nameof(jobIds));
        }

        // 未知 id 立即失败，避免空等到超时。
        var missing = ids.Where(id => !_jobs.ContainsKey(id)).ToArray();
        if (missing.Length > 0)
        {
            throw new ArgumentException(
                $"Unknown agent job id(s): {string.Join(", ", missing)}",
                nameof(jobIds));
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(waitMs);

        var timedOut = false;
        try
        {
            while (true)
            {
                timeoutCts.Token.ThrowIfCancellationRequested();
                var snapshots = ids.Select(GetSnapshotRequired).ToArray();
                if (snapshots.All(static s => AgentJobStates.IsTerminal(s.State)))
                {
                    var allSucceeded = snapshots.All(static s =>
                        string.Equals(s.State, AgentJobStates.Completed, StringComparison.Ordinal));
                    return new AgentJobWaitResult(
                        AllTerminal: true,
                        TimedOut: false,
                        AllSucceeded: allSucceeded,
                        snapshots);
                }

                await Task.Delay(50, timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
        }

        var latest = ids.Select(GetSnapshotRequired).ToArray();
        var allTerminal = latest.All(static s => AgentJobStates.IsTerminal(s.State));
        var succeeded = allTerminal
            && latest.All(static s =>
                string.Equals(s.State, AgentJobStates.Completed, StringComparison.Ordinal));
        return new AgentJobWaitResult(
            allTerminal,
            timedOut && !allTerminal,
            succeeded,
            latest);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var entry in _jobs.Values)
        {
            entry.TryRequestCancel();
        }

        var tasks = _runningTasks.Values.ToArray();
        if (tasks.Length > 0)
        {
            var drain = Task.WhenAll(tasks);
            var completed = await Task.WhenAny(drain, Task.Delay(DisposeDrainTimeoutMs))
                .ConfigureAwait(false);
            if (!ReferenceEquals(completed, drain))
            {
                // 超时仍继续释放；Worker 应已收到 cancel。
            }
            else
            {
                try
                {
                    await drain.ConfigureAwait(false);
                }
                catch
                {
                    // 单 job 失败不应阻止 Dispose。
                }
            }
        }

        foreach (var entry in _jobs.Values)
        {
            entry.Dispose();
        }

        _pool.Dispose();
        _workerInvokeLock.Dispose();
    }

    private AgentJobSnapshot EnqueueCore(
        AgentJobCreateRequest normalized,
        string runId,
        string sessionId,
        string parentAgentId)
    {
        var entry = CreateEntry(normalized, runId, sessionId, parentAgentId);
        if (!_jobs.TryAdd(entry.JobId, entry))
        {
            entry.Dispose();
            throw new InvalidOperationException($"Failed to register agent job '{entry.JobId}'.");
        }

        StartJobTask(entry);
        return entry.ToSnapshot();
    }

    private void StartJobTask(JobEntry entry)
    {
        var task = Task.Run(() => ExecuteJobAsync(entry), CancellationToken.None);
        _runningTasks[entry.JobId] = task;
        _ = task.ContinueWith(
            t =>
            {
                _runningTasks.TryRemove(entry.JobId, out _);
                _ = t.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static JobEntry CreateEntry(
        AgentJobCreateRequest normalized,
        string runId,
        string sessionId,
        string parentAgentId)
    {
        var timeoutMs = NormalizeJobTimeout(normalized.TimeoutMs);
        var jobId = Guid.NewGuid().ToString("N");
        var cts = new CancellationTokenSource();
        return new JobEntry(
            jobId,
            normalized.AgentName,
            normalized.Task,
            runId,
            sessionId,
            parentAgentId,
            timeoutMs,
            cts);
    }

    private void EnsureCapacity(int additional)
    {
        var activeCount = _jobs.Values.Count(static e =>
        {
            var state = e.ToSnapshot().State;
            return !AgentJobStates.IsTerminal(state);
        });
        if (activeCount + additional > _maxJobsPerRun)
        {
            throw new InvalidOperationException(
                $"Agent job limit exceeded: active={activeCount}, requesting={additional}, maxPerRun={_maxJobsPerRun}.");
        }
    }

    private async Task ExecuteJobAsync(JobEntry entry)
    {
        AgentJobWorker? worker;
        lock (_workerGate)
        {
            worker = _worker;
        }

        if (worker is null)
        {
            entry.MarkFailed("Agent job worker is not bound.");
            return;
        }

        var acquiredPool = false;
        try
        {
            await _pool.WaitAsync(entry.Cancellation.Token).ConfigureAwait(false);
            acquiredPool = true;
        }
        catch (OperationCanceledException)
        {
            entry.MarkCancelled();
            return;
        }

        try
        {
            if (entry.IsCancelRequested)
            {
                entry.MarkCancelled();
                return;
            }

            if (!entry.TryMarkRunning())
            {
                if (entry.IsCancelRequested
                    || string.Equals(entry.CurrentState, AgentJobStates.Cancelling, StringComparison.Ordinal))
                {
                    entry.MarkCancelled();
                }

                return;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(entry.Cancellation.Token);
            timeoutCts.CancelAfter(entry.TimeoutMs);

            // 串行化 Worker，保护共享 Runtime 依赖。
            await _workerInvokeLock.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            try
            {
                if (entry.IsCancelRequested)
                {
                    entry.MarkCancelled();
                    return;
                }

                try
                {
                    var result = await worker(
                        entry.JobId,
                        entry.AgentName,
                        entry.Task,
                        entry.RunId,
                        entry.SessionId,
                        entry.ParentAgentId,
                        timeoutCts.Token).ConfigureAwait(false);

                    if (entry.IsCancelRequested)
                    {
                        entry.MarkCancelled();
                        return;
                    }

                    if (result.Success)
                    {
                        entry.MarkCompleted(result.ResultText ?? string.Empty);
                    }
                    else
                    {
                        entry.MarkFailed(result.Error ?? "Sub-agent failed to complete the task.");
                    }
                }
                catch (OperationCanceledException) when (entry.IsCancelRequested)
                {
                    entry.MarkCancelled();
                }
                catch (OperationCanceledException)
                {
                    entry.MarkTimedOut();
                }
                catch (Exception ex)
                {
                    entry.MarkFailed(ex.Message);
                }
            }
            finally
            {
                _workerInvokeLock.Release();
            }
        }
        catch (OperationCanceledException) when (entry.IsCancelRequested)
        {
            entry.MarkCancelled();
        }
        catch (OperationCanceledException)
        {
            entry.MarkTimedOut();
        }
        finally
        {
            if (acquiredPool)
            {
                _pool.Release();
            }
        }
    }

    private AgentJobSnapshot GetSnapshotRequired(string jobId)
    {
        var snapshot = GetSnapshot(jobId);
        if (snapshot is null)
        {
            throw new InvalidOperationException($"Agent job '{jobId}' disappeared during wait.");
        }

        return snapshot;
    }

    private void EnsureWorkerBound()
    {
        lock (_workerGate)
        {
            if (_worker is null)
            {
                throw new InvalidOperationException(
                    "Agent job worker is not bound. Parallel agent jobs require Expert mode with a bound worker.");
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private static AgentJobCreateRequest NormalizeCreateRequest(AgentJobCreateRequest request)
    {
        var agentName = request.AgentName?.Trim() ?? string.Empty;
        var task = request.Task?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(agentName))
        {
            throw new ArgumentException("Agent name cannot be empty.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(task))
        {
            throw new ArgumentException("Task description cannot be empty.", nameof(request));
        }

        return new AgentJobCreateRequest(agentName, task, request.TimeoutMs);
    }

    private static int NormalizeJobTimeout(int? timeoutMs)
    {
        if (timeoutMs is null or <= 0)
        {
            return DefaultJobTimeoutMs;
        }

        return Math.Clamp(timeoutMs.Value, 1_000, MaxJobTimeoutMs);
    }

    private static int NormalizeWaitTimeout(int timeoutMs)
    {
        if (timeoutMs <= 0)
        {
            return DefaultWaitTimeoutMs;
        }

        return Math.Clamp(timeoutMs, 100, MaxWaitTimeoutMs);
    }

    private sealed class JobEntry : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cts;
        private string _state = AgentJobStates.Pending;
        private string? _result;
        private string? _error;
        private DateTimeOffset? _startedAt;
        private DateTimeOffset? _completedAt;
        private bool _cancelRequested;
        private bool _disposed;

        public JobEntry(
            string jobId,
            string agentName,
            string task,
            string runId,
            string sessionId,
            string parentAgentId,
            int timeoutMs,
            CancellationTokenSource cts)
        {
            JobId = jobId;
            AgentName = agentName;
            Task = task;
            RunId = runId;
            SessionId = sessionId;
            ParentAgentId = parentAgentId;
            TimeoutMs = timeoutMs;
            _cts = cts;
            CreatedAt = DateTimeOffset.UtcNow;
        }

        public string JobId { get; }
        public string AgentName { get; }
        public string Task { get; }
        public string RunId { get; }
        public string SessionId { get; }
        public string ParentAgentId { get; }
        public int TimeoutMs { get; }
        public DateTimeOffset CreatedAt { get; }
        public CancellationTokenSource Cancellation => _cts;

        public bool IsCancelRequested
        {
            get
            {
                lock (_gate)
                {
                    return _cancelRequested;
                }
            }
        }

        public string CurrentState
        {
            get
            {
                lock (_gate)
                {
                    return _state;
                }
            }
        }

        public AgentJobSnapshot ToSnapshot()
        {
            lock (_gate)
            {
                return new AgentJobSnapshot(
                    JobId,
                    _state,
                    AgentName,
                    Task,
                    _result,
                    _error,
                    RunId,
                    SessionId,
                    CreatedAt,
                    _startedAt,
                    _completedAt);
            }
        }

        public bool TryMarkRunning()
        {
            lock (_gate)
            {
                if (_cancelRequested
                    || !string.Equals(_state, AgentJobStates.Pending, StringComparison.Ordinal))
                {
                    return false;
                }

                _state = AgentJobStates.Running;
                _startedAt = DateTimeOffset.UtcNow;
                return true;
            }
        }

        public void MarkCompleted(string result)
        {
            lock (_gate)
            {
                if (AgentJobStates.IsTerminal(_state))
                {
                    return;
                }

                if (_cancelRequested)
                {
                    _state = AgentJobStates.Cancelled;
                    _error = "Agent job was cancelled.";
                    _result = null;
                    _completedAt = DateTimeOffset.UtcNow;
                    return;
                }

                _state = AgentJobStates.Completed;
                _result = result;
                _error = null;
                _completedAt = DateTimeOffset.UtcNow;
            }
        }

        public void MarkFailed(string error)
        {
            lock (_gate)
            {
                if (AgentJobStates.IsTerminal(_state))
                {
                    return;
                }

                if (_cancelRequested)
                {
                    _state = AgentJobStates.Cancelled;
                    _error = "Agent job was cancelled.";
                    _result = null;
                    _completedAt = DateTimeOffset.UtcNow;
                    return;
                }

                _state = AgentJobStates.Failed;
                _error = error;
                _completedAt = DateTimeOffset.UtcNow;
            }
        }

        public void MarkTimedOut()
        {
            lock (_gate)
            {
                if (AgentJobStates.IsTerminal(_state))
                {
                    return;
                }

                if (_cancelRequested)
                {
                    _state = AgentJobStates.Cancelled;
                    _error = "Agent job was cancelled.";
                    _result = null;
                    _completedAt = DateTimeOffset.UtcNow;
                    return;
                }

                _state = AgentJobStates.TimedOut;
                _error = "Agent job timed out.";
                _completedAt = DateTimeOffset.UtcNow;
            }
        }

        public void MarkCancelled()
        {
            lock (_gate)
            {
                if (AgentJobStates.IsTerminal(_state))
                {
                    return;
                }

                _state = AgentJobStates.Cancelled;
                _error = "Agent job was cancelled.";
                _result = null;
                _completedAt = DateTimeOffset.UtcNow;
            }
        }

        /// <summary>
        /// 请求取消：非终态进入 cancelling 并触发 CTS；Worker 退出后 MarkCancelled。
        /// </summary>
        public bool TryRequestCancel()
        {
            lock (_gate)
            {
                if (AgentJobStates.IsTerminal(_state))
                {
                    return false;
                }

                if (_cancelRequested
                    && string.Equals(_state, AgentJobStates.Cancelling, StringComparison.Ordinal))
                {
                    return true;
                }

                _cancelRequested = true;
                if (!string.Equals(_state, AgentJobStates.Cancelling, StringComparison.Ordinal))
                {
                    _state = AgentJobStates.Cancelling;
                }
            }

            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cts.Dispose();
        }
    }
}
