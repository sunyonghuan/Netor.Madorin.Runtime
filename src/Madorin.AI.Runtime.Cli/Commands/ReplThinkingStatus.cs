using Madorin.AI.Runtime.Cli.Config;

namespace Madorin.AI.Runtime.Cli.Commands;

/// <summary>在交互窗口用同一行显示正在思考，不输出思维内容。</summary>
internal sealed class ReplThinkingStatus(ICliTerminal terminal, Func<long>? clock = null)
{
    private long _startedAt;
    private int _renderedSeconds = -1;
    private int _width;
    private bool _active;

    public void Show()
    {
        var now = clock?.Invoke() ?? Environment.TickCount64;
        if (!_active)
        {
            _active = true;
            _startedAt = now;
        }

        var seconds = (int)Math.Min(int.MaxValue, Math.Max(0, now - _startedAt) / 1000);
        if (seconds == _renderedSeconds)
        {
            return;
        }

        var text = $"thinking {seconds}s";
        terminal.Write("\r" + text.PadRight(_width), CliTextStyle.Muted);
        _width = Math.Max(_width, text.Length);
        _renderedSeconds = seconds;
    }

    public void Clear()
    {
        if (!_active)
        {
            return;
        }

        terminal.Write("\r" + new string(' ', _width) + "\r");
        _active = false;
        _width = 0;
        _renderedSeconds = -1;
    }
}
