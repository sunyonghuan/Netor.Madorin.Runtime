using System.Text.Json;

namespace Madorin.AI.Runtime.Tools.Abstractions;

/// <summary>命令工具只按规范化后的初始工作目录授权，不检查脚本内容。</summary>
public static class CommandToolAuthorization
{
    public const string ToolId = "mcli_command";

    public static string ResolveWorkingDirectory(string workspaceRoot, JsonElement arguments)
    {
        var path = workspaceRoot;
        if (arguments.TryGetProperty("workingDirectory", out var property))
        {
            if (property.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException("Tool argument 'workingDirectory' must be a string.");
            }

            path = property.GetString()!;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return NormalizeDirectory(Path.GetFullPath(path, Path.GetFullPath(workspaceRoot)));
    }

    public static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool MatchesDirectory(string? allowedDirectory, string requestedDirectory) =>
        allowedDirectory is not null && string.Equals(
            NormalizeDirectory(allowedDirectory),
            NormalizeDirectory(requestedDirectory),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
