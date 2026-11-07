using Madorin.AI.Runtime.Cli.Commands;
using Madorin.AI.Runtime.Cli.Config;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class ReplThinkingStatusTests
{
    [TestMethod]
    public void Start_ShowsWorkingThenSwitchesToThinking()
    {
        var terminal = new ProbeTerminal();
        var now = 1_000L;
        var status = new ReplThinkingStatus(terminal, () => now);

        status.Start();
        now = 4_000;
        status.BeginThinking();

        Assert.HasCount(2, terminal.Writes);
        Assert.AreEqual("\rworking 0s", terminal.Writes[0].Text);
        Assert.AreEqual("\rthinking 3s", terminal.Writes[1].Text);
        Assert.AreEqual(CliTextStyle.Muted, terminal.Writes[1].Style);
    }

    [TestMethod]
    public void Finish_EndsTheLineWithoutErasingIt()
    {
        var terminal = new ProbeTerminal();
        var status = new ReplThinkingStatus(terminal, () => 0);

        status.Start();
        status.Finish();
        status.Finish();

        Assert.AreEqual(Environment.NewLine, terminal.Writes[1].Text);
        Assert.HasCount(2, terminal.Writes);
    }

    [TestMethod]
    public void BeginThinking_AfterReply_StartsOnANewLine()
    {
        var terminal = new ProbeTerminal();
        var status = new ReplThinkingStatus(terminal, () => 0);

        status.Start();
        status.Finish();
        status.BeginThinking();

        Assert.AreEqual("\rworking 0s", terminal.Writes[0].Text);
        Assert.AreEqual(Environment.NewLine, terminal.Writes[1].Text);
        Assert.AreEqual(Environment.NewLine, terminal.Writes[2].Text);
        Assert.AreEqual("\rthinking 0s", terminal.Writes[3].Text);
    }

    private sealed class ProbeTerminal : ICliTerminal
    {
        public List<(string Text, CliTextStyle Style)> Writes { get; } = [];

        public void Write(string value) => Write(value, CliTextStyle.Default);

        public void Write(string value, CliTextStyle style) => Writes.Add((value, style));

        public void WriteLine(string value = "") => Write(value + Environment.NewLine);

        public void WriteLine(string value, CliTextStyle style) =>
            Write(value + Environment.NewLine, style);

        public void Clear()
        {
        }

        public ValueTask<string?> ReadLineAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask<string> ReadSecretAsync(string prompt, CancellationToken ct = default) =>
            ValueTask.FromResult(string.Empty);
    }
}
