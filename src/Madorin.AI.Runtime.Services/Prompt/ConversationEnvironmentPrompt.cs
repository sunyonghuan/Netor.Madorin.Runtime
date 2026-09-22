using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Madorin.AI.Runtime.Services.Prompt;

/// <summary>
/// 把当前运行环境追加到系统提示词末尾。语言由宿主传入；未传或空白时使用操作系统界面语言。
/// </summary>
public static class ConversationEnvironmentPrompt
{
    private const string CliName = "madorin";

    /// <summary>
    /// 在系统提示词后追加环境块。同一天内日期不变；能确定 shell 时才写 shell 行。
    /// </summary>
    public static string Append(
        string instructions,
        string workspaceRoot,
        string? language,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        var block = Build(workspaceRoot, language, now ?? DateTimeOffset.Now);
        if (instructions.Length == 0)
        {
            return block;
        }

        return instructions + "\n\n" + block;
    }

    private static string Build(string workspaceRoot, string? language, DateTimeOffset now)
    {
        var builder = new StringBuilder();
        builder.Append("## Environment\n");
        builder.Append("os: ").Append(FormatOperatingSystem()).Append('\n');
        builder.Append("locale: ").Append(ResolveLanguage(language)).Append('\n');
        builder.Append("date: ").Append(FormatDate(now)).Append('\n');
        builder.Append("cli: ").Append(CliName).Append(' ').Append(ResolveCliVersion()).Append('\n');
        builder.Append("workspace: ").Append(workspaceRoot);
        var shell = TryDetectShell();
        if (shell is not null)
        {
            builder.Append('\n');
            builder.Append("shell: ").Append(shell);
        }

        return builder.ToString();
    }

    /// <summary>宿主传入非空白语言时原样使用，否则取当前操作系统界面语言。</summary>
    private static string ResolveLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return CultureInfo.CurrentUICulture.Name;
        }

        return language.Trim();
    }

    private static string FormatOperatingSystem()
    {
        var family = OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsMacOS()
                ? "macos"
                : OperatingSystem.IsLinux()
                    ? "linux"
                    : "unknown";
        var version = Environment.OSVersion.Version;
        var versionText = version.Build > 0
            ? version.ToString(3)
            : version.ToString(2);
        var architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        return family + " " + versionText + " " + architecture;
    }

    private static string FormatDate(DateTimeOffset now)
    {
        var offset = now.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var absolute = offset.Duration();
        return now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            + " "
            + sign
            + absolute.Hours.ToString("00", CultureInfo.InvariantCulture)
            + ":"
            + absolute.Minutes.ToString("00", CultureInfo.InvariantCulture);
    }

    private static string ResolveCliVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        if (version is null)
        {
            return "0.0.0";
        }

        return version.Build < 0
            ? version.ToString(2)
            : version.ToString(3);
    }

    /// <summary>Unix 读取 SHELL。Windows 进程无法确认交互式 shell，省略该行。</summary>
    private static string? TryDetectShell()
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (string.IsNullOrWhiteSpace(shell))
        {
            return null;
        }

        var name = Path.GetFileName(shell.Trim());
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
