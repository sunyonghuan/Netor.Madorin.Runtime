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
        var lines = new List<string> { string.Empty };
        Write(InputMarker, CliTextStyle.Prompt);
        var lastKeyAt = Environment.TickCount64 - InteractiveConsoleInput.PasteBurstMilliseconds - 1;

        while (true)
        {
            var key = await ReadKeyAsync(ct).ConfigureAwait(false);
            var now = Environment.TickCount64;
            var elapsed = now - lastKeyAt;
            lastKeyAt = now;
            var moreKeysQueued = Console.KeyAvailable;
            if (key.Key == ConsoleKey.Enter || key.KeyChar is '\r' or '\n')
            {
                var shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;
                if (InteractiveConsoleInput.IsSubmitEnter(shift, moreKeysQueued, elapsed))
                {
                    WriteLine();
                    return string.Join('\n', lines);
                }

                lines.Add(string.Empty);
                WriteLine();
                Write(InputMarker, CliTextStyle.Prompt);
                continue;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                EraseLastCharacter(lines, InputMarker.Length, InputMarker);
                continue;
            }

            if (char.IsControl(key.KeyChar))
            {
                continue;
            }

            lines[^1] += key.KeyChar;
            Write(key.KeyChar.ToString(), CliTextStyle.User);
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

    private static void EraseLastCharacter(List<string> lines, int inputColumn, string continuation)
    {
        var current = lines[^1];
        if (current.Length > 0)
        {
            var removed = current[^1];
            lines[^1] = current[..^1];
            var width = InteractiveConsoleInput.DisplayWidth(removed);
            Console.Write(new string('\b', width));
            Console.Write(new string(' ', width));
            Console.Write(new string('\b', width));
            return;
        }

        if (lines.Count == 1)
        {
            return;
        }

        lines.RemoveAt(lines.Count - 1);
        var previous = lines[^1];
        var startColumn = lines.Count == 1 ? inputColumn : continuation.Length;
        var column = startColumn + InteractiveConsoleInput.DisplayWidth(previous);
        var staleTop = Console.CursorTop;
        var targetTop = Math.Max(0, staleTop - 1);
        var maxColumn = Math.Max(0, Console.BufferWidth - 1);
        try
        {
            Console.SetCursorPosition(0, staleTop);
            Console.Write('\r');
            if (maxColumn > 0)
            {
                Console.Write(new string(' ', maxColumn));
                Console.Write('\r');
            }

            Console.SetCursorPosition(Math.Min(column, maxColumn), targetTop);
        }
        catch (ArgumentOutOfRangeException)
        {
            // 窗口滚动后原行号失效，留在当前光标继续输入。
        }
    }
}
