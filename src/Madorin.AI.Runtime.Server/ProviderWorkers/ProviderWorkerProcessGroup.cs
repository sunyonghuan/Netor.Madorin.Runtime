using System.Diagnostics;

namespace Madorin.AI.Runtime.Server.ProviderWorkers;

/// <summary>
/// 每个 Runtime 一份进程组：Windows Job Object（KillOnJobClose）或 Unix pgid，禁止跨 Runtime 误杀。
/// </summary>
internal sealed partial class ProviderWorkerProcessGroup : IDisposable
{
    private bool _disposed;

    public void Assign(Process process)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(process);
        if (OperatingSystem.IsWindows())
        {
            AssignWindows(process);
            return;
        }

        AssignUnix(process);
    }

    public bool Contains(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (OperatingSystem.IsWindows())
        {
            return ContainsWindows(process);
        }

        return ContainsUnix(process);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (OperatingSystem.IsWindows())
        {
            DisposeWindows();
            return;
        }

        DisposeUnix();
    }
}
