using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;

namespace Madorin.AI.Runtime.Server;

/// <summary>Owns the process-wide write lease for one normalized workspace.</summary>
public sealed partial class WorkspaceWriteLock : IAsyncDisposable
{
    private const int UnixLockExclusive = 2;
    private const int UnixLockNonBlocking = 4;
    private const int UnixLockUnlock = 8;
    private readonly FileStream _stream;
    private readonly string _holderPath;
    private int _disposed;

    private WorkspaceWriteLock(FileStream stream, string lockPath, string holderPath)
    {
        _stream = stream;
        LockPath = lockPath;
        _holderPath = holderPath;
    }

    /// <summary>Gets the system-temporary lock file used by this lease.</summary>
    public string LockPath { get; }

    /// <summary>Acquires the write lease for a workspace.</summary>
    public static async Task<WorkspaceWriteLock> AcquireAsync(
        string workspaceRoot,
        string runtimeInstanceId,
        TimeSpan? waitTimeout = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
        var timeout = waitTimeout ?? TimeSpan.Zero;
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));
        }

        var normalizedWorkspace = NormalizeWorkspace(workspaceRoot);
        var workspaceKey = ComputeWorkspaceKey(normalizedWorkspace);
        var lockDirectory = Path.Combine(
            Path.GetTempPath(),
            "madorin.ai.runtime",
            "workspace-locks");
        Directory.CreateDirectory(lockDirectory);
        SetUnixMode(
            lockDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var lockPath = Path.Combine(lockDirectory, workspaceKey + ".lock");
        var holderPath = lockPath + ".holder";
        var deadline = DateTimeOffset.UtcNow + timeout;
        string? holder = null;
        var staleRecoveries = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            FileStream? stream = null;
            try
            {
                stream = new FileStream(lockPath, new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    // 外部可以读取持有者信息。排他性由下方的字节锁保证。
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.WriteThrough
                });
                // FileShare is not an inter-process lock on Unix. Keep the same
                // exclusive lease semantics on every supported platform.
                AcquireExclusiveLock(stream);
                SetUnixMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var metadata = CreateHolderMetadata(runtimeInstanceId);
                await WriteHolderMetadataAsync(stream, metadata, ct).ConfigureAwait(false);
                await File.WriteAllTextAsync(holderPath, metadata, Encoding.UTF8, ct)
                    .ConfigureAwait(false);
                SetUnixMode(holderPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return new WorkspaceWriteLock(stream, lockPath, holderPath);
            }
            catch (IOException)
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }

                holder = await TryReadHolderAsync(holderPath, lockPath, ct).ConfigureAwait(false);
                if (staleRecoveries < 2 && TryRecoverStaleLock(holder, holderPath))
                {
                    staleRecoveries++;
                    continue;
                }

                if (timeout == TimeSpan.Zero || DateTimeOffset.UtcNow >= deadline)
                {
                    throw CreateBusyException(lockPath, holder);
                }

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    continue;
                }

                await Task.Delay(
                        remaining < TimeSpan.FromMilliseconds(50)
                            ? remaining
                            : TimeSpan.FromMilliseconds(50),
                        ct)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            try
            {
                File.Delete(_holderPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The lock itself remains authoritative if stale diagnostics cannot be removed.
            }

            ReleaseExclusiveLock(_stream);
        }
        finally
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static string NormalizeWorkspace(string workspaceRoot)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    internal static string ComputeWorkspaceKey(string normalizedWorkspace) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedWorkspace)));

    private static string CreateHolderMetadata(string runtimeInstanceId) =>
        $"runtimeInstanceId={runtimeInstanceId}\n" +
        $"pid={Environment.ProcessId}\n" +
        $"startedAt={DateTimeOffset.UtcNow:O}\n";

    private static async Task WriteHolderMetadataAsync(
        FileStream stream,
        string metadata,
        CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(metadata);
        stream.SetLength(0);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<string?> TryReadHolderAsync(
        string holderPath,
        string lockPath,
        CancellationToken ct)
    {
        try
        {
            return (await File.ReadAllTextAsync(holderPath, ct).ConfigureAwait(false)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            try
            {
                await using var stream = new FileStream(lockPath, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.ReadWrite,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return (await reader.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
            }
            catch (Exception fallback) when (fallback is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 持有者进程已经退出时，操作系统会释放字节锁。
    /// 删掉过期说明后立刻重试，避免把死进程的锁当成占用。
    /// </summary>
    private static bool TryRecoverStaleLock(string? holder, string holderPath)
    {
        if (!TryParseHolder(holder, out _, out var processId, out _)
            || processId is not int pid
            || IsProcessAlive(pid))
        {
            return false;
        }

        try
        {
            File.Delete(holderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 说明文件删不掉不影响下一次获取字节锁。
        }

        return true;
    }

    private static WorkspaceBusyException CreateBusyException(string lockPath, string? holder)
    {
        if (!TryParseHolder(holder, out var runtimeInstanceId, out var processId, out var startedAt))
        {
            runtimeInstanceId = null;
            processId = null;
            startedAt = null;
        }
        var alive = processId is int pid && IsProcessAlive(pid);
        var visibleHolder = string.IsNullOrWhiteSpace(holder)
            ? "unavailable"
            : holder.Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);
        var processState = processId is null
            ? string.Empty
            : alive
                ? " The holder process is still running."
                : " The holder process is not running.";
        return new WorkspaceBusyException(
            lockPath,
            runtimeInstanceId,
            processId,
            startedAt,
            "The workspace already has an active Runtime instance. " +
            $"Lock: '{lockPath}'. Holder: {visibleHolder}.{processState}");
    }

    private static bool TryParseHolder(
        string? holder,
        out string? runtimeInstanceId,
        out int? processId,
        out DateTimeOffset? startedAt)
    {
        runtimeInstanceId = null;
        processId = null;
        startedAt = null;
        if (string.IsNullOrWhiteSpace(holder))
        {
            return false;
        }

        foreach (var rawLine in holder.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
            {
                continue;
            }

            var name = line[..separator];
            var value = line[(separator + 1)..];
            if (string.Equals(name, "runtimeInstanceId", StringComparison.Ordinal))
            {
                runtimeInstanceId = value;
            }
            else if (string.Equals(name, "pid", StringComparison.Ordinal)
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                processId = pid;
            }
            else if (string.Equals(name, "startedAt", StringComparison.Ordinal)
                && DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var started))
            {
                startedAt = started;
            }
        }

        return runtimeInstanceId is not null || processId is not null || startedAt is not null;
    }

    private static bool IsProcessAlive(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void SetUnixMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }

    private static void AcquireExclusiveLock(FileStream stream)
    {
        if (OperatingSystem.IsWindows())
        {
            stream.Lock(0, 1);
            return;
        }

        var descriptor = checked((int)stream.SafeFileHandle.DangerousGetHandle().ToInt64());
        if (Flock(descriptor, UnixLockExclusive | UnixLockNonBlocking) == 0)
        {
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        throw new IOException($"The workspace lock could not be acquired (errno {error}).", error);
    }

    private static void ReleaseExclusiveLock(FileStream stream)
    {
        if (OperatingSystem.IsWindows())
        {
            stream.Unlock(0, 1);
            return;
        }

        var descriptor = checked((int)stream.SafeFileHandle.DangerousGetHandle().ToInt64());
        _ = Flock(descriptor, UnixLockUnlock);
    }

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int descriptor, int operation);
}
