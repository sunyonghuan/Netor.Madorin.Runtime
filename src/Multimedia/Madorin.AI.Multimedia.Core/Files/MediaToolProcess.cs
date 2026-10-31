using System.ComponentModel;
using System.Diagnostics;
using Madorin.AI.Multimedia.Core.Protocol;

namespace Madorin.AI.Multimedia.Core.Files;

internal static class MediaToolProcess
{
    public static void EnsureAvailable()
    {
        foreach (var tool in new[] { "ffprobe", "ffmpeg" })
        {
            if (!CanStart(tool))
            {
                throw new SkillException("internal_error", "缺少可执行的 ffmpeg/ffprobe；请安装开发工具或使用包含工具的正式技能包。");
            }
        }
    }

    private static bool CanStart(string tool)
    {
        var executable = tool + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, executable)))
        {
            return true;
        }

#if !DEBUG
        return false;
#else

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory, executable)))
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
            }
        }

        return false;
#endif
    }

    public static async Task<string> RunAsync(string tool, string[] args, CancellationToken cancellationToken)
    {
        var executable = tool + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
        var bundled = Path.Combine(AppContext.BaseDirectory, executable);
        var toolPath = File.Exists(bundled) ? bundled : null;
#if DEBUG
        toolPath ??= executable;
#endif
        if (toolPath is null)
        {
            throw new SkillException("internal_error", $"正式技能包缺少已授权打包的 {tool} 工具。");
        }
        var info = new ProcessStartInfo(toolPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in args) { info.ArgumentList.Add(arg); }
        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
            var output = ReadBoundedAsync(process.StandardOutput, cancellationToken);
            var errors = ReadBoundedAsync(process.StandardError, cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
                await errors;
                var text = await output;
                if (process.ExitCode != 0) { throw new SkillException("provider_error", "视频文件无法完整解码或探测。"); }
                return text;
            }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(true); }
                    catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
                }
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { }
                // 子进程终止后有界回收退出状态和管道读取。
                try { await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); }
                catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException or SkillException) { }
            }
        }
        catch (Win32Exception)
        {
            throw new SkillException("internal_error", "缺少可执行的 ffmpeg/ffprobe；请安装开发工具或使用包含工具的正式技能包。");
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var text = new System.Text.StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            if (text.Length + count <= 65536) { text.Append(buffer, 0, count); }
        }
        return text.ToString();
    }
}
