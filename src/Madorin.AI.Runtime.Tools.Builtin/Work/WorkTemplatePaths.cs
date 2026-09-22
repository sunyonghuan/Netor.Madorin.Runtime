namespace Madorin.AI.Runtime.Tools.Builtin.Work;

/// <summary>
/// 工作计划模板扫描根与路径约束。
/// 假设（规格文档缺失时的默认）：仅允许落在用户根或系统根之下的绝对路径。
/// </summary>
internal static class WorkTemplatePaths
{
    public const string TemplateFileName = "TEMPLATE.md";
    public const int MaxTemplateMarkdownBytes = 256 * 1024;
    public const int MaxIndexedFiles = 64;
    public const int MaxReadFileBytes = 256 * 1024;

    /// <summary>解析用户根与系统根；测试可注入覆盖。</summary>
    public static (string UserRoot, string SystemRoot) ResolveRoots(
        string? userRootOverride = null,
        string? systemRootOverride = null)
    {
        var userRoot = string.IsNullOrWhiteSpace(userRootOverride)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "madorin",
                "work-templates")
            : Path.GetFullPath(userRootOverride);
        var systemRoot = string.IsNullOrWhiteSpace(systemRootOverride)
            ? Path.Combine(AppContext.BaseDirectory, "work-templates")
            : Path.GetFullPath(systemRootOverride);
        return (userRoot, systemRoot);
    }

    /// <summary>判断绝对路径是否落在任一允许根之下（含根本身）。</summary>
    public static bool IsUnderAllowedRoot(string absolutePath, string userRoot, string systemRoot)
    {
        var full = NormalizeDirectory(absolutePath);
        return IsWithin(NormalizeDirectory(userRoot), full)
            || IsWithin(NormalizeDirectory(systemRoot), full);
    }

    public static string NormalizeDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static bool IsWithin(string root, string candidate)
    {
        if (string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>规范化相对路径：必须用 /，禁止 .. 与盘符。</summary>
    public static bool TryNormalizeRelativePath(string relativePath, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = "relativePath is required.";
            return false;
        }

        if (relativePath.Contains('\\', StringComparison.Ordinal)
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains(':', StringComparison.Ordinal))
        {
            error = "relativePath must be a forward-slash relative path.";
            return false;
        }

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0
            || segments.Any(static s =>
                s is "." or ".."
                || s.Contains('\0')))
        {
            error = "relativePath contains illegal segments.";
            return false;
        }

        normalized = string.Join('/', segments);
        return true;
    }
}
