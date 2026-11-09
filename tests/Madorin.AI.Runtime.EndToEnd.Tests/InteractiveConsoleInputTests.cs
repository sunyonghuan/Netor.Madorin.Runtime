using Madorin.AI.Runtime.Cli.Commands;
using Madorin.AI.Runtime.Cli.Config;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class InteractiveConsoleInputTests
{
    [TestMethod]
    public void IsSubmitEnter_PlainEnter_Submits()
    {
        Assert.IsTrue(InteractiveConsoleInput.IsSubmitEnter(
            shift: false,
            moreKeysQueued: false,
            elapsedMilliseconds: InteractiveConsoleInput.PasteBurstMilliseconds));
    }

    [TestMethod]
    public void IsSubmitEnter_ShiftOrPaste_InsertsLineBreak()
    {
        Assert.IsFalse(InteractiveConsoleInput.IsSubmitEnter(
            shift: true,
            moreKeysQueued: false,
            elapsedMilliseconds: 1_000));
        Assert.IsFalse(InteractiveConsoleInput.IsSubmitEnter(
            shift: false,
            moreKeysQueued: true,
            elapsedMilliseconds: 1_000));
        Assert.IsFalse(InteractiveConsoleInput.IsSubmitEnter(
            shift: false,
            moreKeysQueued: false,
            elapsedMilliseconds: InteractiveConsoleInput.PasteBurstMilliseconds - 1));
    }

    [TestMethod]
    public void DisplayWidth_CountsChineseAsTwoColumns()
    {
        Assert.AreEqual(1, InteractiveConsoleInput.DisplayWidth("a"));
        Assert.AreEqual(4, InteractiveConsoleInput.DisplayWidth("你好"));
        Assert.AreEqual(0, InteractiveConsoleInput.DisplayWidth(string.Empty));
    }

    [TestMethod]
    public async Task TerminalStyleTextWriter_UsesAssistantStyle()
    {
        var terminal = new StyleProbeTerminal();
        await using var writer = new TerminalStyleTextWriter(
            terminal,
            CliTextStyle.Assistant,
            new StringWriter());

        await writer.WriteAsync("回复".AsMemory());

        Assert.AreEqual("回复", terminal.Value);
        Assert.AreEqual(CliTextStyle.Assistant, terminal.Style);
    }

    [TestMethod]
    public async Task TerminalStyleTextWriter_WritesReplyTitleOnce()
    {
        var terminal = new StyleProbeTerminal();
        var titles = 0;
        await using var writer = new TerminalStyleTextWriter(
            terminal,
            CliTextStyle.Assistant,
            new StringWriter(),
            () => titles++);

        await writer.WriteAsync("第一段".AsMemory());
        await writer.WriteAsync("第二段".AsMemory());

        Assert.AreEqual(1, titles);
        Assert.AreEqual("第一段第二段", terminal.Value);
    }

    private sealed class StyleProbeTerminal : ICliTerminal
    {
        public string Value { get; private set; } = string.Empty;

        public CliTextStyle Style { get; private set; }

        public void Write(string value) => Write(value, CliTextStyle.Default);

        public void Write(string value, CliTextStyle style)
        {
            Value += value;
            Style = style;
        }

        public void WriteLine(string value = "") => Write(value + Environment.NewLine);

        public void WriteLine(string value, CliTextStyle style) => Write(value + Environment.NewLine, style);

        public void Clear()
        {
        }

        public ValueTask<string?> ReadLineAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask<string> ReadSecretAsync(string prompt, CancellationToken ct = default) =>
            ValueTask.FromResult(string.Empty);
    }
}
