using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Madorin.AI.Runtime.Server.ProviderWorkers;

internal sealed partial class ProviderWorkerProcessGroup
{
    private const int SigKill = 9;

    private int? _pgid;

    [UnsupportedOSPlatform("windows")]
    private void AssignUnix(Process process)
    {
        int pid;
        try
        {
            pid = process.Id;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (_pgid is null)
        {
            if (SetPgid(pid, pid) != 0)
            {
                Trace.WriteLine($"setpgid({pid}, {pid}) failed (errno {Marshal.GetLastPInvokeError()}).");
                return;
            }

            _pgid = pid;
            return;
        }

        var pgid = _pgid.Value;
        if (SetPgid(pid, pgid) == 0)
        {
            return;
        }

        if (SetPgid(pid, pid) != 0)
        {
            Trace.WriteLine($"setpgid({pid}, {pid}) fallback failed (errno {Marshal.GetLastPInvokeError()}).");
            return;
        }

        _pgid = pid;
    }

    [UnsupportedOSPlatform("windows")]
    private bool ContainsUnix(Process process)
    {
        if (_pgid is null)
        {
            return false;
        }

        try
        {
            var pgid = GetPgid(process.Id);
            return pgid >= 0 && pgid == _pgid.Value;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    [UnsupportedOSPlatform("windows")]
    private void DisposeUnix()
    {
        if (_pgid is not int pgid)
        {
            return;
        }

        _ = Kill(-pgid, SigKill);
        _pgid = null;
    }

    [LibraryImport("libc", EntryPoint = "setpgid", SetLastError = true)]
    [UnsupportedOSPlatform("windows")]
    private static partial int SetPgid(int pid, int pgid);

    [LibraryImport("libc", EntryPoint = "getpgid", SetLastError = true)]
    [UnsupportedOSPlatform("windows")]
    private static partial int GetPgid(int pid);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    [UnsupportedOSPlatform("windows")]
    private static partial int Kill(int pid, int signal);
}
