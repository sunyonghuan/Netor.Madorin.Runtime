using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Cli.Tools;

namespace Madorin.AI.Runtime.Cli.Commands;

/// <summary>消息发出后先占一行显示等待；模型思考时改成 thinking，工具调用时改成 tool:工具名，正式输出前换行。</summary>
internal sealed class ReplThinkingStatus(ICliTerminal terminal, Func<long>? clock = null) : IConsoleActivity, IDisposable
{
    private readonly object _gate = new();
    private Timer? _timer;
    private long _startedAt;
    private int _renderedSeconds = -1;
    private int _width;
    private bool _active;
    private bool _thinking;
    private string? _toolName;
    private bool _restartOnNewLine;

    public void Start()
    {
        lock (_gate)
        {
            if (_active)
            {
                return;
            }

            EnsureActive();
            _thinking = false;
            _toolName = null;
            Render();
        }
    }

    public void BeginThinking()
    {
        lock (_gate)
        {
            EnsureActive();
            if (_toolName is not null || !_thinking)
            {
                _thinking = true;
                _toolName = null;
                _renderedSeconds = -1;
            }

            Render();
        }
    }

    public void BeginTool(string toolName)
    {
        var normalized = NormalizeToolName(toolName);
        if (normalized is null)
        {
            return;
        }

        lock (_gate)
        {
            EnsureActive();
            if (!string.Equals(_toolName, normalized, StringComparison.Ordinal))
            {
                _toolName = normalized;
                _thinking = false;
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
            _restartOnNewLine = true;
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

        var label = _toolName is not null
            ? $"tool:{_toolName}"
            : _thinking
                ? "thinking"
                : "working";
        var text = $"{label} {seconds}s";
        terminal.Write("\r" + text.PadRight(_width), CliTextStyle.Muted);
        _width = Math.Max(_width, text.Length);
        _renderedSeconds = seconds;
    }

    private void EnsureActive()
    {
        if (_active)
        {
            return;
        }

        OpenNewLineIfNeeded();
        _active = true;
        _startedAt = Now();
        _renderedSeconds = -1;
        _width = 0;
        if (clock is null && _timer is null)
        {
            _timer = new Timer(_ => Pulse(), null, 200, 200);
        }
    }

    private static string? NormalizeToolName(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return null;
        }

        var trimmed = toolName.Trim();
        foreach (var character in trimmed)
        {
            if (char.IsControl(character))
            {
                return null;
            }
        }

        return trimmed;
    }

    private void OpenNewLineIfNeeded()
    {
        if (!_restartOnNewLine)
        {
            return;
        }

        terminal.WriteLine();
        _restartOnNewLine = false;
    }

    private long Now() => clock?.Invoke() ?? Environment.TickCount64;
}
