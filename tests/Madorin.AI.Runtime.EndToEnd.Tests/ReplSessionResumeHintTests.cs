using Madorin.AI.Runtime.Cli.Commands;
using Madorin.AI.Runtime.Cli.Config;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class ReplSessionResumeHintTests
{
    [TestMethod]
    public void Write_ShowsBothResumeCommandsInEnglish()
    {
        var terminal = new ProbeTerminal();

        ReplSessionResumeHint.Write(terminal, " sess-1 ");

        Assert.HasCount(1, terminal.Lines);
        Assert.AreEqual(
            "Session sess-1 can be resumed. In this program: /resume sess-1. After exit: madorin run --session sess-1",
            terminal.Lines[0]);
        Assert.AreEqual(CliTextStyle.Muted, terminal.Styles[0]);
    }

    [TestMethod]
    public void Write_BlankSession_WritesNothing()
    {
        var terminal = new ProbeTerminal();

        ReplSessionResumeHint.Write(terminal, "  ");

        Assert.IsEmpty(terminal.Lines);
    }

    private sealed class ProbeTerminal : ICliTerminal
    {
        public List<string> Lines { get; } = [];

        public List<CliTextStyle> Styles { get; } = [];

        public void Write(string value)
        {
        }

        public void Write(string value, CliTextStyle style)
        {
        }

        public void WriteLine(string value = "") => WriteLine(value, CliTextStyle.Default);

        public void WriteLine(string value, CliTextStyle style)
        {
            Lines.Add(value);
            Styles.Add(style);
        }

        public void Clear()
        {
        }

        public ValueTask<string?> ReadLineAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask<string> ReadSecretAsync(string prompt, CancellationToken ct = default) =>
            ValueTask.FromResult(string.Empty);
    }
}
