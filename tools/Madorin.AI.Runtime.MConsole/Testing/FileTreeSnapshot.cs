namespace Madorin.AI.Runtime.MConsole.Testing;

public sealed record FileSnapshotEntry(
    string RelativePath,
    long Length,
    DateTimeOffset LastWriteTimeUtc,
    string Sha256);

public sealed record FileTreeSnapshot(
    string RootPath,
    IReadOnlyList<FileSnapshotEntry> Files,
    DateTimeOffset CapturedAt);

public sealed record FileTreeDifference(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Modified)
{
    public bool HasChanges => Added.Count > 0 || Removed.Count > 0 || Modified.Count > 0;
}
