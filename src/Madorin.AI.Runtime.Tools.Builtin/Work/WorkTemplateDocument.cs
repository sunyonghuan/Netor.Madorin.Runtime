namespace Madorin.AI.Runtime.Tools.Builtin.Work;

/// <summary>已解析的工作计划模板摘要（list 用）。</summary>
internal sealed record WorkTemplateSummary(
    string Name,
    string Description,
    string Path,
    string Category,
    string Version);

/// <summary>已解析的工作计划模板正文材料（load 用）。</summary>
internal sealed record WorkTemplateDocument(
    string Name,
    string Description,
    string Category,
    string Version,
    string Instructions,
    string PlanSkeleton,
    IReadOnlyList<WorkTemplateFileEntry> Files);

/// <summary>模板目录内可索引文件。</summary>
internal sealed record WorkTemplateFileEntry(string RelativePath, string Kind);

/// <summary>
/// TEMPLATE.md 解析。
/// 假设：YAML frontmatter 必含 name/description；category/version 可选；
/// 正文按「## 计划骨架」拆出 planSkeleton，其余为 instructions。
/// </summary>
internal static class WorkTemplateDocumentParser
{
    public static bool TryParse(
        string markdown,
        out WorkTemplateDocument? document,
        out string error)
    {
        document = null;
        error = string.Empty;
        if (!TrySplitFrontmatter(markdown, out var frontmatter, out var body))
        {
            error = "TEMPLATE.md must start with YAML frontmatter.";
            return false;
        }

        var fields = ParseFrontmatterFields(frontmatter);
        if (!fields.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
        {
            error = "TEMPLATE.md frontmatter requires name.";
            return false;
        }

        if (!fields.TryGetValue("description", out var description)
            || string.IsNullOrWhiteSpace(description))
        {
            error = "TEMPLATE.md frontmatter requires description.";
            return false;
        }

        fields.TryGetValue("category", out var category);
        fields.TryGetValue("version", out var version);
        SplitBody(body, out var instructions, out var planSkeleton);
        document = new WorkTemplateDocument(
            name.Trim(),
            description.Trim(),
            string.IsNullOrWhiteSpace(category) ? string.Empty : category.Trim(),
            string.IsNullOrWhiteSpace(version) ? "1.0" : version.Trim(),
            instructions,
            planSkeleton,
            Array.Empty<WorkTemplateFileEntry>());
        return true;
    }

    private static bool TrySplitFrontmatter(string text, out string frontmatter, out string body)
    {
        frontmatter = string.Empty;
        body = string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return false;
        }

        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            if (normalized.EndsWith("\n---", StringComparison.Ordinal))
            {
                frontmatter = normalized[4..^4];
                body = string.Empty;
                return true;
            }

            return false;
        }

        frontmatter = normalized[4..end];
        body = normalized[(end + 5)..];
        return true;
    }

    private static Dictionary<string, string> ParseFrontmatterFields(string frontmatter)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in frontmatter.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"', '\'');
            if (key.Length > 0)
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static void SplitBody(string body, out string instructions, out string planSkeleton)
    {
        var normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        const string marker = "\n## 计划骨架";
        var index = normalized.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0 && normalized.StartsWith("## 计划骨架", StringComparison.Ordinal))
        {
            instructions = string.Empty;
            planSkeleton = normalized.Trim();
            return;
        }

        if (index < 0)
        {
            instructions = normalized.Trim();
            planSkeleton = string.Empty;
            return;
        }

        instructions = normalized[..index].Trim();
        planSkeleton = normalized[(index + 1)..].Trim();
    }
}
