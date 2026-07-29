using System.Security.Cryptography;
using System.Text.Json;
using Madorin.AI.Runtime.Server;

namespace Madorin.AI.Runtime.Cli.Config;

/// <summary>
/// Prepares workspace-local standalone data and migrates the former user-level data layout.
/// </summary>
public static class StandaloneWorkspaceDataMigrator
{
    public const string MigrationMarkerFileName = ".workspace-data-migration-v1.json";

    private static readonly HashSet<string> RuntimeEntryNames = new(
        [
            "state.db",
            "state.db-shm",
            "state.db-wal",
            "messages",
            "blobs",
            "checkpoints",
            "staging",
            "replay",
            "locks",
            "logs",
            "backups"
        ],
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Prepares the resolved data directory and performs an idempotent migration.</summary>
    public static async Task PrepareAsync(
        StandaloneRuntimeContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        if (!context.UsesDefaultDataDirectory || context.LegacyDataDirectory is null)
        {
            return;
        }

        var legacyDataDirectory = context.LegacyDataDirectory;
        if (!Directory.Exists(legacyDataDirectory)
            || !Directory.EnumerateFileSystemEntries(legacyDataDirectory).Any())
        {
            return;
        }

        try
        {
            await using var workspaceLock = await WorkspaceWriteLock.AcquireAsync(
                    context.WorkspaceRoot,
                    context.RuntimeInstanceId + "-data-migration",
                    TimeSpan.FromSeconds(5),
                    ct)
                .ConfigureAwait(false);
            await MigrateUnderLockAsync(context, legacyDataDirectory, ct).ConfigureAwait(false);
        }
        catch (StandaloneWorkspaceDataMigrationException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            throw new StandaloneWorkspaceDataMigrationException(
                "Legacy workspace data could not be migrated because the workspace is active in another Runtime instance.",
                ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StandaloneWorkspaceDataMigrationException(
                $"Legacy workspace data could not be migrated from '{legacyDataDirectory}' " +
                $"to '{context.DataDirectory}'. The legacy data was retained.",
                ex);
        }
    }

    private static async Task MigrateUnderLockAsync(
        StandaloneRuntimeContext context,
        string legacyDataDirectory,
        CancellationToken ct)
    {
        var markerPath = Path.Combine(context.DataDirectory, MigrationMarkerFileName);
        if (File.Exists(markerPath))
        {
            return;
        }

        if (!Directory.Exists(legacyDataDirectory)
            || !Directory.EnumerateFileSystemEntries(legacyDataDirectory).Any())
        {
            return;
        }

        if (ContainsRuntimeData(context.DataDirectory))
        {
            throw new StandaloneWorkspaceDataMigrationException(
                $"Runtime data exists in both '{legacyDataDirectory}' and " +
                $"'{context.DataDirectory}'. Automatic merging is refused; both locations were left unchanged.");
        }

        var stagingDirectory = Path.Combine(
            context.WorkspaceRoot,
            ".madorin-data-migration-" + Guid.NewGuid().ToString("N"));
        EnsureDirectChild(context.WorkspaceRoot, stagingDirectory);
        var destinationExisted = Directory.Exists(context.DataDirectory);
        var movedEntries = new List<(string Destination, string Staged)>();
        var retainStagingDirectory = false;

        try
        {
            await CopyDirectoryVerifiedAsync(legacyDataDirectory, stagingDirectory, ct)
                .ConfigureAwait(false);
            EnsureNoDestinationConflicts(stagingDirectory, context.DataDirectory);
            Directory.CreateDirectory(context.DataDirectory);

            foreach (var stagedEntry in Directory.EnumerateFileSystemEntries(stagingDirectory))
            {
                ct.ThrowIfCancellationRequested();
                var destinationEntry = Path.Combine(
                    context.DataDirectory,
                    Path.GetFileName(stagedEntry));
                MoveEntry(stagedEntry, destinationEntry);
                movedEntries.Add((destinationEntry, stagedEntry));
            }

            await WriteMarkerAsync(markerPath, legacyDataDirectory, ct).ConfigureAwait(false);
        }
        catch (Exception migrationException)
        {
            try
            {
                RollBackMovedEntries(movedEntries);
            }
            catch (StandaloneWorkspaceDataMigrationException rollbackException)
            {
                retainStagingDirectory = true;
                throw new StandaloneWorkspaceDataMigrationException(
                    $"Migration failed and rollback was incomplete. The legacy source and " +
                    $"staging directory '{stagingDirectory}' were retained for recovery.",
                    new AggregateException(migrationException, rollbackException));
            }

            if (!destinationExisted)
            {
                TryDeleteEmptyDirectory(context.DataDirectory);
            }

            throw;
        }
        finally
        {
            if (!retainStagingDirectory)
            {
                TryDeleteMigrationStaging(stagingDirectory, context.WorkspaceRoot);
            }
        }
    }

    private static bool ContainsRuntimeData(string dataDirectory)
    {
        if (!Directory.Exists(dataDirectory))
        {
            return false;
        }

        return Directory.EnumerateFileSystemEntries(dataDirectory)
            .Select(Path.GetFileName)
            .Any(name => name is not null && RuntimeEntryNames.Contains(name));
    }

    private static void EnsureNoDestinationConflicts(
        string stagingDirectory,
        string destinationDirectory)
    {
        if (!Directory.Exists(destinationDirectory))
        {
            return;
        }

        foreach (var stagedEntry in Directory.EnumerateFileSystemEntries(stagingDirectory))
        {
            var destinationEntry = Path.Combine(
                destinationDirectory,
                Path.GetFileName(stagedEntry));
            if (File.Exists(destinationEntry) || Directory.Exists(destinationEntry))
            {
                throw new StandaloneWorkspaceDataMigrationException(
                    $"Legacy entry '{Path.GetFileName(stagedEntry)}' already exists in " +
                    $"'{destinationDirectory}'. Automatic merging is refused.");
            }
        }
    }

    private static async Task CopyDirectoryVerifiedAsync(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken ct)
    {
        ThrowIfReparsePoint(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);

        foreach (var sourceEntry in Directory.EnumerateFileSystemEntries(sourceDirectory))
        {
            ct.ThrowIfCancellationRequested();
            ThrowIfReparsePoint(sourceEntry);
            var destinationEntry = Path.Combine(destinationDirectory, Path.GetFileName(sourceEntry));
            if (Directory.Exists(sourceEntry))
            {
                await CopyDirectoryVerifiedAsync(sourceEntry, destinationEntry, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await CopyFileVerifiedAsync(sourceEntry, destinationEntry, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task CopyFileVerifiedAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken ct)
    {
        await using (var source = OpenRead(sourcePath))
        await using (var destination = new FileStream(destinationPath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 81920,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        }))
        {
            await source.CopyToAsync(destination, ct).ConfigureAwait(false);
            await destination.FlushAsync(ct).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
        }

        var sourceInfo = new FileInfo(sourcePath);
        var destinationInfo = new FileInfo(destinationPath);
        if (sourceInfo.Length != destinationInfo.Length)
        {
            throw new IOException($"Length verification failed for '{sourcePath}'.");
        }

        var sourceHash = await ComputeHashAsync(sourcePath, ct).ConfigureAwait(false);
        var destinationHash = await ComputeHashAsync(destinationPath, ct).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash))
        {
            throw new IOException($"SHA-256 verification failed for '{sourcePath}'.");
        }
    }

    private static FileStream OpenRead(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.Read,
        BufferSize = 81920,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan
    });

    private static async Task<byte[]> ComputeHashAsync(string path, CancellationToken ct)
    {
        await using var stream = OpenRead(path);
        return await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
    }

    private static async Task WriteMarkerAsync(
        string markerPath,
        string legacyDataDirectory,
        CancellationToken ct)
    {
        var temporaryMarkerPath = markerPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                             temporaryMarkerPath,
                             new FileStreamOptions
                             {
                                 Mode = FileMode.CreateNew,
                                 Access = FileAccess.Write,
                                 Share = FileShare.None,
                                 Options = FileOptions.Asynchronous | FileOptions.WriteThrough
                             }))
            {
                using var writer = new Utf8JsonWriter(
                    stream,
                    new JsonWriterOptions
                    {
                        Encoder = ConfigJsonContext.Persistence.Options.Encoder,
                        Indented = true
                    });
                writer.WriteStartObject();
                writer.WriteNumber("version", 1);
                writer.WriteString("source", legacyDataDirectory);
                writer.WriteString("completedAtUtc", DateTimeOffset.UtcNow);
                writer.WriteBoolean("legacyDataRetained", true);
                writer.WriteEndObject();
                writer.Flush();

                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryMarkerPath, markerPath);
        }
        finally
        {
            TryDeleteFile(temporaryMarkerPath);
        }
    }

    private static void MoveEntry(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }

    private static void RollBackMovedEntries(
        List<(string Destination, string Staged)> movedEntries)
    {
        for (var index = movedEntries.Count - 1; index >= 0; index--)
        {
            var entry = movedEntries[index];
            try
            {
                MoveEntry(entry.Destination, entry.Staged);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new StandaloneWorkspaceDataMigrationException(
                    $"Migration rollback failed for '{entry.Destination}'. " +
                    "The legacy source remains available; inspect the workspace migration staging directory.",
                    ex);
            }
        }
    }

    private static void ThrowIfReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new StandaloneWorkspaceDataMigrationException(
                $"Migration refuses the symbolic link or reparse point '{path}'.");
        }
    }

    private static void EnsureDirectChild(string parentDirectory, string childDirectory)
    {
        var relative = Path.GetRelativePath(parentDirectory, childDirectory);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.Contains(Path.DirectorySeparatorChar))
        {
            throw new InvalidOperationException(
                $"Migration staging path '{childDirectory}' escaped the workspace root.");
        }
    }

    private static void TryDeleteMigrationStaging(string path, string workspaceRoot)
    {
        try
        {
            EnsureDirectChild(workspaceRoot, path);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A retained staging directory is safer than masking the primary migration result.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale temporary marker is harmless and must not mask the primary result.
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)
                && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leaving an empty directory does not select partially migrated data.
        }
    }
}

/// <summary>Reports a safe, non-destructive failure to prepare workspace-local data.</summary>
public sealed class StandaloneWorkspaceDataMigrationException : IOException
{
    public StandaloneWorkspaceDataMigrationException(string message)
        : base(message)
    {
    }

    public StandaloneWorkspaceDataMigrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
