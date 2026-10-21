using Madorin.AI.Multimedia.Core.Protocol;

namespace Madorin.AI.Multimedia.Core.Files;

public sealed class WorkspacePaths(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public string Resolve(string path, string? baseDirectory = null)
    {
        string resolved;
        try
        {
            resolved = Path.GetFullPath(path, baseDirectory ?? Root);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new SkillException("invalid_parameter", "本地路径无效。");
        }

        EnsureAllowed(resolved);
        return resolved;
    }

    public void EnsureAllowed(string path)
    {
        var relative = Path.GetRelativePath(Root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new SkillException("invalid_parameter", "本地路径超出工作目录授权范围。");
        }

        // 保留现有拒绝链接路径的边界，读写前再次检查以避免绕过授权根。
        var current = Root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null || info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new SkillException("invalid_parameter", "本地路径包含链接或重解析点。");
            }
        }
    }
}
