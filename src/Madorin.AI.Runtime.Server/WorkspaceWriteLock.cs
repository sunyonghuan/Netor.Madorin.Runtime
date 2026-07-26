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
                    Share = FileShare.ReadWrite,
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
                if (timeout == TimeSpan.Zero || DateTimeOffset.UtcNow >= deadline)
                {
                    throw new InvalidOperationException(
                        $"The workspace already has an active Runtime instance. " +
                        $"Lock: '{lockPath}'. Holder: {holder ?? "unavailable"}.");
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
