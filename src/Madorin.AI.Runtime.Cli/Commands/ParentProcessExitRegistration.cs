using System.Diagnostics;
using System.Globalization;

namespace Madorin.AI.Runtime.Cli.Commands;

/// <summary>
/// 宿主通过环境变量告知自己的进程号。宿主退出后取消 serve，使 Runtime 能先关闭 Provider。
/// 独立启动不设置该变量，因此不会被误关。
/// </summary>
internal static class ParentProcessExitRegistration
{
    public const string HostProcessIdVariable = "MADORIN_HOST_PID";

    public static IDisposable Track(CancellationTokenSource shutdown)
    {
        ArgumentNullException.ThrowIfNull(shutdown);
        var raw = Environment.GetEnvironmentVariable(HostProcessIdVariable);
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parentId)
            || parentId <= 0
            || parentId == Environment.ProcessId)
        {
            return EmptyRegistration.Instance;
        }

        try
        {
            return new ExitWatcher(parentId, shutdown);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Cancel(shutdown);
            return EmptyRegistration.Instance;
        }
    }

    private static void Cancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class ExitWatcher : IDisposable
    {
        private readonly Process _parent;
        private readonly CancellationTokenSource _shutdown;

        public ExitWatcher(int parentId, CancellationTokenSource shutdown)
        {
            _shutdown = shutdown;
            _parent = Process.GetProcessById(parentId);
            if (_parent.HasExited)
            {
                Cancel(shutdown);
                return;
            }

            _parent.EnableRaisingEvents = true;
            _parent.Exited += OnExited;
        }

        private void OnExited(object? sender, EventArgs e) => Cancel(_shutdown);

        public void Dispose()
        {
            _parent.Exited -= OnExited;
            _parent.Dispose();
        }
    }

    private sealed class EmptyRegistration : IDisposable
    {
        public static readonly EmptyRegistration Instance = new();

        public void Dispose()
        {
        }
    }
}
