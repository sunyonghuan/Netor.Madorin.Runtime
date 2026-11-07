using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Cli.Tools;

namespace Madorin.AI.Runtime.Cli.Commands;

/// <summary>消息发出后先占一行显示等待；模型思考时改成 thinking，正式输出前换行。</summary>
internal sealed class ReplThinkingStatus(ICliTerminal terminal, Func<long>? clock = null) : IConsoleActivity, IDisposable
{
    private readonly object _gate = new();
    private Timer? _timer;
    private long _startedAt;
    private int _renderedSeconds = -1;
    private int _width;
    private bool _active;
    private bool _thinking;

    public void Start()
    {
        lock (_gate)
        {
            if (_active)
            {
                return;
            }

            _active = true;
            _thinking = false;
            _startedAt = Now();
            _renderedSeconds = -1;
            _width = 0;
            Render();
            if (clock is null)
            {
                _timer = new Timer(_ => Pulse(), null, 200, 200);
            }
        }
    }

    public void BeginThinking()
    {
        lock (_gate)
        {
            if (!_active)
            {
                _active = true;
                _startedAt = Now();
                _renderedSeconds = -1;
                _width = 0;
            }

            if (!_thinking)
            {
                _thinking = true;
                _renderedSeconds = -1;
            }

            Render();
        }
    }

    public void Finish()
    {
        lock (_gate)
        {
            if (!_active)
            {
                return;
            }

            _active = false;
            _timer?.Dispose();
            _timer = null;
            terminal.WriteLine();
        }
    }

    public void Dispose() => Finish();

    internal void Pulse()
    {
        lock (_gate)
        {
            if (!_active)
            {
                return;
            }

            Render();
        }
    }

    private void Render()
    {
        var seconds = (int)Math.Min(int.MaxValue, Math.Max(0, Now() - _startedAt) / 1000);
        if (seconds == _renderedSeconds)
        {
            return;
        }

        var text = $"{(_thinking ? "thinking" : "working")} {seconds}s";
        terminal.Write("\r" + text.PadRight(_width), CliTextStyle.Muted);
        _width = Math.Max(_width, text.Length);
        _renderedSeconds = seconds;
    }

    private long Now() => clock?.Invoke() ?? Environment.TickCount64;
}
