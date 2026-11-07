using Madorin.AI.Runtime.Cli.Commands;
using Madorin.AI.Runtime.Cli.Config;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class ReplThinkingStatusTests
{
    [TestMethod]
    public void Show_WritesElapsedStatusWithoutReasoningText()
    {
        var terminal = new ProbeTerminal();
        var now = 1_000L;
        var status = new ReplThinkingStatus(terminal, () => now);

        status.Show();
        now = 4_000;
        status.Show();
        status.Show();

        Assert.HasCount(2, terminal.Writes);
        Assert.AreEqual("\rthinking 0s", terminal.Writes[0].Text);
        Assert.AreEqual("\rthinking 3s", terminal.Writes[1].Text);
        Assert.AreEqual(CliTextStyle.Muted, terminal.Writes[1].Style);
        Assert.IsFalse(terminal.Writes.Any(write => write.Text.Contains("secret thought", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Clear_ErasesTheStatusLineOnce()
    {
        var terminal = new ProbeTerminal();
        var status = new ReplThinkingStatus(terminal, () => 0);

        status.Show();
        status.Clear();
        status.Clear();

        Assert.AreEqual("\r" + new string(' ', "thinking 0s".Length) + "\r", terminal.Writes[1].Text);
        Assert.HasCount(2, terminal.Writes);
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
