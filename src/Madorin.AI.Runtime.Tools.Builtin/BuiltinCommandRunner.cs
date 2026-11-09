using System.Globalization;
using System.Text;

namespace Madorin.AI.Runtime.Tools.Builtin;

/// <summary>Resolves the host shell and prepares a bounded command invocation.</summary>
internal static class BuiltinCommandRunner
{
    private const int MaxScriptBytes = 8 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, false);

    public static Encoding ResolveEncoding(string? requested) => requested?.Trim().ToLowerInvariant() switch
    {
        null or "utf-8" => Utf8,
        "gbk" => CodePagesEncodingProvider.Instance.GetEncoding(936)!,
        "gb18030" => CodePagesEncodingProvider.Instance.GetEncoding(54936)!,
        _ => throw new ArgumentException("Command encoding must be utf-8, gbk, or gb18030.", nameof(requested))
    };

    public static string NormalizeShell(string? requested)
    {
        var shell = string.IsNullOrWhiteSpace(requested) ? "auto" : requested.Trim();
        return shell.ToLowerInvariant() switch
        {
            "auto" => "auto",
            "pwsh" or "powershell" or "cmd" or "bash" or "zsh" or "sh" => shell.ToLowerInvariant(),
            _ => throw new ArgumentException("The command shell is not supported.", nameof(requested))
        };
    }

    public static (string Name, string Path) ResolveShell(string requested)
    {
        var candidates = GetCandidates(requested);
        foreach (var candidate in candidates)
        {
            try
            {
                // 保留启动路径，让操作系统解析应用别名、符号链接及其目标。
                return (candidate, BuiltinProcessRunner.FindOnRuntimePath(candidate));
            }
            catch (FileNotFoundException)
            {
            }
        }

        throw new FileNotFoundException(
            $"Shell executable not found on the Runtime PATH: {string.Join(", ", candidates)}.");
    }

    public static IReadOnlyList<string> BuildArguments(string shell, string script, Encoding encoding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        if (Utf8.GetByteCount(script) > MaxScriptBytes)
        {
            throw new InvalidDataException(
                "The command script exceeds the 8 KB limit. No command was executed. " +
                "Use mcli_fs_write to save long text or scripts to files, then pass file paths to the program " +
                "or invoke the script file with its interpreter. If the program supports standard input, " +
                "put long text in stdin instead of script. Retry with a shorter script and a new tool call id.");
        }

        if (OperatingSystem.IsWindows())
        {
            var codePage = encoding.CodePage.ToString(CultureInfo.InvariantCulture);
            return shell switch
            {
                "cmd" => ["/d", "/s", "/c", $"chcp {codePage}>nul & " + script],
                "pwsh" or "powershell" => ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                    $"[Console]::InputEncoding=[Text.Encoding]::GetEncoding({codePage});" +
                    $"[Console]::OutputEncoding=[Text.Encoding]::GetEncoding({codePage});" +
                    $"$OutputEncoding=[Text.Encoding]::GetEncoding({codePage});" + script],
                _ => throw new ArgumentException("The selected shell is not available on Windows.")
            };
        }

        var pythonEncoding = encoding.CodePage == 936 ? "gbk" : encoding.WebName;
        var prefix = $"export PYTHONIOENCODING={pythonEncoding} PYTHONUTF8=1; ";
        return shell switch
        {
            "bash" or "zsh" => ["-lc", prefix + script],
            "sh" => ["-c", prefix + script],
            _ => throw new ArgumentException("The selected shell is not available on this platform.")
        };
    }

    private static IReadOnlyList<string> GetCandidates(string requested)
    {
        if (requested != "auto")
        {
            return [requested];
        }

        return OperatingSystem.IsWindows()
            ? ["pwsh", "powershell", "cmd"]
            : ["bash", "zsh", "sh"];
    }
}
