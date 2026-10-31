using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Madorin.AI.Runtime.Tools.Builtin;

/// <summary>Resolves the host shell and prepares a bounded, UTF-8 command invocation.</summary>
internal static class BuiltinCommandRunner
{
    private const int MaxScriptBytes = 8 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, false);

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

    public static string[] GetAllowedShellNames()
    {
        return OperatingSystem.IsWindows()
            ? ["pwsh", "powershell", "cmd"]
            : ["bash", "zsh", "sh"];
    }

    public static BuiltinProcessRunner.PinnedExecutable ResolveShell(
        string requested,
        IReadOnlyList<string> allowedExecutables)
    {
        var candidates = GetCandidates(requested);
        foreach (var candidate in candidates)
        {
            try
            {
                return BuiltinProcessRunner.ResolveAndPinExecutable(candidate, allowedExecutables);
            }
            catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException)
            {
            }
        }

        throw new FileNotFoundException(
            $"None of the requested shells were available: {string.Join(", ", candidates)}.");
    }

    public static IReadOnlyList<string> BuildArguments(string shell, string script)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        if (Utf8.GetByteCount(script) > MaxScriptBytes)
        {
            throw new InvalidDataException("The command script exceeds the 8 KB limit.");
        }

        if (OperatingSystem.IsWindows())
        {
            return shell switch
            {
                "cmd" => ["/d", "/s", "/c", "chcp 65001>nul & " + script],
                "pwsh" or "powershell" => ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                    "[Console]::InputEncoding=[Text.UTF8Encoding]::new($false);" +
                    "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);" +
                    "$OutputEncoding=[Text.UTF8Encoding]::new($false);" + script],
                _ => throw new ArgumentException("The selected shell is not available on Windows.")
            };
        }

        return shell switch
        {
            "bash" or "zsh" => ["-lc", "export PYTHONIOENCODING=utf-8 PYTHONUTF8=1; " + script],
            "sh" => ["-c", "export PYTHONIOENCODING=utf-8 PYTHONUTF8=1; " + script],
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
