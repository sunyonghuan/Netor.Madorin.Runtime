using System.Text;

namespace Madorin.AI.Runtime.Cli.Config;

/// <summary>Provides injectable terminal input and secure secret entry for CLI workflows.</summary>
public interface ICliTerminal
{
    /// <summary>Indicates whether input and output are attached to a real interactive console.</summary>
    public bool IsInteractive => false;

    /// <summary>Writes text without a trailing line terminator.</summary>
    public void Write(string value);

    /// <summary>Writes styled text without a trailing line terminator.</summary>
    public void Write(string value, CliTextStyle style);

    /// <summary>Writes text with a trailing line terminator.</summary>
    public void WriteLine(string value = "");

    /// <summary>Writes styled text with a trailing line terminator.</summary>
    public void WriteLine(string value, CliTextStyle style);

    /// <summary>Clears an attached interactive console without emitting terminal control sequences.</summary>
    public void Clear();

    /// <summary>Reads one input line.</summary>
    public ValueTask<string?> ReadLineAsync(CancellationToken ct = default);

    /// <summary>
    /// 读取一段对话。交互终端里 Shift+Enter 换行，Enter 发送；非交互输入仍按一行读取。
    /// </summary>
    public ValueTask<string?> ReadBlockAsync(CancellationToken ct = default) => ReadLineAsync(ct);

    /// <summary>Reads a secret without echoing it when attached to an interactive console.</summary>
    public ValueTask<string> ReadSecretAsync(string prompt, CancellationToken ct = default);

    /// <summary>当前终端能显示图片时画出 logo，否则返回 false。</summary>
    public bool TryWriteLogo(int leftMargin) => false;
}

/// <summary>Default CLI terminal adapter.</summary>
public sealed class CliTerminal : ICliTerminal
{
    private static readonly object ColorSync = new();
    private const string InputMarker = "> ";

    private readonly TextReader _input;
    private readonly bool _isInteractive;
    private readonly TextWriter _output;
    private readonly bool _useSecureConsoleInput;
    private readonly bool _useColor;
    private readonly List<string> _interactiveHistory = [];
    private int _inputCursorRowOffset;
    private int _inputBlockRows;

    /// <summary>Creates an injectable reader/writer terminal.</summary>
    public CliTerminal(TextReader input, TextWriter output, bool colorsEnabled = true)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _isInteractive = ReferenceEquals(input, Console.In)
            && ReferenceEquals(output, Console.Out)
            && !Console.IsInputRedirected
            && !Console.IsOutputRedirected;
        _useSecureConsoleInput = ReferenceEquals(input, Console.In)
            && ReferenceEquals(output, Console.Out)
            && !Console.IsInputRedirected;
        _useColor = colorsEnabled
            && ReferenceEquals(output, Console.Out)
            && !Console.IsOutputRedirected
            && Environment.GetEnvironmentVariable("NO_COLOR") is null;
    }

    /// <summary>Creates the production console terminal.</summary>
    public static CliTerminal ConsoleTerminal { get; } = new(Console.In, Console.Out);

    /// <inheritdoc />
    public bool IsInteractive => _isInteractive;

    /// <inheritdoc />
    public bool TryWriteLogo(int leftMargin) => ConsoleLogo.TryWrite(_output, _isInteractive, leftMargin);

    /// <inheritdoc />
    public void Write(string value) => _output.Write(value);

    /// <inheritdoc />
    public void Write(string value, CliTextStyle style) => WriteStyled(value, style, appendLine: false);

    /// <inheritdoc />
    public void WriteLine(string value = "") => _output.WriteLine(value);

    /// <inheritdoc />
    public void WriteLine(string value, CliTextStyle style) => WriteStyled(value, style, appendLine: true);

    /// <inheritdoc />
    public void Clear()
    {
        if (ReferenceEquals(_output, Console.Out) && !Console.IsOutputRedirected)
        {
            Console.Clear();
        }
    }

    /// <inheritdoc />
    public async ValueTask<string?> ReadLineAsync(CancellationToken ct = default) =>
        await _input.ReadLineAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public ValueTask<string?> ReadBlockAsync(CancellationToken ct = default)
    {
        if (!_isInteractive)
        {
            return ReadLineAsync(ct);
        }

        return ReadInteractiveBlockAsync(ct);
    }

    /// <inheritdoc />
    public async ValueTask<string> ReadSecretAsync(
        string prompt,
        CancellationToken ct = default)
    {
        Write(prompt);
        if (!_useSecureConsoleInput)
        {
            return await _input.ReadLineAsync(ct).ConfigureAwait(false) ?? string.Empty;
        }

        var value = new StringBuilder();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                WriteLine();
                return value.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                {
                    value.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
            }
        }
    }

    private void WriteStyled(string value, CliTextStyle style, bool appendLine)
    {
        if (!_useColor || style == CliTextStyle.Default)
        {
            WriteCore(value, appendLine);
            return;
        }

        lock (ColorSync)
        {
            var previousColor = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = ResolveColor(style);
                WriteCore(value, appendLine);
                _output.Flush();
            }
            finally
            {
                Console.ForegroundColor = previousColor;
            }
        }
    }

    private void WriteCore(string value, bool appendLine)
    {
        if (appendLine)
        {
            _output.WriteLine(value);
            return;
        }

        _output.Write(value);
    }

    internal static ConsoleColor ResolveColor(CliTextStyle style) => style switch
    {
        CliTextStyle.Title or CliTextStyle.Command or CliTextStyle.Prompt => ConsoleColor.DarkCyan,
        CliTextStyle.Parameter => ConsoleColor.Gray,
        CliTextStyle.Selected => ConsoleColor.White,
        CliTextStyle.Success => ConsoleColor.DarkGreen,
        CliTextStyle.Warning => ConsoleColor.DarkYellow,
        CliTextStyle.Error => ConsoleColor.DarkRed,
        CliTextStyle.Muted => ConsoleColor.DarkGray,
        CliTextStyle.Horse => ConsoleColor.White,
        CliTextStyle.Accent => ConsoleColor.Yellow,
        CliTextStyle.User => ConsoleColor.Magenta,
        CliTextStyle.Assistant => ConsoleColor.Green,
        _ => Console.ForegroundColor
    };

    private async ValueTask<string?> ReadInteractiveBlockAsync(CancellationToken ct)
    {
        _inputCursorRowOffset = 0;
        _inputBlockRows = 0;
        if (Console.CursorLeft != 0)
        {
            WriteLine();
        }

        var editor = new InteractiveLineEditor(_interactiveHistory);
        var lastKeyAt = Environment.TickCount64 - InteractiveConsoleInput.PasteBurstMilliseconds - 1;
        RenderInteractiveInput(editor, placeAtEnd: false);

        while (true)
        {
            var key = await ReadKeyAsync(ct).ConfigureAwait(false);
            var now = Environment.TickCount64;
            var elapsed = now - lastKeyAt;
            lastKeyAt = now;
            var status = editor.Apply(key, Console.KeyAvailable, elapsed);
            if (status == InteractiveInputStatus.Submitted)
            {
                RenderInteractiveInput(editor, placeAtEnd: true);
                WriteLine();
                _inputCursorRowOffset = 0;
                _inputBlockRows = 0;
                return editor.Text;
            }

            if (status == InteractiveInputStatus.Changed)
            {
                RenderInteractiveInput(editor, placeAtEnd: false);
            }
        }
    }

    private static async ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken ct)
    {
        while (!Console.KeyAvailable)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(15, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        return Console.ReadKey(intercept: true);
    }

    /// <summary>按编辑器状态整块重画输入区，再把光标放到字符位置。</summary>
    private void RenderInteractiveInput(InteractiveLineEditor editor, bool placeAtEnd)
    {
        try
        {
            RenderInteractiveInputCore(editor, placeAtEnd);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or InvalidOperationException or IOException)
        {
            // 窗口滚动或终端断开后，放弃旧锚点，下一轮从当前光标重画。
            _inputCursorRowOffset = 0;
            _inputBlockRows = 0;
        }
    }

    private void RenderInteractiveInputCore(InteractiveLineEditor editor, bool placeAtEnd)
    {
        var bufferWidth = Math.Max(1, Console.BufferWidth);
        var row = placeAtEnd ? editor.Lines.Count - 1 : editor.Row;
        var column = placeAtEnd ? editor.Lines[row].Length : editor.Column;
        var layout = InteractiveInputLayout.Build(
            editor.Lines,
            row,
            column,
            InputMarker,
            bufferWidth);
        _output.Flush();
        if (Console.CursorTop < _inputCursorRowOffset)
        {
            _inputCursorRowOffset = 0;
            _inputBlockRows = 0;
        }

        var startTop = Console.CursorTop - _inputCursorRowOffset;
        Console.SetCursorPosition(0, startTop);
        var totalRows = Math.Max(layout.Rows.Count, _inputBlockRows);
        for (var index = 0; index < totalRows; index++)
        {
            if (index > 0)
            {
                MoveToNextVisualRow();
            }

            if (index < layout.Rows.Count)
            {
                var visual = layout.Rows[index];
                if (visual.PromptText.Length > 0)
                {
                    Write(visual.PromptText, CliTextStyle.Prompt);
                }

                if (visual.UserText.Length > 0)
                {
                    Write(visual.UserText, CliTextStyle.User);
                }
            }

            ClearVisualTail(bufferWidth);
        }

        _output.Flush();
        var delta = totalRows - 1 - layout.CursorRow;
        var top = Math.Max(0, Console.CursorTop - delta);
        var cursorColumn = Math.Clamp(layout.CursorColumn, 0, bufferWidth - 1);
        Console.SetCursorPosition(cursorColumn, top);
        _inputCursorRowOffset = layout.CursorRow;
        _inputBlockRows = layout.Rows.Count;
    }

    private void MoveToNextVisualRow()
    {
        _output.Flush();
        var next = Console.CursorTop + 1;
        if (next < Console.BufferHeight)
        {
            Console.SetCursorPosition(0, next);
            return;
        }

        _output.WriteLine();
    }

    private void ClearVisualTail(int bufferWidth)
    {
        _output.Flush();
        var usable = Math.Max(1, bufferWidth - 1);
        var column = Console.CursorLeft;
        if (column < 0 || column >= usable)
        {
            return;
        }

        _output.Write(new string(' ', usable - column));
    }
}
