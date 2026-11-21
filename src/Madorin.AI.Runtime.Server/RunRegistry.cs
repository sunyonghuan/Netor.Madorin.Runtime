using System.Collections.Concurrent;
using System.Diagnostics;

namespace Madorin.AI.Runtime.Server;

internal enum RunCapacityResult
{
    Acquired,
    GlobalCapacityReached,
    ProviderCapacityReached,
    SessionCapacityReached
}

internal sealed class RunRegistry : IDisposable
{
    private readonly SemaphoreSlim _globalCapacity;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _providerCapacities =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionCapacities =
        new(StringComparer.Ordinal);
    private readonly int _maxConcurrentRunsPerProvider;
    private readonly int _maxConcurrentRunsPerSession;
    private readonly TimeSpan _runTimeout;
    private readonly CancellationToken _shutdownToken;
    private int _disposed;

    public RunRegistry(
        int maxConcurrentRuns,
        int maxConcurrentRunsPerProvider,
        int maxConcurrentRunsPerSession,
        TimeSpan runTimeout,
        CancellationToken shutdownToken)
    {
        _globalCapacity = new SemaphoreSlim(maxConcurrentRuns, maxConcurrentRuns);
        _maxConcurrentRunsPerProvider = maxConcurrentRunsPerProvider;
        _maxConcurrentRunsPerSession = maxConcurrentRunsPerSession;
        _runTimeout = runTimeout;
        _shutdownToken = shutdownToken;
    }

    public RunLease? TryAcquire(string providerId, out RunCapacityResult result)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        if (!_globalCapacity.Wait(0))
        {
            result = RunCapacityResult.GlobalCapacityReached;
            return null;
        }

        var providerCapacity = _providerCapacities.GetOrAdd(
            providerId,
            _ => new SemaphoreSlim(
                _maxConcurrentRunsPerProvider,
                _maxConcurrentRunsPerProvider));
        if (!providerCapacity.Wait(0))
        {
            _globalCapacity.Release();
            result = RunCapacityResult.ProviderCapacityReached;
            return null;
        }

        try
        {
            result = RunCapacityResult.Acquired;
            return new RunLease(
                this,
                providerId,
                _runTimeout,
                _shutdownToken);
        }
        catch
        {
            providerCapacity.Release();
            _globalCapacity.Release();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _globalCapacity.Dispose();
        foreach (var providerCapacity in _providerCapacities.Values)
        {
            providerCapacity.Dispose();
        }

        foreach (var sessionCapacity in _sessionCapacities.Values)
        {
            sessionCapacity.Dispose();
        }

        _providerCapacities.Clear();
        _sessionCapacities.Clear();
    }

    private bool TryAcquireSession(string sessionId)
    {
        var sessionCapacity = _sessionCapacities.GetOrAdd(
            sessionId,
            _ => new SemaphoreSlim(
                _maxConcurrentRunsPerSession,
                _maxConcurrentRunsPerSession));
        return sessionCapacity.Wait(0);
    }

    private void Release(string providerId, string? sessionId)
    {
        if (sessionId is not null
            && _sessionCapacities.TryGetValue(sessionId, out var sessionCapacity))
        {
            sessionCapacity.Release();
        }

        if (_providerCapacities.TryGetValue(providerId, out var providerCapacity))
        {
            providerCapacity.Release();
        }

        _globalCapacity.Release();
    }

    internal sealed class RunLease : IDisposable
    {
        private readonly RunRegistry _owner;
        private readonly string _providerId;
        private readonly CancellationTokenSource _cancellation;
        private readonly Timer _timeoutTimer;
        private readonly long _inactivityTimeoutMs;
        private long _lastActivityTick;
        private int _waiting;
        private string? _sessionId;
        private int _cancelRequested;
        private int _timedOut;
        private int _disposed;

        public RunLease(
            RunRegistry owner,
            string providerId,
            TimeSpan timeout,
            CancellationToken shutdownToken)
        {
            _owner = owner;
            _providerId = providerId;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
            _inactivityTimeoutMs = (long)timeout.TotalMilliseconds;
            _lastActivityTick = Environment.TickCount64;
            // 不活动看门狗：连续 timeout 时长没有任何运行事件才判定 Run 死亡；
            // 等待工具、审批或凭据期间暂停计时，恢复后重新获得完整窗口。
            var checkInterval = TimeSpan.FromMilliseconds(Math.Max(_inactivityTimeoutMs / 4, 1));
            _timeoutTimer = new Timer(
                static state => ((RunLease)state!).CheckInactivity(),
                this,
                checkInterval,
                checkInterval);
        }

        public CancellationToken CancellationToken => _cancellation.Token;

        public bool IsTimedOut => Volatile.Read(ref _timedOut) != 0;

        /// <summary>报告一次运行事件，重置不活动计时。</summary>
        public void ReportActivity()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Volatile.Write(ref _lastActivityTick, Environment.TickCount64);
        }

        /// <summary>进入或离开等待态（工具/审批/凭据）；等待期间不活动看门狗不计时。</summary>
        public void SetWaiting(bool waiting)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Volatile.Write(ref _waiting, waiting ? 1 : 0);
            Volatile.Write(ref _lastActivityTick, Environment.TickCount64);
        }

        public bool Cancel()
        {
            if (Interlocked.Exchange(ref _cancelRequested, 1) != 0
                || Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            try
            {
                CancelNoThrow(_cancellation);
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        public bool TryBindSession(string sessionId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (Volatile.Read(ref _sessionId) is not null)
            {
                throw new InvalidOperationException("The Run lease is already bound to a Session.");
            }

            if (!_owner.TryAcquireSession(sessionId))
            {
                return false;
            }

            _sessionId = sessionId;
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _timeoutTimer.Dispose();
            _cancellation.Dispose();
            _owner.Release(_providerId, _sessionId);
        }

        private void CheckInactivity()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            var now = Environment.TickCount64;
            if (Volatile.Read(ref _waiting) != 0)
            {
                Volatile.Write(ref _lastActivityTick, now);
                return;
            }

            if (now - Volatile.Read(ref _lastActivityTick) >= _inactivityTimeoutMs)
            {
                CancelForTimeout();
            }
        }

        private void CancelForTimeout()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _timedOut, 1);
            CancelNoThrow(_cancellation);
        }

        private static void CancelNoThrow(CancellationTokenSource cancellation)
        {
            try
            {
                cancellation.Cancel(throwOnFirstException: false);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (AggregateException exception)
            {
                var diagnosticId = $"diag_cancel_{Guid.NewGuid():N}";
                Trace.TraceWarning(
                    "Cancellation callback failure type={0} diagnosticId={1}",
                    exception.GetType().FullName,
                    diagnosticId);
            }
        }
    }
}
