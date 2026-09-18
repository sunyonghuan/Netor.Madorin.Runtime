using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Madorin.AI.Runtime.Server.ProviderWorkers;

internal sealed partial class ProviderWorkerProcessGroup
{
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private SafeJobHandle? _job;

    [SupportedOSPlatform("windows")]
    private void AssignWindows(Process process)
    {
        EnsureWindowsJob();
        var job = _job;
        if (job is null || job.IsInvalid || job.IsClosed)
        {
            return;
        }

        try
        {
            if (!AssignProcessToJobObject(job, process.SafeHandle))
            {
                Trace.WriteLine(
                    $"AssignProcessToJobObject failed for PID {process.Id} (error {Marshal.GetLastPInvokeError()}).");
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private bool ContainsWindows(Process process)
    {
        var job = _job;
        if (job is null || job.IsInvalid || job.IsClosed)
        {
            return false;
        }

        try
        {
            return IsProcessInJob(process.SafeHandle, job, out var result) && result;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private void DisposeWindows()
    {
        _job?.Dispose();
        _job = null;
    }

    [SupportedOSPlatform("windows")]
    private void EnsureWindowsJob()
    {
        if (_job is { IsInvalid: false, IsClosed: false })
        {
            return;
        }

        var job = CreateJobObjectW(0, null);
        if (job.IsInvalid)
        {
            job.Dispose();
            Trace.WriteLine($"CreateJobObjectW failed (error {Marshal.GetLastPInvokeError()}).");
            return;
        }

        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };
        var size = (uint)Unsafe.SizeOf<JobObjectExtendedLimitInformation>();
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, in info, size))
        {
            Trace.WriteLine($"SetInformationJobObject failed (error {Marshal.GetLastPInvokeError()}).");
            job.Dispose();
            return;
        }

        _job = job;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [SupportedOSPlatform("windows")]
    private static partial SafeJobHandle CreateJobObjectW(nint jobAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeJobHandle job,
        int jobObjectInformationClass,
        in JobObjectExtendedLimitInformation jobObjectInformation,
        uint jobObjectInformationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(
        SafeProcessHandle process,
        SafeJobHandle job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [SupportedOSPlatform("windows")]
    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
}
