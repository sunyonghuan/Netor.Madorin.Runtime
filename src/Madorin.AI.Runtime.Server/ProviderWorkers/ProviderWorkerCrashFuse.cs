namespace Madorin.AI.Runtime.Server.ProviderWorkers;

/// <summary>
/// 第 9 节崩溃滑动窗口：30s 内 ≥ 3 次非停止顺序退出后，该 slug 熔断直到 Runtime 重启。
/// </summary>
internal sealed class ProviderWorkerCrashFuse
{
    internal const int WindowSeconds = 30;
    internal const int Threshold = 3;

    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly Queue<DateTimeOffset> _crashes = new();
    private bool _fused;

    public ProviderWorkerCrashFuse(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsFused
    {
        get
        {
            lock (_sync)
            {
                return _fused;
            }
        }
    }

    public bool RecordCrash()
    {
        lock (_sync)
        {
            if (_fused)
            {
                return true;
            }

            var now = _timeProvider.GetUtcNow();
            _crashes.Enqueue(now);
            var cutoff = now - TimeSpan.FromSeconds(WindowSeconds);
            while (_crashes.Count > 0 && _crashes.Peek() < cutoff)
            {
                _crashes.Dequeue();
            }

            if (_crashes.Count >= Threshold)
            {
                _fused = true;
            }

            return _fused;
        }
    }
}
