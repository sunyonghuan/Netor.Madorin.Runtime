using System.Text;
using Madorin.AI.Runtime.Cli.Config;

namespace Madorin.AI.Runtime.Cli.Commands;

/// <summary>把一段终端正文写成同一种语义颜色，同时仍刷新原来的输出流。</summary>
internal sealed class TerminalStyleTextWriter : TextWriter
{
    private readonly TextWriter _output;
    private readonly CliTextStyle _style;
    private readonly ICliTerminal _terminal;
    private readonly Action? _beforeFirstText;
    private bool _started;

    public TerminalStyleTextWriter(
        ICliTerminal terminal,
        CliTextStyle style,
        TextWriter output,
        Action? beforeFirstText = null)
    {
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _style = style;
        _beforeFirstText = beforeFirstText;
    }

    public override Encoding Encoding => _output.Encoding;

    public override void Write(char value) => Write(value.ToString());

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (!_started)
        {
            _started = true;
            _beforeFirstText?.Invoke();
        }

        _terminal.Write(value, _style);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Write(new string(buffer, index, count));
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        if (!buffer.IsEmpty)
        {
            Write(buffer.ToString());
        }
    }

    public override void Flush() => _output.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken = default) =>
        _output.FlushAsync(cancellationToken);
}
