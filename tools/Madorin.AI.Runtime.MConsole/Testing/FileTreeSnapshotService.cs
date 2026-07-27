using System.Security.Cryptography;

namespace Madorin.AI.Runtime.MConsole.Testing;

public static class FileTreeSnapshotService
{
    public static async Task<FileTreeSnapshot> CaptureAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
        {
            return new FileTreeSnapshot(root, [], DateTimeOffset.UtcNow);
        }

        var entries = new List<FileSnapshotEntry>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            await using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
            var hash = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken))
                .ToLowerInvariant();
            entries.Add(new FileSnapshotEntry(
                Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                info.Length,
                info.LastWriteTimeUtc,
                hash));
        }

        return new FileTreeSnapshot(root, entries, DateTimeOffset.UtcNow);
    }

    public static FileTreeDifference Compare(FileTreeSnapshot before, FileTreeSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (!string.Equals(before.RootPath, after.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Snapshots must have the same root path.", nameof(after));
        }

        var first = before.Files.ToDictionary(static item => item.RelativePath, StringComparer.Ordinal);
        var second = after.Files.ToDictionary(static item => item.RelativePath, StringComparer.Ordinal);
        return new FileTreeDifference(
            second.Keys.Except(first.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            first.Keys.Except(second.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            first.Keys.Intersect(second.Keys, StringComparer.Ordinal)
                .Where(path => !Equivalent(first[path], second[path]))
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    private static bool Equivalent(FileSnapshotEntry first, FileSnapshotEntry second) =>
        first.Length == second.Length
        && first.LastWriteTimeUtc == second.LastWriteTimeUtc
        && string.Equals(first.Sha256, second.Sha256, StringComparison.Ordinal);
}
