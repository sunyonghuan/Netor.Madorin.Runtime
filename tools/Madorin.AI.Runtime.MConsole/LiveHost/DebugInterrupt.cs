namespace Madorin.AI.Runtime.MConsole.LiveHost;

/// <summary>
/// 把 Ctrl+C 先交给正在执行的一轮。没有运行中的轮次时，调用方再退出整个程序。
/// </summary>
public static class DebugInterrupt
{
    private static CancellationTokenSource? _run;

    public static void Bind(CancellationTokenSource? run) => _run = run;

    public static bool TryConsume()
    {
        var run = _run;
        if (run is null || run.IsCancellationRequested)
        {
            return false;
        }

        run.Cancel();
        return true;
    }
}
