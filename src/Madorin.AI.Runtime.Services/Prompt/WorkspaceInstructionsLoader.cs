using System.Text;

namespace Madorin.AI.Runtime.Services.Prompt;

/// <summary>
/// Loads the optional workspace-level AGENTS.md file. File names are matched
/// case-insensitively so the feature behaves the same on case-sensitive systems.
/// </summary>
public static class WorkspaceInstructionsLoader
{
    public const string FileName = "AGENTS.md";
    public const long MaximumFileBytes = 64 * 1024;

    public static async Task<WorkspaceInstructions?> LoadAsync(
        string workspaceRoot,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var path = ResolvePath(workspaceRoot);
        if (path is null)
        {
            return null;
        }

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length > MaximumFileBytes)
        {
            throw new InvalidDataException(
                $"Workspace instructions '{path}' exceed the {MaximumFileBytes}-byte limit.");
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            if (bytes.LongLength > MaximumFileBytes)
            {
                throw new InvalidDataException(
                    $"Workspace instructions '{path}' exceed the {MaximumFileBytes}-byte limit.");
            }

            var content = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(bytes);
            if (HasUtf8Bom(bytes))
            {
                content = content.TrimStart('\uFEFF');
            }

            return string.IsNullOrWhiteSpace(content)
                ? null
                : new WorkspaceInstructions(path, content);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(
                $"Workspace instructions '{path}' must be valid UTF-8.",
                ex);
        }
    }

    public static string GetExpectedPath(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return Path.Combine(Path.GetFullPath(workspaceRoot), FileName);
    }

    private static string? ResolvePath(string workspaceRoot)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var expectedPath = Path.Combine(root, FileName);
        if (File.Exists(expectedPath))
        {
            return expectedPath;
        }

        // On case-insensitive file systems the exact name is normally found above.
        // Enumerating the root also makes AGENTS.MD and AgEnTs.Md work on Linux,
        // where File.Exists is case-sensitive.
        if (!Directory.Exists(root))
        {
            return null;
        }

        return Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Where(static path => string.Equals(
                Path.GetFileName(path),
                FileName,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3
        && bytes[0] == 0xEF
        && bytes[1] == 0xBB
        && bytes[2] == 0xBF;
}

public sealed record WorkspaceInstructions(string Path, string Content);
