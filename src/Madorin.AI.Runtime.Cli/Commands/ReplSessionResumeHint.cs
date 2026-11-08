using Madorin.AI.Runtime.Cli.Config;

namespace Madorin.AI.Runtime.Cli.Commands;

/// <summary>离开当前会话时，用一行英文提示会话号和两种恢复方式。</summary>
internal static class ReplSessionResumeHint
{
    public static void Write(ICliTerminal terminal, string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        sessionId = sessionId?.Trim();
        if (string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        terminal.WriteLine(
            $"Session {sessionId} can be resumed. In this program: /resume {sessionId}. After exit: madorin run --session {sessionId}",
            CliTextStyle.Muted);
    }
}
