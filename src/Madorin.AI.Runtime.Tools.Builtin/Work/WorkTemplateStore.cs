namespace Madorin.AI.Runtime.Tools.Builtin.Work;

/// <summary>扫描、加载与读取工作计划模板目录（Runtime 内置，无宿主回调）。</summary>
internal sealed class WorkTemplateStore
{
    private readonly string _userRoot;
    private readonly string _systemRoot;

    public WorkTemplateStore(string? userRootOverride = null, string? systemRootOverride = null)
    {
        (_userRoot, _systemRoot) = WorkTemplatePaths.ResolveRoots(userRootOverride, systemRootOverride);
    }

    public string UserRoot => _userRoot;

    public string SystemRoot => _systemRoot;

    /// <summary>列出有效模板摘要；空目录返回空数组，不算失败。</summary>
    public IReadOnlyList<WorkTemplateSummary> List(string? keyword, string? category)
    {
        var results = new List<WorkTemplateSummary>();
        foreach (var root in EnumerateExistingRoots())
        {
            foreach (var directory in SafeEnumerateDirectories(root))
            {
                if (!TryLoadSummary(directory, out var summary) || summary is null)
                {
                    continue;
                }

                if (!MatchesFilter(summary, keyword, category))
                {
                    continue;
                }

                results.Add(summary);
            }
        }

        return results
            .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>加载模板材料。errorCode: path_not_allowed / template_not_found / template_invalid。</summary>
    public bool TryLoad(
        string path,
        out WorkTemplateDocument? document,
        out string absolutePath,
        out string errorCode,
        out string message)
    {
        document = null;
        absolutePath = string.Empty;
        errorCode = string.Empty;
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            errorCode = "path_not_allowed";
            message = "path is required and must be an absolute template directory.";
            return false;
        }

        string full;
        try
        {
            full = WorkTemplatePaths.NormalizeDirectory(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errorCode = "path_not_allowed";
            message = "path could not be normalized.";
            return false;
        }

        absolutePath = full;
        if (!WorkTemplatePaths.IsUnderAllowedRoot(full, _userRoot, _systemRoot))
        {
            errorCode = "path_not_allowed";
            message = "path must be under the user or system work-templates root.";
            return false;
        }

        if (!Directory.Exists(full))
        {
            errorCode = "template_not_found";
            message = "template directory was not found.";
            return false;
        }

        var templateFile = Path.Combine(full, WorkTemplatePaths.TemplateFileName);
        if (!File.Exists(templateFile))
        {
            errorCode = "template_not_found";
            message = "TEMPLATE.md was not found in the template directory.";
            return false;
        }

        if (!TryReadUtf8Limited(templateFile, WorkTemplatePaths.MaxTemplateMarkdownBytes, out var markdown, out var readError))
        {
            errorCode = "template_invalid";
            message = readError;
            return false;
        }

        if (!WorkTemplateDocumentParser.TryParse(markdown, out var parsed, out var parseError) || parsed is null)
        {
            errorCode = "template_invalid";
            message = parseError;
            return false;
        }

        var files = IndexFiles(full);
        if (files.Count > WorkTemplatePaths.MaxIndexedFiles)
        {
            errorCode = "template_invalid";
            message = $"template indexes more than {WorkTemplatePaths.MaxIndexedFiles} files.";
            return false;
        }

        document = parsed with { Files = files };
        return true;
    }

    /// <summary>读取模板内已索引文件全文。</summary>
    public bool TryReadFile(
        string path,
        string relativePath,
        out string content,
        out string absolutePath,
        out string normalizedRelative,
        out string errorCode,
        out string message)
    {
        content = string.Empty;
        normalizedRelative = string.Empty;
        if (!TryLoad(path, out var document, out absolutePath, out errorCode, out message)
            || document is null)
        {
            return false;
        }

        if (!WorkTemplatePaths.TryNormalizeRelativePath(relativePath, out normalizedRelative, out var relError))
        {
            errorCode = "path_not_allowed";
            message = relError;
            return false;
        }

        var expectedRelative = normalizedRelative;
        if (!document.Files.Any(file =>
                string.Equals(file.RelativePath, expectedRelative, StringComparison.Ordinal)))
        {
            errorCode = "template_not_found";
            message = "relativePath is not listed in the template file index.";
            return false;
        }

        var fullFile = Path.GetFullPath(Path.Combine(absolutePath, normalizedRelative.Replace('/', Path.DirectorySeparatorChar)));
        if (!WorkTemplatePaths.IsWithin(absolutePath, fullFile) || !File.Exists(fullFile))
        {
            errorCode = "path_not_allowed";
            message = "resolved file path escapes the template directory or is missing.";
            return false;
        }

        if (!TryReadUtf8Limited(fullFile, WorkTemplatePaths.MaxReadFileBytes, out content, out var readError))
        {
            errorCode = "template_invalid";
            message = readError;
            return false;
        }

        return true;
    }

    private IEnumerable<string> EnumerateExistingRoots()
    {
        if (Directory.Exists(_userRoot))
        {
            yield return _userRoot;
        }

        if (Directory.Exists(_systemRoot)
            && !string.Equals(
                WorkTemplatePaths.NormalizeDirectory(_userRoot),
                WorkTemplatePaths.NormalizeDirectory(_systemRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            yield return _systemRoot;
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            yield return directory;
        }
    }

    private static bool TryLoadSummary(string directory, out WorkTemplateSummary? summary)
    {
        summary = null;
        var templateFile = Path.Combine(directory, WorkTemplatePaths.TemplateFileName);
        if (!File.Exists(templateFile))
        {
            return false;
        }

        if (!TryReadUtf8Limited(
                templateFile,
                WorkTemplatePaths.MaxTemplateMarkdownBytes,
                out var markdown,
                out _))
        {
            return false;
        }

        if (!WorkTemplateDocumentParser.TryParse(markdown, out var document, out _) || document is null)
        {
            return false;
        }

        summary = new WorkTemplateSummary(
            document.Name,
            document.Description,
            WorkTemplatePaths.NormalizeDirectory(directory),
            document.Category,
            document.Version);
        return true;
    }

    private static bool MatchesFilter(WorkTemplateSummary summary, string? keyword, string? category)
    {
        if (!string.IsNullOrWhiteSpace(category)
            && !string.Equals(summary.Category, category.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(keyword))
        {
            return true;
        }

        var needle = keyword.Trim();
        return summary.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || summary.Description.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || summary.Category.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static List<WorkTemplateFileEntry> IndexFiles(string templateDirectory)
    {
        var entries = new List<WorkTemplateFileEntry>
        {
            new(WorkTemplatePaths.TemplateFileName, "template")
        };

        AppendIndexedTree(templateDirectory, "references", "reference", entries);
        AppendIndexedTree(templateDirectory, "scripts", "script", entries);
        return entries;
    }

    private static void AppendIndexedTree(
        string templateDirectory,
        string folderName,
        string kind,
        List<WorkTemplateFileEntry> entries)
    {
        var folder = Path.Combine(templateDirectory, folderName);
        if (!Directory.Exists(folder))
        {
            return;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(templateDirectory, file)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (WorkTemplatePaths.TryNormalizeRelativePath(relative, out var normalized, out _))
            {
                entries.Add(new WorkTemplateFileEntry(normalized, kind));
            }
        }
    }

    private static bool TryReadUtf8Limited(
        string path,
        int maxBytes,
        out string content,
        out string error)
    {
        content = string.Empty;
        error = string.Empty;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > maxBytes)
            {
                error = $"file exceeds the {maxBytes}-byte limit.";
                return false;
            }

            content = File.ReadAllText(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            error = "file could not be read as UTF-8 text.";
            return false;
        }
    }
}
