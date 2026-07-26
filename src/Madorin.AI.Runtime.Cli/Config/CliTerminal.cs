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

    /// <summary>Reads a secret without echoing it when attached to an interactive console.</summary>
    public ValueTask<string> ReadSecretAsync(string prompt, CancellationToken ct = default);
}

/// <summary>Default CLI terminal adapter.</summary>
public sealed class CliTerminal : ICliTerminal
{
    private static readonly object ColorSync = new();

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
        CliTextStyle.Parameter or CliTextStyle.Selected => ConsoleColor.White,
        CliTextStyle.Success => ConsoleColor.DarkGreen,
        CliTextStyle.Warning => ConsoleColor.DarkYellow,
        CliTextStyle.Error => ConsoleColor.DarkRed,
        CliTextStyle.Muted => ConsoleColor.DarkGray,
        _ => Console.ForegroundColor
    };
}
